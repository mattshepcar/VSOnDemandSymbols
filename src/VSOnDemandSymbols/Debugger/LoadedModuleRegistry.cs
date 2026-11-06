using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.VisualStudio.Debugger;
using VSOnDemandSymbols.Core.Modules;
using VSOnDemandSymbols.Core.Policy;

namespace VSOnDemandSymbols.Debugger;

/// <summary>
/// Thread-safe registry of loaded native modules (<see cref="DkmModuleInstance"/>) across active debug processes
/// (ARCHITECTURE.md §3).
/// </summary>
public sealed class LoadedModuleRegistry
{
    private readonly ConcurrentDictionary<Guid, LoadedModuleInfo> _byUniqueId = new();
    private readonly ConcurrentDictionary<Guid, byte> _countedStartupSymbolModuleIds = new();
    private readonly ConcurrentDictionary<Guid, byte> _countedOnDemandSymbolModuleIds = new();

    private int _totalModulesLoadedCount;
    private int _symbolsLoadedAtLoadCount;
    private int _symbolsLoadedOnDemandCount;

    public int TotalModulesLoadedCount => Volatile.Read(ref _totalModulesLoadedCount);
    public int SymbolsLoadedAtLoadCount => Volatile.Read(ref _symbolsLoadedAtLoadCount);
    public int SymbolsLoadedOnDemandCount => Volatile.Read(ref _symbolsLoadedOnDemandCount);
    public int TotalSymbolsLoadedCount => SymbolsLoadedAtLoadCount + SymbolsLoadedOnDemandCount;

    public LoadedModuleInfo RegisterModuleLoad(DkmModuleInstance moduleInstance, ModuleKey? key, string? resolvedFileName = null)
    {
        if (moduleInstance == null)
        {
            throw new ArgumentNullException(nameof(moduleInstance));
        }

        string fileName = !string.IsNullOrEmpty(resolvedFileName)
            ? resolvedFileName!
            : WildcardPattern.ExtractFileNameSafe(moduleInstance.Name);
        string fullPath = WildcardPattern.SanitizePathString(moduleInstance.FullName);
        if (string.IsNullOrEmpty(fullPath))
        {
            fullPath = fileName;
        }

        var info = new LoadedModuleInfo(moduleInstance, fileName, fullPath, key);

        if (_byUniqueId.TryAdd(moduleInstance.UniqueId, info))
        {
            Interlocked.Increment(ref _totalModulesLoadedCount);
        }
        else
        {
            _byUniqueId[moduleInstance.UniqueId] = info;
        }

        if (info.HadSymbolsAtModuleMap)
        {
            RecordStartupSymbolLoad(moduleInstance.UniqueId);
        }

        return info;
    }

    public bool TryGetByUniqueId(Guid uniqueId, out LoadedModuleInfo? info)
    {
        return _byUniqueId.TryGetValue(uniqueId, out info);
    }

    public void RegisterModuleUnload(DkmModuleInstance moduleInstance)
    {
        if (moduleInstance == null)
        {
            return;
        }

        _byUniqueId.TryRemove(moduleInstance.UniqueId, out _);
    }

    public void RecordStartupSymbolLoad(Guid moduleUniqueId)
    {
        if (_byUniqueId.TryGetValue(moduleUniqueId, out var info))
        {
            info.LoadedAtStartup = true;
        }

        if (_countedStartupSymbolModuleIds.TryAdd(moduleUniqueId, 1))
        {
            Interlocked.Increment(ref _symbolsLoadedAtLoadCount);
        }
    }

    public void RecordOnDemandSymbolLoad(Guid moduleUniqueId)
    {
        if (_byUniqueId.TryGetValue(moduleUniqueId, out var info))
        {
            info.LoadedOnDemand = true;
        }

        if (!_countedStartupSymbolModuleIds.ContainsKey(moduleUniqueId) &&
            _countedOnDemandSymbolModuleIds.TryAdd(moduleUniqueId, 1))
        {
            Interlocked.Increment(ref _symbolsLoadedOnDemandCount);
        }
    }

    public IReadOnlyList<LoadedModuleInfo> GetLoadedModulesForKey(ModuleKey key, IEnumerable<string>? candidateFileNames = null)
    {
        HashSet<string>? candidateSet = null;
        if (candidateFileNames != null)
        {
            candidateSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string fn in candidateFileNames)
            {
                string cleanFn = WildcardPattern.ExtractFileNameSafe(fn);
                if (!string.IsNullOrWhiteSpace(cleanFn))
                {
                    candidateSet.Add(cleanFn);
                }
            }
        }

        var matches = new List<LoadedModuleInfo>();
        foreach (var entry in _byUniqueId.Values)
        {
            if (entry.Key.HasValue && entry.Key.Value.Equals(key))
            {
                matches.Add(entry);
                continue;
            }

            if (candidateSet != null && candidateSet.Contains(entry.FileName))
            {
                matches.Add(entry);
                continue;
            }

            if ((key.Kind == ModuleKind.MsBuild || key.Kind == ModuleKind.External) &&
                string.Equals(entry.FileName, key.Name, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(entry);
            }
        }

        return matches;
    }

    public IReadOnlyList<LoadedModuleInfo> GetLoadedModulesByFileName(string fileName)
    {
        string shortName = WildcardPattern.ExtractFileNameSafe(fileName);
        var matches = new List<LoadedModuleInfo>();
        foreach (var entry in _byUniqueId.Values)
        {
            if (string.Equals(entry.FileName, shortName, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(entry);
            }
        }

        return matches;
    }

    public IReadOnlyCollection<LoadedModuleInfo> GetAllLoadedModules()
    {
        return new List<LoadedModuleInfo>(_byUniqueId.Values);
    }

    public void RemoveProcess(Guid processUniqueId)
    {
        foreach (var kvp in _byUniqueId)
        {
            if (kvp.Value.ProcessUniqueId == processUniqueId)
            {
                _byUniqueId.TryRemove(kvp.Key, out _);
            }
        }
    }

    public void ResetSessionCountersAndClear()
    {
        _byUniqueId.Clear();
        _countedStartupSymbolModuleIds.Clear();
        _countedOnDemandSymbolModuleIds.Clear();
        Interlocked.Exchange(ref _totalModulesLoadedCount, 0);
        Interlocked.Exchange(ref _symbolsLoadedAtLoadCount, 0);
        Interlocked.Exchange(ref _symbolsLoadedOnDemandCount, 0);
    }
}

