using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE80;
using Microsoft.VisualStudio.Debugger;
using Microsoft.VisualStudio.Debugger.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using VSOnDemandSymbols.Core.Abstractions;
using VSOnDemandSymbols.Core.Modules;
using VSOnDemandSymbols.Core.Policy;

namespace VSOnDemandSymbols.Debugger;

/// <summary>
/// De-duplicating symbol loader that coordinates native symbol loading
/// via Visual Studio symbol settings IncludeList, AD7 <see cref="IDebugModule3.LoadSymbols"/>,
/// and DTE <see cref="EnvDTE90.Module.LoadSymbols"/> (SPEC §8.2, ARCHITECTURE.md §3).
/// </summary>
public sealed class SymbolLoader
{
    private sealed class LoadRequest
    {
        public ModuleKey Key { get; }
        public TriggerReason Reason { get; }
        public string? Detail { get; }
        public IReadOnlyList<string>? CandidateFileNames { get; }

        public LoadRequest(
            ModuleKey key,
            TriggerReason reason,
            string? detail,
            IReadOnlyList<string>? candidateFileNames)
        {
            Key = key;
            Reason = reason;
            Detail = detail;
            CandidateFileNames = candidateFileNames;
        }
    }

    private readonly ConcordSymbolBridge _bridge;
    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<Guid, byte> _attemptedModuleInstanceIds = new();
    private readonly ConcurrentDictionary<string, byte> _attemptedFileNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IDebugModule2> _ad7ModulesByFileName = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<ModuleKey, byte> _queuedKeys = new();
    private readonly ConcurrentQueue<LoadRequest> _queue = new();
    private int _isProcessingQueue;

    public DTE2? Dte { get; set; }
    public SymbolSettingsController? SymbolSettingsController { get; set; }

