using System;
using System.Collections.Generic;
using VSOnDemandSymbols.Core.Policy;

namespace VSOnDemandSymbols.Core.State;

public sealed class PolicyDecision
{
    public SymbolSettingsSnapshot EffectiveSettings { get; }
    public bool ShouldReleaseBaseline { get; }
    public bool RestoreServerSettings { get; }
    public string DecisionReason { get; }

    public PolicyDecision(
        SymbolSettingsSnapshot effectiveSettings,
        bool shouldReleaseBaseline,
        bool restoreServerSettings,
        string decisionReason)
    {
        EffectiveSettings = effectiveSettings ?? throw new ArgumentNullException(nameof(effectiveSettings));
        ShouldReleaseBaseline = shouldReleaseBaseline;
        RestoreServerSettings = restoreServerSettings;
        DecisionReason = decisionReason ?? string.Empty;
    }
}

/// <summary>
/// Pure decision policy that computes the effective symbol settings to write to Visual Studio (ADR-16).
/// Takes the baseline snapshot and all live leases across the VS hive.
/// </summary>
public static class EffectiveSettingsPolicy
{
    public static PolicyDecision Compute(
        BaselineDto baseline,
        IReadOnlyList<SessionLeaseDto>? liveLeases,
        bool recoverServerSettings = false)
    {
        if (baseline == null)
        {
            throw new ArgumentNullException(nameof(baseline));
        }

        var baselineSnapshot = baseline.Snapshot ?? new SymbolSettingsSnapshot();

        // Case 1: No live leases remain -> restore baseline and mark for release.
        if (liveLeases == null || liveLeases.Count == 0)
        {
            var restored = baselineSnapshot.Clone();
            return new PolicyDecision(
                effectiveSettings: restored,
                shouldReleaseBaseline: true,
                restoreServerSettings: recoverServerSettings,
                decisionReason: "No active leases remain; restoring baseline.");
        }

        // Case 2: Priority Rule P1 (Correctness beats speed).
        // If ANY instance is debugging a non-Unreal target, the hive must be in baseline mode
        // so that non-Unreal debuggers get standard symbol loading.
        for (int i = 0; i < liveLeases.Count; i++)
        {
            var lease = liveLeases[i];
            if (lease.Kind == LeaseKind.Baseline)
            {
                var forcedBaseline = baselineSnapshot.Clone();
                return new PolicyDecision(
                    effectiveSettings: forcedBaseline,
                    shouldReleaseBaseline: false,
                    restoreServerSettings: recoverServerSettings,
                    decisionReason: $"Baseline lease active for PID {lease.ProcessId}; forcing baseline settings (P1).");
            }
        }

        // Case 3: Priority Rule P2 (Unreal sessions merge).
        // Only Unreal leases exist. Put hive into manual-load mode and merge wanted binaries
        // from all active Unreal leases on top of the baseline IncludeList.
        var merged = baselineSnapshot.Clone();
        merged.IsManualLoad = true;
        merged.IsLoadAdjacent = false;
        merged.AutomaticSymbolLoad = false;

        var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var opt in merged.IncludeList)
        {
            if (!string.IsNullOrWhiteSpace(opt.Name))
            {
                existingNames.Add(opt.Name);
            }
        }

        int addedCount = 0;
        foreach (var lease in liveLeases)
        {
            if (lease.WantedBinaries != null)
            {
                foreach (string binary in lease.WantedBinaries)
                {
                    string clean = WildcardPattern.ExtractFileNameSafe(binary);
                    if (!string.IsNullOrWhiteSpace(clean) && existingNames.Add(clean))
                    {
                        merged.IncludeList.Add(new SymbolOptionBackupDto
                        {
                            Name = clean,
                            IsEnabled = true
                        });
                        addedCount++;
                    }
                }
            }
        }

        return new PolicyDecision(
            effectiveSettings: merged,
            shouldReleaseBaseline: false,
            restoreServerSettings: recoverServerSettings,
            decisionReason: $"Manual mode active for {liveLeases.Count} Unreal lease(s); merged {addedCount} wanted pattern(s).");
    }
}
