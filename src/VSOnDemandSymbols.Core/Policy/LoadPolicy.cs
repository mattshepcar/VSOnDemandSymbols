using System;
using System.Collections.Generic;
using System.Linq;
using VSOnDemandSymbols.Core.Modules;

namespace VSOnDemandSymbols.Core.Policy;

public sealed class LoadPolicy
{
    public IReadOnlyList<WildcardPattern> AlwaysLoadPatterns { get; set; } = Array.Empty<WildcardPattern>();
    public IReadOnlyList<WildcardPattern> NeverLoadPatterns { get; set; } = Array.Empty<WildcardPattern>();

    public LoadPolicy(
        IEnumerable<WildcardPattern>? alwaysLoadPatterns = null,
        IEnumerable<WildcardPattern>? neverLoadPatterns = null)
    {
        if (alwaysLoadPatterns != null)
        {
            AlwaysLoadPatterns = alwaysLoadPatterns.ToList();
        }
        if (neverLoadPatterns != null)
        {
            NeverLoadPatterns = neverLoadPatterns.ToList();
        }
    }

    public bool IsNeverLoad(string fileName)
    {
        return WildcardPattern.MatchesAnyFileName(NeverLoadPatterns, fileName);
    }

    public bool IsNeverLoad(ModuleKey key, IEnumerable<string>? candidateBinaries = null)
    {
        return WildcardPattern.MatchesAnyModule(NeverLoadPatterns, key, candidateBinaries);
    }

    public bool IsAlwaysLoad(string fileName)
    {
        return WildcardPattern.MatchesAnyFileName(AlwaysLoadPatterns, fileName);
    }

    public bool IsAlwaysLoad(ModuleKey key, IEnumerable<string>? candidateBinaries = null)
    {
        return WildcardPattern.MatchesAnyModule(AlwaysLoadPatterns, key, candidateBinaries);
    }

    public bool ShouldLoadOnDemand(ModuleKey key, TriggerReason reason, IEnumerable<string>? candidateBinaries = null)
    {
        // SPEC §5.7: "Never-load always wins over automatic triggers. It does not block explicit user actions (Modules window, 'Load Symbols Now' command)."
        if (reason == TriggerReason.Manual)
        {
            return true;
        }

        if (IsNeverLoad(key, candidateBinaries))
        {
            return false;
        }

        return true;
    }

    public bool ShouldLoadAtModuleLoad(
        string fileName,
        ModuleKey? key,
        bool isKeyInWantedSet,
        IEnumerable<string>? candidateBinaries = null)
    {
        // 1. Check never-load on filename and key
        if (IsNeverLoad(fileName))
        {
            return false;
        }

        if (key.HasValue && IsNeverLoad(key.Value, candidateBinaries))
        {
            return false;
        }

        // 2. Check always-load on filename and key
        if (IsAlwaysLoad(fileName))
        {
            return true;
        }

        if (key.HasValue && IsAlwaysLoad(key.Value, candidateBinaries))
        {
            return true;
        }

        // 3. Wanted set match
        return isKeyInWantedSet;
    }

    public HashSet<ModuleKey> ComputeWantedSet(
        IEnumerable<ModuleKey> pinnedKeys,
        IEnumerable<ModuleKey> mruKeys,
        IEnumerable<ModuleKey> breakpointKeys,
        IEnumerable<ModuleKey> recentlyBuiltKeys,
        bool recentlyBuiltGuardTripped,
        IEnumerable<ModuleKey>? sessionTriggeredKeys = null,
        Func<ModuleKey, IReadOnlyList<string>>? getCandidateBinaries = null)
    {
        var wanted = new HashSet<ModuleKey>();

        // Always-load ue:Module patterns
        foreach (var p in AlwaysLoadPatterns)
        {
            if (p.IsUnrealModulePattern && !p.Pattern.Contains('*') && !p.Pattern.Contains('?'))
            {
                wanted.Add(new ModuleKey(ModuleKind.Unreal, p.Pattern));
            }
        }

        if (pinnedKeys != null)
        {
            foreach (var k in pinnedKeys) wanted.Add(k);
        }

        if (mruKeys != null)
        {
            foreach (var k in mruKeys) wanted.Add(k);
        }

        if (breakpointKeys != null)
        {
            foreach (var k in breakpointKeys) wanted.Add(k);
        }

        if (!recentlyBuiltGuardTripped && recentlyBuiltKeys != null)
        {
            foreach (var k in recentlyBuiltKeys) wanted.Add(k);
        }

        if (sessionTriggeredKeys != null)
        {
            foreach (var k in sessionTriggeredKeys) wanted.Add(k);
        }

        // Subtract NeverLoad patterns
        if (NeverLoadPatterns.Count > 0)
        {
            wanted.RemoveWhere(k => IsNeverLoad(k, getCandidateBinaries?.Invoke(k)));
        }

        return wanted;
    }
}

