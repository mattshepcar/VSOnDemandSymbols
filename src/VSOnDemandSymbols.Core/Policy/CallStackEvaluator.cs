using System;
using System.Collections.Generic;
using VSOnDemandSymbols.Core.Modules;

namespace VSOnDemandSymbols.Core.Policy;

/// <summary>
/// Evaluates call-stack frames on break (SPEC §5.2 CS-1–CS-6) and frame activation (SPEC §5.3 FA-1–FA-2)
/// according to <see cref="LoadPolicy"/>.
/// </summary>
public sealed class CallStackEvaluator
{
    private readonly LoadPolicy _policy;

    public CallStackEvaluator(LoadPolicy policy)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    /// <summary>
    /// Examines the threads selected by <see cref="LoadPolicy.CallStackMode"/> when the debugger enters break mode
    /// (CS-1, CS-2, CS-3, CS-4). Records examined thread IDs in <paramref name="examinedThreadIds"/> for FA-2.
    /// </summary>
    public IReadOnlyList<CallStackTriggerDecision> EvaluateBreak(
        IReadOnlyList<CallStackThreadInput> threads,
        bool isExceptionOrHardBreak,
        ISet<uint>? examinedThreadIds,
        Func<string, ModuleKey?> resolveLoadedModule,
        Func<ModuleKey, IReadOnlyList<string>>? getCandidateBinaries = null)
    {
        if (threads == null || threads.Count == 0 || !_policy.ShouldExamineCallStackOnBreak())
        {
            return Array.Empty<CallStackTriggerDecision>();
        }

        if (resolveLoadedModule == null)
        {
            throw new ArgumentNullException(nameof(resolveLoadedModule));
        }

        bool examineAllThreads = _policy.ShouldExamineAllThreadsOnBreak(isExceptionOrHardBreak);
        int maxFrames = Math.Max(0, _policy.CallStackMaxFrames);
        var decisions = new List<CallStackTriggerDecision>();
        var seenKeys = new HashSet<ModuleKey>();

        foreach (var thread in threads)
        {
            if (!examineAllThreads && !thread.IsCurrentThread)
            {
                continue;
            }

            examinedThreadIds?.Add(thread.ThreadId);
            ExamineThreadFrames(
                thread.Frames,
                maxFrames,
                TriggerReason.CallStack,
                resolveLoadedModule,
                getCandidateBinaries,
                seenKeys,
                decisions);
        }

        return decisions;
    }

    /// <summary>
    /// Evaluates frame activation (SPEC §5.3 FA-1, FA-2):
    /// 1. Loads symbols for <paramref name="activatedFrame"/> if its module has no symbols (FA-1).
    /// 2. If <paramref name="activatedThread"/> has not yet been examined during this break pause
    ///    (not in <paramref name="examinedThreadIds"/>), examines the whole stack of that thread up to
    ///    <see cref="LoadPolicy.CallStackMaxFrames"/> (FA-2).
    /// </summary>
    public IReadOnlyList<CallStackTriggerDecision> EvaluateFrameActivation(
        CallStackFrameInput? activatedFrame,
        CallStackThreadInput? activatedThread,
        ISet<uint>? examinedThreadIds,
        Func<string, ModuleKey?> resolveLoadedModule,
        Func<ModuleKey, IReadOnlyList<string>>? getCandidateBinaries = null)
    {
        if (!_policy.LoadOnFrameActivation)
        {
            return Array.Empty<CallStackTriggerDecision>();
        }

        if (resolveLoadedModule == null)
        {
            throw new ArgumentNullException(nameof(resolveLoadedModule));
        }

        var decisions = new List<CallStackTriggerDecision>();
        var seenKeys = new HashSet<ModuleKey>();

        // FA-1: Evaluate the specifically activated frame first
        if (activatedFrame.HasValue)
        {
            TryAddFrameDecision(
                activatedFrame.Value,
                TriggerReason.FrameActivated,
                resolveLoadedModule,
                getCandidateBinaries,
                seenKeys,
                decisions);
        }

        // FA-2: If the activated thread's stack hasn't been examined yet, examine the whole stack of that thread
        if (activatedThread != null)
        {
            bool isUnexaminedThread = examinedThreadIds == null || examinedThreadIds.Add(activatedThread.ThreadId);
            if (isUnexaminedThread)
            {
                int maxFrames = Math.Max(0, _policy.CallStackMaxFrames);
                ExamineThreadFrames(
                    activatedThread.Frames,
                    maxFrames,
                    TriggerReason.FrameActivated,
                    resolveLoadedModule,
                    getCandidateBinaries,
                    seenKeys,
                    decisions);
            }
        }

        return decisions;
    }

