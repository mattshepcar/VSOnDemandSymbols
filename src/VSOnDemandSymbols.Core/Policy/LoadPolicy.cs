using System;
using System.Collections.Generic;
using System.Linq;
using VSOnDemandSymbols.Core.Modules;

namespace VSOnDemandSymbols.Core.Policy;

public sealed class LoadPolicy
{
    public const int DefaultCallStackMaxFrames = 128;

    public IReadOnlyList<WildcardPattern> AlwaysLoadPatterns { get; set; } = Array.Empty<WildcardPattern>();
    public IReadOnlyList<WildcardPattern> NeverLoadPatterns { get; set; } = Array.Empty<WildcardPattern>();

    /// <summary>
    /// Whether breakpoints trigger symbol loads (SPEC §5.1, §8.5). Default: <c>true</c>.
    /// </summary>
    public bool LoadOnBreakpoints { get; set; } = true;

    /// <summary>
    /// Whether disabled breakpoints are included when resolving breakpoints (SPEC §5.1 BP-3, §8.5). Default: <c>false</c>.
    /// </summary>
    public bool IncludeDisabledBreakpoints { get; set; } = false;

    /// <summary>
    /// Which threads' call stacks are examined when execution breaks (SPEC §5.2 CS-2, §8.5).
    /// Default: <see cref="CallStackMode.CurrentThreadAllThreadsOnException"/>.
    /// </summary>
    public CallStackMode CallStackMode { get; set; } = CallStackMode.CurrentThreadAllThreadsOnException;

    /// <summary>
    /// Maximum number of stack frames examined per thread (SPEC §5.2 CS-3, §8.5). Default: 128.
    /// </summary>
    public int CallStackMaxFrames { get; set; } = DefaultCallStackMaxFrames;

    /// <summary>
    /// Whether frames in modules outside the solution (Windows, CRT, drivers, third-party) load symbols on break/activation
    /// (SPEC §5.2 CS-4, §8.5). Default: <c>true</c>.
    /// </summary>
    public bool CallStackIncludeExternalModules { get; set; } = true;

    /// <summary>
    /// Whether activating a stack frame (double-clicking in Call Stack / Threads / Parallel Stacks or switching threads)
    /// loads symbols for that frame and its unexamined thread (SPEC §5.3 FA-1, FA-2, §8.5). Default: <c>true</c>.
    /// </summary>
    public bool LoadOnFrameActivation { get; set; } = true;

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

    /// <summary>
    /// Returns true if any call-stack frames should be examined on break (SPEC §5.2 CS-1, CS-2).
    /// </summary>
    public bool ShouldExamineCallStackOnBreak()
    {
        return CallStackMode != CallStackMode.None && CallStackMaxFrames > 0;
    }

    /// <summary>
    /// Returns true if all threads (rather than only the current thread) should be examined for the given stop reason (SPEC §5.2 CS-2).
    /// </summary>
    public bool ShouldExamineAllThreadsOnBreak(bool isExceptionOrHardBreak)
    {
        return CallStackMode switch
        {
            CallStackMode.AllThreads => true,
            CallStackMode.CurrentThreadAllThreadsOnException => isExceptionOrHardBreak,
            _ => false
        };
    }

    /// <summary>
    /// Evaluates whether a module on the call stack should load symbols on break (SPEC §5.2 CS-1, CS-4, §5.7).
    /// </summary>
    public bool ShouldLoadForCallStackModule(ModuleKey key, IEnumerable<string>? candidateBinaries = null)
    {
        if (CallStackMode == CallStackMode.None)
        {
            return false;
        }

        if (key.Kind == ModuleKind.External && !CallStackIncludeExternalModules)
        {
            return false;
        }

        return ShouldLoadOnDemand(key, TriggerReason.CallStack, candidateBinaries);
    }

    /// <summary>
    /// Evaluates whether a module should load symbols when its stack frame or thread is activated (SPEC §5.3 FA-1, FA-2, §5.2 CS-4, §5.7).
    /// </summary>
    public bool ShouldLoadForFrameActivation(ModuleKey key, IEnumerable<string>? candidateBinaries = null)
    {
        if (!LoadOnFrameActivation)
        {
            return false;
        }

        if (key.Kind == ModuleKind.External && !CallStackIncludeExternalModules)
        {
            return false;
        }

        return ShouldLoadOnDemand(key, TriggerReason.FrameActivated, candidateBinaries);
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
        IEnumerable<ModuleKey> breakpointKeys,
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

        if (LoadOnBreakpoints && breakpointKeys != null)
        {
            foreach (var k in breakpointKeys) wanted.Add(k);
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
