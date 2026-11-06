using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Debugger;
using VSOnDemandSymbols.Core.Abstractions;
using VSOnDemandSymbols.Core.Modules;
using VSOnDemandSymbols.Core.Policy;

namespace VSOnDemandSymbols.Debugger;

/// <summary>
/// De-duplicating symbol loader that calls <see cref="DkmModuleInstance.TryLoadSymbols()"/>
/// and logs every symbol load with its trigger reason (SPEC §8.2, ARCHITECTURE.md §3).
/// Never attempts to load the same <see cref="DkmModuleInstance"/> twice per session for automatic triggers.
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
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<Guid, byte> _attemptedModuleInstanceIds = new();
    private readonly ConcurrentDictionary<ModuleKey, byte> _queuedKeys = new();
    private readonly ConcurrentQueue<LoadRequest> _queue = new();
    private int _isProcessingQueue;

    public SymbolLoader(ConcordSymbolBridge bridge, ILogger logger)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
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

            bool logDiag = _logger.IsEnabled(LogLevel.Diagnostic);
            Stopwatch? sw = logDiag ? Stopwatch.StartNew() : null;

            try
            {
                moduleInstance.TryLoadSymbols();
            }
            catch (DkmException dkmEx)
            {
                _logger.Verbose($"TryLoadSymbols returned {dkmEx.Code} for {fileName}: {dkmEx.Message}");
            }

            if (sw != null)
            {
                sw.Stop();
            }

            if (moduleInstance.Module != null)
            {
                _bridge.ModuleRegistry.RecordOnDemandSymbolLoad(moduleInstance.UniqueId);
                if (sw != null)
                {
                    _logger.Diagnostic($"TryLoadSymbols succeeded for '{fileName}' in {sw.Elapsed.TotalMilliseconds:F2} ms.");
                }

                return true;
            }

            _bridge.RemoveExtensionInitiatedLoad(moduleInstance.UniqueId);
            string searchDetails = ConcordSymbolBridge.GetSymbolSearchDetails(moduleInstance);
            _logger.Info($"Symbols not found for {fileName} (searched: {searchDetails})");
            return false;
        }
        catch (Exception ex)
        {
            _bridge.RemoveExtensionInitiatedLoad(moduleInstance.UniqueId);
            _logger.Error($"Error calling TryLoadSymbols for '{moduleInstance.Name}'.", ex);
            return false;
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

