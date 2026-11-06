using System;
using System.Collections.Generic;
using VSOnDemandSymbols.Core.Modules;

namespace VSOnDemandSymbols.Core.Policy;

/// <summary>
/// Represents a symbol load decision produced by <see cref="CallStackEvaluator"/> (SPEC §5.2, §5.3).
/// </summary>
public sealed class CallStackTriggerDecision
{
    public ModuleKey Key { get; }
    public TriggerReason Reason { get; }
    public string Detail { get; }
    public IReadOnlyList<string> CandidateFileNames { get; }

    public CallStackTriggerDecision(
        ModuleKey key,
        TriggerReason reason,
        string detail,
        IReadOnlyList<string> candidateFileNames)
    {
        Key = key;
        Reason = reason;
        Detail = detail ?? throw new ArgumentNullException(nameof(detail));
        CandidateFileNames = candidateFileNames ?? throw new ArgumentNullException(nameof(candidateFileNames));
    }
}

