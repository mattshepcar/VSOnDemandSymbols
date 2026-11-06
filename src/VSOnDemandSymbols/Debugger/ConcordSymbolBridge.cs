using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Microsoft.VisualStudio.Debugger;
using VSOnDemandSymbols.Core.Abstractions;
using VSOnDemandSymbols.Core.Modules;

namespace VSOnDemandSymbols.Debugger;

/// <summary>
/// Thread-safe in-process bridge between the VSIX package and <see cref="ConcordSymbolMonitor"/>
/// (ARCHITECTURE.md §3).
/// </summary>
public sealed class ConcordSymbolBridge
{
    public static readonly ConcordSymbolBridge Instance = new();

    private readonly object _snapshotLock = new();
    private readonly ConcurrentDictionary<Guid, byte> _extensionInitiatedLoads = new();
    private readonly ConcurrentDictionary<Guid, byte> _attemptedLoadModuleIds = new();

    private WantedSetSnapshot _currentSnapshot = WantedSetSnapshot.Empty;

    public WantedSetSnapshot CurrentSnapshot => Volatile.Read(ref _currentSnapshot);
    public LoadedModuleRegistry ModuleRegistry { get; } = new();
    public ILogger Logger { get; set; } = NullLogger.Instance;
    public SessionTracker? SessionTracker { get; set; }
    public SymbolLoader? SymbolLoader { get; set; }

    public void PublishSnapshot(WantedSetSnapshot snapshot)
    {
        if (snapshot == null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        lock (_snapshotLock)
        {
            Interlocked.Exchange(ref _currentSnapshot, snapshot);
        }
    }

    public WantedSetSnapshot AddWantedKeyLive(
        ModuleKey key,
        string reasonDescription,
        IEnumerable<string>? candidateFileNames = null)
    {
        lock (_snapshotLock)
        {
            var updated = _currentSnapshot.WithAddedWantedKey(key, reasonDescription, candidateFileNames);
            Interlocked.Exchange(ref _currentSnapshot, updated);
            return updated;
        }
    }

    public void MarkExtensionInitiatedLoad(Guid moduleUniqueId)
    {
        _extensionInitiatedLoads[moduleUniqueId] = 1;
    }

    public bool ConsumeExtensionInitiatedLoad(Guid moduleUniqueId)
    {
        return _extensionInitiatedLoads.TryRemove(moduleUniqueId, out _);
    }

    public void RemoveExtensionInitiatedLoad(Guid moduleUniqueId)
    {
        _extensionInitiatedLoads.TryRemove(moduleUniqueId, out _);
    }

    public bool TryMarkLoadAttempted(Guid moduleUniqueId)
    {
        return _attemptedLoadModuleIds.TryAdd(moduleUniqueId, 1);
    }

    public void ResetForNewSession()
    {
        _extensionInitiatedLoads.Clear();
        _attemptedLoadModuleIds.Clear();
        ModuleRegistry.ResetSessionCountersAndClear();
    }

    public void Clear()
    {
        _extensionInitiatedLoads.Clear();
        _attemptedLoadModuleIds.Clear();
        ModuleRegistry.ResetSessionCountersAndClear();
        PublishSnapshot(WantedSetSnapshot.Empty);
    }

    public static string GetSymbolSearchDetails(DkmModuleInstance moduleInstance)
    {
        if (moduleInstance == null)
        {
            return "unknown";
        }

        try
        {
            string? status = moduleInstance.GetSymbolStatusMessage(false);
            if (!string.IsNullOrWhiteSpace(status))
            {
                return status!.Trim().Replace("\r\n", "; ").Replace("\n", "; ");
            }
        }
        catch
        {
            // Fall back to module path
        }

        return moduleInstance.FullName ?? moduleInstance.Name ?? "symbol search path";
    }
}

