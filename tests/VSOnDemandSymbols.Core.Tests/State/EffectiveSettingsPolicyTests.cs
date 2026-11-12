using System;
using System.Collections.Generic;
using VSOnDemandSymbols.Core.State;
using Xunit;

namespace VSOnDemandSymbols.Core.Tests.State;

public class EffectiveSettingsPolicyTests
{
    private static BaselineDto CreateSampleBaseline()
    {
        return new BaselineDto
        {
            State = BaselineState.Active,
            ActiveSinceUtc = new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc),
            Snapshot = new SymbolSettingsSnapshot
            {
                IsManualLoad = false,
                IsLoadAdjacent = true,
                AutomaticSymbolLoad = true,
                UseMsSymbolServers = true,
                UseNugetSymbolServers = false,
                SymbolPaths = new List<SymbolOptionBackupDto>
                {
                    new() { Name = @"C:\Symbols", IsEnabled = true }
                },
                IncludeList = new List<SymbolOptionBackupDto>
                {
                    new() { Name = "UserModule.dll", IsEnabled = true }
                }
            }
        };
    }

    [Fact]
    public void Compute_NullBaseline_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            EffectiveSettingsPolicy.Compute(null!, Array.Empty<SessionLeaseDto>()));
    }

    [Fact]
    public void Compute_NoLiveLeases_RestoresBaselineAndRequestsRelease()
    {
        var baseline = CreateSampleBaseline();
        var decision = EffectiveSettingsPolicy.Compute(baseline, Array.Empty<SessionLeaseDto>());

        Assert.True(decision.ShouldReleaseBaseline);
        Assert.False(decision.RestoreServerSettings);
        Assert.False(decision.EffectiveSettings.IsManualLoad);
        Assert.True(decision.EffectiveSettings.IsLoadAdjacent);
        Assert.True(decision.EffectiveSettings.AutomaticSymbolLoad);
        Assert.Single(decision.EffectiveSettings.IncludeList);
        Assert.Equal("UserModule.dll", decision.EffectiveSettings.IncludeList[0].Name);
    }

    [Fact]
    public void Compute_NoLiveLeasesWithServerRecovery_SetsRestoreServerSettings()
    {
        var baseline = CreateSampleBaseline();
        var decision = EffectiveSettingsPolicy.Compute(baseline, Array.Empty<SessionLeaseDto>(), recoverServerSettings: true);

        Assert.True(decision.ShouldReleaseBaseline);
        Assert.True(decision.RestoreServerSettings);
        Assert.True(decision.EffectiveSettings.UseMsSymbolServers);
    }

    [Fact]
    public void Compute_SingleUnrealLease_AppliesManualModeAndMergesWantedBinaries()
    {
        var baseline = CreateSampleBaseline();
        var leases = new List<SessionLeaseDto>
        {
            new()
            {
                ProcessId = 1000,
                Kind = LeaseKind.Unreal,
                WantedBinaries = new List<string> { "UnrealEditor*.exe", "UnrealEditor-MyGame*.dll" }
            }
        };

        var decision = EffectiveSettingsPolicy.Compute(baseline, leases);

        Assert.False(decision.ShouldReleaseBaseline);
        Assert.True(decision.EffectiveSettings.IsManualLoad);
        Assert.False(decision.EffectiveSettings.IsLoadAdjacent);
        Assert.False(decision.EffectiveSettings.AutomaticSymbolLoad);

        // IncludeList should contain UserModule.dll from baseline + 2 wanted patterns
        Assert.Equal(3, decision.EffectiveSettings.IncludeList.Count);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var opt in decision.EffectiveSettings.IncludeList)
        {
            names.Add(opt.Name);
            Assert.True(opt.IsEnabled);
        }

        Assert.Contains("UserModule.dll", names);
        Assert.Contains("UnrealEditor*.exe", names);
        Assert.Contains("UnrealEditor-MyGame*.dll", names);
    }

    [Fact]
    public void Compute_MultipleUnrealLeases_MergesWantedBinariesFromAllLeases()
    {
        var baseline = CreateSampleBaseline();
        var leases = new List<SessionLeaseDto>
        {
            new()
            {
                ProcessId = 1000,
                Kind = LeaseKind.Unreal,
                WantedBinaries = new List<string> { "UnrealEditor*.exe", "UnrealEditor-ProjectA*.dll" }
            },
            new()
            {
                ProcessId = 2000,
                Kind = LeaseKind.Unreal,
                WantedBinaries = new List<string> { "UnrealEditor*.exe", "UnrealEditor-ProjectB*.dll" }
            }
        };

        var decision = EffectiveSettingsPolicy.Compute(baseline, leases);

        Assert.False(decision.ShouldReleaseBaseline);
        Assert.True(decision.EffectiveSettings.IsManualLoad);

        // 1 baseline (UserModule.dll) + 3 unique wanted patterns (UnrealEditor*.exe deduped, ProjectA, ProjectB)
        Assert.Equal(4, decision.EffectiveSettings.IncludeList.Count);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var opt in decision.EffectiveSettings.IncludeList)
        {
            names.Add(opt.Name);
        }

        Assert.Contains("UserModule.dll", names);
        Assert.Contains("UnrealEditor*.exe", names);
        Assert.Contains("UnrealEditor-ProjectA*.dll", names);
        Assert.Contains("UnrealEditor-ProjectB*.dll", names);
    }

    [Fact]
    public void Compute_UnrealAndBaselineLeases_PriorityRuleP1_ForcesBaseline()
    {
        var baseline = CreateSampleBaseline();
        var leases = new List<SessionLeaseDto>
        {
            new()
            {
                ProcessId = 1000,
                Kind = LeaseKind.Unreal,
                WantedBinaries = new List<string> { "UnrealEditor*.exe" }
            },
            new()
            {
                ProcessId = 2000,
                Kind = LeaseKind.Baseline
            }
        };

        var decision = EffectiveSettingsPolicy.Compute(baseline, leases);

        // Priority rule P1: correctness beats speed. A non-Unreal target is debugging.
        Assert.False(decision.ShouldReleaseBaseline);
        Assert.False(decision.EffectiveSettings.IsManualLoad);
        Assert.True(decision.EffectiveSettings.IsLoadAdjacent);
        Assert.True(decision.EffectiveSettings.AutomaticSymbolLoad);
        Assert.Single(decision.EffectiveSettings.IncludeList);
        Assert.Equal("UserModule.dll", decision.EffectiveSettings.IncludeList[0].Name);
        Assert.Contains("P1", decision.DecisionReason);
    }
}
