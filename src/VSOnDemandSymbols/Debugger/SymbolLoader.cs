using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE80;
using Microsoft.VisualStudio.Debugger;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using VSOnDemandSymbols.Core.Abstractions;
using VSOnDemandSymbols.Core.Modules;
using VSOnDemandSymbols.Core.Policy;

namespace VSOnDemandSymbols.Debugger;

/// <summary>
/// De-duplicating symbol loader that coordinates native symbol loading
/// via Visual Studio symbol settings IncludeList and DTE Module.LoadSymbols (SPEC §8.2, ARCHITECTURE.md §3).
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
        _queuedKeys.Clear();
        while (_queue.TryDequeue(out _))
        {
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
    /// for all matching loaded <see cref="DkmModuleInstance"/>s via <see cref="DkmModuleInstance.TryLoadSymbols()"/>.
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
            // OnModuleInstanceLoad loads its symbols when mapped (SPEC §4, §7.4).
            _bridge.AddWantedKeyLive(key, reasonText, candidateFileNames);

            var loadedMatches = _bridge.ModuleRegistry.GetLoadedModulesForKey(key, candidateFileNames);
            if (loadedMatches.Count == 0)
            {
                _logger.Verbose($"Module '{key.Name}' is not loaded yet; added to wanted set ({reasonText}).");
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

            _logger.Info($"Loading symbols: {fileName} — {reasonDescription}");

            // Trigger native symbol load via VS Symbol Settings IncludeList and DTE Module.LoadSymbols
            TriggerNativeSymbolLoad(fileName);

            return true;
        }
        catch (Exception ex)
        {
            _bridge.RemoveExtensionInitiatedLoad(moduleInstance.UniqueId);
            _logger.Error($"Error initiating symbol load for '{moduleInstance.Name}'.", ex);
            return false;
        }
    }

    private void TriggerNativeSymbolLoad(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return;
        }

        _ = _joinableTaskFactory.RunAsync(() => TriggerNativeSymbolLoadAsync(fileName));
    }

    private async Task TriggerNativeSymbolLoadAsync(string fileName)
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync();

        try
        {
            SymbolSettingsController?.AddModuleToIncludeList(new[] { fileName }, loadSymbolsNow: true);

            if (Dte?.Debugger?.DebuggedProcesses != null)
            {
                foreach (EnvDTE.Process proc in Dte.Debugger.DebuggedProcesses)
                {
                    if (proc is EnvDTE90.Process3 proc3 && proc3.Modules != null)
                    {
                        foreach (EnvDTE90.Module mod in proc3.Modules)
                        {
                            string modName = WildcardPattern.ExtractFileNameSafe(mod.Name);
                            if (string.Equals(modName, fileName, StringComparison.OrdinalIgnoreCase))
                            {
                                mod.LoadSymbols(string.Empty);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Verbose($"Native symbol load invocation for '{fileName}' resulted in: {ex.Message}");
        }
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