    public static string FormatFrameDetail(string moduleFileName, string? functionName)
    {
        string cleanModule = WildcardPattern.ExtractFileNameSafe(moduleFileName);
        if (string.IsNullOrWhiteSpace(functionName))
        {
            return cleanModule;
        }

        string trimmedFunc = functionName!.Trim();
        if (trimmedFunc.StartsWith(cleanModule + "!", StringComparison.OrdinalIgnoreCase))
        {
            return trimmedFunc;
        }

        return $"{cleanModule}!{trimmedFunc}";
    }

    private void ExamineThreadFrames(
        IReadOnlyList<CallStackFrameInput> frames,
        int maxFrames,
        TriggerReason reason,
        Func<string, ModuleKey?> resolveLoadedModule,
        Func<ModuleKey, IReadOnlyList<string>>? getCandidateBinaries,
        HashSet<ModuleKey> seenKeys,
        List<CallStackTriggerDecision> decisions)
    {
        int limit = Math.Min(frames.Count, maxFrames);
        for (int i = 0; i < limit; i++)
        {
            TryAddFrameDecision(
                frames[i],
                reason,
                resolveLoadedModule,
                getCandidateBinaries,
                seenKeys,
                decisions);
        }
    }

    private void TryAddFrameDecision(
        CallStackFrameInput frame,
        TriggerReason reason,
        Func<string, ModuleKey?> resolveLoadedModule,
        Func<ModuleKey, IReadOnlyList<string>>? getCandidateBinaries,
        HashSet<ModuleKey> seenKeys,
        List<CallStackTriggerDecision> decisions)
    {
        if (frame.HasSymbolsLoaded)
        {
            return;
        }

        string fileName = frame.ModuleFileName;
        if (string.IsNullOrWhiteSpace(fileName) || IsPseudoModuleName(fileName))
        {
            return;
        }

        ModuleKey key = resolveLoadedModule(fileName) ?? new ModuleKey(ModuleKind.External, fileName);
        if (seenKeys.Contains(key))
        {
            return;
        }

        var candidates = BuildCandidateList(key, fileName, getCandidateBinaries);
        bool allowed = reason == TriggerReason.FrameActivated
            ? _policy.ShouldLoadForFrameActivation(key, candidates)
            : _policy.ShouldLoadForCallStackModule(key, candidates);

        if (!allowed)
        {
            return;
        }

        seenKeys.Add(key);
        string detail = FormatFrameDetail(fileName, frame.FunctionName);
        decisions.Add(new CallStackTriggerDecision(key, reason, detail, candidates));
    }

    private static IReadOnlyList<string> BuildCandidateList(
        ModuleKey key,
        string loadedFileName,
        Func<ModuleKey, IReadOnlyList<string>>? getCandidateBinaries)
    {
        var list = new List<string> { loadedFileName };
        if (getCandidateBinaries != null)
        {
            var resolved = getCandidateBinaries(key);
            if (resolved != null)
            {
                foreach (string candidate in resolved)
                {
                    string clean = WildcardPattern.ExtractFileNameSafe(candidate);
                    if (!string.IsNullOrWhiteSpace(clean) &&
                        !string.Equals(clean, loadedFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        list.Add(clean);
                    }
                }
            }
        }

        return list;
    }

    private static bool IsPseudoModuleName(string fileName)
    {
        if (fileName.StartsWith("[", StringComparison.Ordinal) ||
            string.Equals(fileName, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}