    public SymbolLoader(ConcordSymbolBridge bridge, JoinableTaskFactory joinableTaskFactory, ILogger logger)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _joinableTaskFactory = joinableTaskFactory ?? throw new ArgumentNullException(nameof(joinableTaskFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void ResetForNewSession()
    {
        _attemptedModuleInstanceIds.Clear();
        _attemptedFileNames.Clear();
        _ad7ModulesByFileName.Clear();
        _queuedKeys.Clear();
        while (_queue.TryDequeue(out _))
        {
        }
    }

    /// <summary>
    /// Caches an AD7 <see cref="IDebugModule2"/> reference discovered during call-stack inspection
    /// so <see cref="IDebugModule3.LoadSymbols"/> can be invoked on demand (CS-1, CS-5).
    /// </summary>
    public void RegisterAd7Module(string fileName, IDebugModule2? ad7Module)
    {
        if (ad7Module == null)
        {
            return;
        }

        string clean = WildcardPattern.ExtractFileNameSafe(fileName);
        if (!string.IsNullOrWhiteSpace(clean))
        {
            _ad7ModulesByFileName[clean] = ad7Module;
        }
    }

    /// <summary>
    /// Queues an asynchronous on-demand symbol load request on a background thread with de-duplication per <see cref="ModuleKey"/>.
    /// </summary>
    public void EnqueueLoad(
        ModuleKey key,
        TriggerReason reason,
        string? detail = null,
        IReadOnlyList<string>? candidateFileNames = null)
    {
        var snapshot = _bridge.CurrentSnapshot;
        if (snapshot.Policy.ShouldLoadOnDemand(key, reason, candidateFileNames))
        {
            _bridge.AddWantedKeyLive(key, FormatReason(reason, detail), candidateFileNames);
        }

        if (reason != TriggerReason.Manual && !_queuedKeys.TryAdd(key, 1))
        {
            return;
        }

        _queue.Enqueue(new LoadRequest(key, reason, detail, candidateFileNames));
        if (Interlocked.CompareExchange(ref _isProcessingQueue, 1, 0) == 0)
        {
            _ = Task.Run(ProcessQueue);
        }
    }

    /// <summary>
    /// Immediately evaluates policy, adds the module key to the live wanted set, and loads symbols
    /// for all matching loaded modules. If the module is not loaded yet, adds its candidate file names
    /// to <c>IncludeList</c> so symbols load synchronously when the DLL maps (SPEC §4, §5.1 BP-2, §7.4).
    /// </summary>
    public bool LoadForModuleKeyNow(
        ModuleKey key,
        TriggerReason reason,
        string? detail = null,
        IReadOnlyList<string>? candidateFileNames = null)
    {
        try
        {
            var snapshot = _bridge.CurrentSnapshot;
            if (!snapshot.Policy.ShouldLoadOnDemand(key, reason, candidateFileNames))
            {
                _logger.Info($"Skipped symbol load for {key.Name} ({FormatReason(reason, detail)}): matched NeverLoadPatterns.");
                return false;
            }

            string reasonText = FormatReason(reason, detail);

            // Add to live wanted set so that if the module is not loaded yet (or hot-reloads/patches later),
            // OnModuleInstanceLoad recognizes it as wanted when mapped (SPEC §4, §7.4).
            _bridge.AddWantedKeyLive(key, reasonText, candidateFileNames);

            var loadedMatches = _bridge.ModuleRegistry.GetLoadedModulesForKey(key, candidateFileNames);
            if (loadedMatches.Count == 0)
            {
                // If triggered from a call stack or frame activation, the module is already mapped in the debuggee
                // even if Concord's registry didn't match it by key; load by candidate filename directly.
                if ((reason == TriggerReason.CallStack || reason == TriggerReason.FrameActivated || reason == TriggerReason.Manual) &&
                    candidateFileNames != null &&
                    candidateFileNames.Count > 0)
                {
                    string targetFile = WildcardPattern.ExtractFileNameSafe(candidateFileNames[0]);
                    if (!string.IsNullOrWhiteSpace(targetFile) &&
                        (reason == TriggerReason.Manual || _attemptedFileNames.TryAdd(targetFile, 1)))
                    {
                        _logger.Info($"Loading symbols: {targetFile} — {reasonText}");
                        TriggerNativeSymbolLoad( new[] { targetFile }, loadSymbolsNow: true);
                        return true;
                    }
                }

                // BP-2 / M4: Module is not loaded yet. Add its candidate binary names to IncludeList now
                // (with loadSymbolsNow: false) so VS loads its symbols synchronously when the DLL maps.
                if (candidateFileNames != null && candidateFileNames.Count > 0)
                {
                    TriggerNativeSymbolLoad(candidateFileNames, loadSymbolsNow: false);
                }

                _logger.Verbose($"Module '{key.Name}' is not loaded yet; added to wanted set and IncludeList ({reasonText}).");
                return false;
            }

            bool anyLoaded = false;
            foreach (var match in loadedMatches)
            {
                if (TryLoadModuleInstance(match.ModuleInstance, reason, reasonText))
                {
                    anyLoaded = true;
                }
            }

            return anyLoaded;
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to load symbols for module '{key}': {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>
    /// Loads symbols for a specific <see cref="DkmModuleInstance"/> with de-duplication and logging.
    /// Updates Visual Studio's IncludeList and triggers native symbol loading via AD7 / DTE.
    /// </summary>
    public bool TryLoadModuleInstance(
        DkmModuleInstance moduleInstance,
        TriggerReason reason,
        string reasonDescription)
    {
        if (moduleInstance == null)
        {
            return false;
        }

        try
        {
            if (moduleInstance.Module != null)
            {
                return true;
            }

            if (reason != TriggerReason.Manual &&
                (!_attemptedModuleInstanceIds.TryAdd(moduleInstance.UniqueId, 1) ||
                 !_bridge.TryMarkLoadAttempted(moduleInstance.UniqueId)))
            {
                return false;
            }

            _attemptedModuleInstanceIds[moduleInstance.UniqueId] = 1;
            _bridge.TryMarkLoadAttempted(moduleInstance.UniqueId);
            _bridge.MarkExtensionInitiatedLoad(moduleInstance.UniqueId);

            string fileName = WildcardPattern.ExtractFileNameSafe(moduleInstance.Name);
            if (string.IsNullOrEmpty(fileName))
            {
                fileName = WildcardPattern.ExtractFileNameSafe(moduleInstance.FullName);
            }

            if (!string.IsNullOrEmpty(fileName))
            {
                _attemptedFileNames[fileName] = 1;
            }

            _logger.Info($"Loading symbols: {fileName} — {reasonDescription}");

            // Trigger native symbol load via VS Symbol Settings IncludeList, AD7 IDebugModule3, and DTE Module.LoadSymbols
            TriggerNativeSymbolLoad(new[] { fileName }, loadSymbolsNow: true, moduleInstance);

            return true;
        }
        catch (Exception ex)
        {
            _bridge.RemoveExtensionInitiatedLoad(moduleInstance.UniqueId);
            _logger.Error($"Error initiating symbol load for '{moduleInstance.Name}'.", ex);
            return false;
        }
    }

    private void TriggerNativeSymbolLoad(
        IReadOnlyList<string> fileNames,
        bool loadSymbolsNow,
        DkmModuleInstance? dkmModule = null)
    {
        if (fileNames == null || fileNames.Count == 0)
        {
            return;
        }

        _ = _joinableTaskFactory.RunAsync(() => TriggerNativeSymbolLoadAsync(fileNames, loadSymbolsNow, dkmModule));
    }

    private async Task TriggerNativeSymbolLoadAsync(
        IReadOnlyList<string> fileNames,
        bool loadSymbolsNow,
        DkmModuleInstance? dkmModule)
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync();

        try
        {
            SymbolSettingsController?.AddModuleToIncludeList(fileNames, loadSymbolsNow: loadSymbolsNow);

            if (!loadSymbolsNow)
            {
                return;
            }

            string primaryFile = WildcardPattern.ExtractFileNameSafe(fileNames[0]);
            if (string.IsNullOrWhiteSpace(primaryFile))
            {
                return;
            }

            // Try AD7 IDebugModule3.LoadSymbols() if we have an AD7 reference (e.g. from call stack inspection)
            if (_ad7ModulesByFileName.TryGetValue(primaryFile, out IDebugModule2? ad7Mod) &&
                ad7Mod is IDebugModule3 ad7Mod3)
            {
                try
                {
                    ad7Mod3.LoadSymbols();
                }
                catch
                {
                    // Fall through to DTE Module.LoadSymbols
                }
            }

            if (Dte?.Debugger?.DebuggedProcesses != null)
            {
                foreach (EnvDTE.Process proc in Dte.Debugger.DebuggedProcesses)
                {
                    if (proc is EnvDTE90.Process3 proc3 && proc3.Modules != null)
                    {
                        foreach (EnvDTE90.Module mod in proc3.Modules)
                        {
                            string modName = WildcardPattern.ExtractFileNameSafe(mod.Name);
                            if (fileNames.Any(f => string.Equals(WildcardPattern.ExtractFileNameSafe(f), modName, StringComparison.OrdinalIgnoreCase)))
                            {
                                mod.LoadSymbols(string.Empty);
                            }
                        }
                    }
                }
            }

            if (dkmModule != null && dkmModule.Module == null && !HasAd7Symbols(primaryFile))
            {
                _logger.Warning($"Symbols not found for {primaryFile} (searched: {ConcordSymbolBridge.GetSymbolSearchDetails(dkmModule)})");
            }
        }
        catch (Exception ex)
        {
            _logger.Verbose($"Native symbol load invocation for '{string.Join(", ", fileNames)}' resulted in: {ex.Message}");
        }
    }

    private bool HasAd7Symbols(string fileName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!_ad7ModulesByFileName.TryGetValue(fileName, out IDebugModule2? ad7Mod) || ad7Mod == null)
        {
            return false;
        }

        try
        {
            var info = new MODULE_INFO[1];
            if (ad7Mod.GetInfo(enum_MODULE_INFO_FIELDS.MIF_FLAGS, info) == 0)
            {
                return (info[0].m_dwModuleFlags & enum_MODULE_FLAGS.MODULE_FLAG_SYMBOLS) != 0;
            }
        }
        catch
        {
            // Ignore AD7 query errors
        }

        return false;
    }

    public static string FormatReason(TriggerReason reason, string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return reason.ToString();
        }

        return $"{reason}: {detail}";
    }

    private void ProcessQueue()
    {
        try
        {
            while (true)
            {
                while (_queue.TryDequeue(out LoadRequest? request))
                {
                    _queuedKeys.TryRemove(request.Key, out _);
                    LoadForModuleKeyNow(request.Key, request.Reason, request.Detail, request.CandidateFileNames);
                }

                Interlocked.Exchange(ref _isProcessingQueue, 0);

                if (_queue.IsEmpty || Interlocked.CompareExchange(ref _isProcessingQueue, 1, 0) != 0)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _isProcessingQueue, 0);
            _logger.Error("Unhandled error in SymbolLoader background queue.", ex);
        }
    }
}
