using System;
using System.Collections.Generic;
using System.Linq;
using VSOnDemandSymbols.Core.Abstractions;
using VSOnDemandSymbols.Core.Modules;
using VSOnDemandSymbols.Core.Policy;

namespace VSOnDemandSymbols.Core.Mru;

public sealed class MruStore
{
    private readonly object _lock = new();
    private readonly IClock _clock;
    private readonly Dictionary<ModuleKey, MruEntry> _entries = new();
    private readonly HashSet<ModuleKey> _hitThisSession = new();

    public MruOptions Options { get; set; }

    public MruStore(IClock clock, MruOptions? options = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Options = options ?? new MruOptions();
    }

    public IReadOnlyList<MruEntry> GetEntries()
    {
        lock (_lock)
        {
            return _entries.Values.ToList();
        }
    }

    public MruEntry? TryGetEntry(ModuleKey key)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(key, out var entry) ? entry : null;
        }
    }

    public void LoadEntries(IEnumerable<MruEntry> entries)
    {
        lock (_lock)
        {
            _entries.Clear();
            _hitThisSession.Clear();
            foreach (var entry in entries)
            {
                _entries[entry.Key] = entry;
            }
        }
    }

    public void RecordHit(ModuleKey key, TriggerReason reason, string? detail = null, IEnumerable<string>? binaries = null)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new MruEntry(key);
                _entries[key] = entry;
            }

            entry.LastTriggerUtc = _clock.UtcNow;
            entry.RunsSinceTrigger = 0;
            entry.LastReason = reason;
            entry.LastReasonDetail = detail;
            entry.TriggerCount++;

            if (binaries != null)
            {
                foreach (var b in binaries)
                {
                    if (!entry.Binaries.Contains(b, StringComparer.OrdinalIgnoreCase))
                    {
                        entry.Binaries.Add(b);
                    }
                }
            }

            _hitThisSession.Add(key);
        }
    }

    public IReadOnlyList<ModuleKey> StartSessionAndPrune()
    {
        lock (_lock)
        {
            _hitThisSession.Clear();

            var prunedKeys = new List<ModuleKey>();
            var now = _clock.UtcNow;

            var unpinned = _entries.Values.Where(e => !e.Pinned).ToList();

            foreach (var entry in unpinned)
            {
                bool pruneByRuns = entry.RunsSinceTrigger >= Options.MruMaxRuns;
                bool pruneByAge = (now - entry.LastTriggerUtc) >= Options.MruMaxAge;

                if (pruneByRuns || pruneByAge)
                {
                    prunedKeys.Add(entry.Key);
                    _entries.Remove(entry.Key);
                }
            }

            // Cap to MruMaxEntries: evict least recently triggered unpinned entries
            var remainingUnpinned = _entries.Values.Where(e => !e.Pinned).ToList();
            if (remainingUnpinned.Count > Options.MruMaxEntries)
            {
                var toEvict = remainingUnpinned
                    .OrderBy(e => e.LastTriggerUtc)
                    .Take(remainingUnpinned.Count - Options.MruMaxEntries);

                foreach (var entry in toEvict)
                {
                    prunedKeys.Add(entry.Key);
                    _entries.Remove(entry.Key);
                }
            }

            return prunedKeys;
        }
    }

    public void EndSession()
    {
        lock (_lock)
        {
            foreach (var entry in _entries.Values)
            {
                // Increment RunsSinceTrigger only for entries that had no hit this session
                if (!_hitThisSession.Contains(entry.Key))
                {
                    entry.RunsSinceTrigger++;
                }
            }

            _hitThisSession.Clear();
        }
    }

    public void Pin(ModuleKey key)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new MruEntry(key)
                {
                    LastTriggerUtc = _clock.UtcNow,
                    RunsSinceTrigger = 0
                };
                _entries[key] = entry;
            }
            entry.Pinned = true;
        }
    }

    public void Unpin(ModuleKey key)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                entry.Pinned = false;
            }
        }
    }

    public bool Forget(ModuleKey key)
    {
        lock (_lock)
        {
            return _entries.Remove(key);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            _hitThisSession.Clear();
        }
    }

    public IReadOnlyList<ModuleKey> GetWantedKeys()
    {
        lock (_lock)
        {
            var result = new List<ModuleKey>();
            foreach (var entry in _entries.Values)
            {
                if (entry.Pinned)
                {
                    result.Add(entry.Key);
                }
                else if (Options.MruEnabled)
                {
                    result.Add(entry.Key);
                }
            }
            return result;
        }
    }
}

