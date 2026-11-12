using System;
using System.Collections.Generic;
using VSOnDemandSymbols.Core.Abstractions;
using VSOnDemandSymbols.Core.State;
using Xunit;

namespace VSOnDemandSymbols.Core.Tests.State;

public class HiveCoordinatorTests
{
    private readonly InMemoryFileSystem _fs = new();
    private readonly TestClock _clock = new(new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void GetHiveDirectoryPath_SanitizesInvalidChars()
    {
        string dir = HiveCoordinator.GetHiveDirectoryPath(@"C:\Users\Dev\AppData\Local", "17.0_1a2b:3c/Exp*");
        Assert.Equal(@"C:\Users\Dev\AppData\Local\OnDemandSymbols\17.0_1a2b_3c_Exp_", dir);
    }

    [Fact]
    public void Baseline_SaveLoadDelete_RoundTripsAtomically()
    {
        var coordinator = new HiveCoordinator(_fs, NullLogger.Instance);
        string hiveDir = @"C:\AppData\OnDemandSymbols\17.0_Exp";

        var baseline = new BaselineDto
        {
            ActiveSinceUtc = _clock.UtcNow,
            Snapshot = new SymbolSettingsSnapshot
            {
                IsManualLoad = false,
                IsLoadAdjacent = true,
                AutomaticSymbolLoad = true,
                IncludeList = new List<SymbolOptionBackupDto>
                {
                    new() { Name = "UserModule.dll", IsEnabled = true }
                }
            }
        };

        coordinator.SaveBaseline(hiveDir, baseline);
        var loaded = coordinator.TryLoadBaseline(hiveDir);

        Assert.NotNull(loaded);
        Assert.Equal(_clock.UtcNow, loaded!.ActiveSinceUtc);
        Assert.Single(loaded.Snapshot.IncludeList);
        Assert.Equal("UserModule.dll", loaded.Snapshot.IncludeList[0].Name);

        coordinator.DeleteBaseline(hiveDir);
        Assert.Null(coordinator.TryLoadBaseline(hiveDir));
    }

    [Fact]
    public void Lease_AcquireUpdateAndDispose_ManagesFileLock()
    {
        var coordinator = new HiveCoordinator(_fs, NullLogger.Instance);
        string hiveDir = @"C:\AppData\OnDemandSymbols\17.0_Exp";

        var lease = coordinator.TryAcquireLease(
            hiveDir,
            processId: 1000,
            wantedBinaries: new[] { "UnrealEditor*.exe" },
            serverMaskActive: false,
            _clock);

        Assert.NotNull(lease);

        // While lease is held, TryDeleteFile should fail (sharing violation simulated in InMemoryFileSystem)
        string leasePath = @"C:\AppData\OnDemandSymbols\17.0_Exp\leases\1000.json";
        Assert.True(_fs.FileExists(leasePath));
        Assert.False(_fs.TryDeleteFile(leasePath));

        // Update lease content
        lease!.Update(HiveCoordinator.SerializeLeaseContent(1000, new[] { "UnrealEditor*.exe", "UnrealEditor-MyGame*.dll" }, true, _clock));

        // Disposing lease releases lock and deletes file
        lease.Dispose();
        Assert.False(_fs.FileExists(leasePath));
    }

    [Fact]
    public void PruneAndListLiveLeases_KeepsLockedLeaseAndPrunesDeadLease()
    {
        var coordinator = new HiveCoordinator(_fs, NullLogger.Instance);
        string hiveDir = @"C:\AppData\OnDemandSymbols\17.0_Exp";

        // PID 1000 is an active debug session holding an open lease
        var liveLeaseHandle = coordinator.TryAcquireLease(
            hiveDir,
            processId: 1000,
            wantedBinaries: new[] { "UnrealEditor*.exe" },
            serverMaskActive: false,
            _clock);
        Assert.NotNull(liveLeaseHandle);

        // PID 2000 is an unlocked (dead/orphaned) lease left behind by a crashed process
        string deadLeasePath = @"C:\AppData\OnDemandSymbols\17.0_Exp\leases\2000.json";
        _fs.WriteAllText(deadLeasePath, HiveCoordinator.SerializeLeaseContent(2000, new[] { "Dead.dll" }, serverMaskActive: true, _clock));

        var live = coordinator.PruneAndListLiveLeases(hiveDir, out bool anyPrunedServerMaskActive);

        // PID 1000 should be listed as live
        Assert.Single(live);
        Assert.Equal(1000, live[0].ProcessId);

        // PID 2000 was pruned and flagged server mask recovery
        Assert.True(anyPrunedServerMaskActive);
        Assert.False(_fs.FileExists(deadLeasePath));

        liveLeaseHandle!.Dispose();
    }

    [Fact]
    public void ComputeEffectiveSettings_NoLiveLeases_RestoresBaseline()
    {
        var coordinator = new HiveCoordinator(_fs, NullLogger.Instance);
        var baseline = new BaselineDto
        {
            Snapshot = new SymbolSettingsSnapshot
            {
                IsManualLoad = false,
                IsLoadAdjacent = true,
                AutomaticSymbolLoad = true,
                UseMsSymbolServers = true,
                IncludeList = new List<SymbolOptionBackupDto>
                {
                    new() { Name = "Base.dll", IsEnabled = true }
                }
            }
        };

        var effective = coordinator.ComputeEffectiveSettings(baseline, Array.Empty<SessionLeaseDto>());

        Assert.False(effective.IsManualLoad);
        Assert.True(effective.IsLoadAdjacent);
        Assert.True(effective.AutomaticSymbolLoad);
        Assert.Single(effective.IncludeList);
        Assert.Equal("Base.dll", effective.IncludeList[0].Name);
    }

    [Fact]
    public void ComputeEffectiveSettings_MultipleUnrealLeases_MergesWantedBinaries()
    {
        var coordinator = new HiveCoordinator(_fs, NullLogger.Instance);
        var baseline = new BaselineDto
        {
            Snapshot = new SymbolSettingsSnapshot
            {
                IsManualLoad = false,
                IncludeList = new List<SymbolOptionBackupDto>
                {
                    new() { Name = "Base.dll", IsEnabled = true }
                }
            }
        };

        var leases = new List<SessionLeaseDto>
        {
            new()
            {
                ProcessId = 1000,
                WantedBinaries = new List<string> { "UnrealEditor*.exe", "UnrealEditor-ProjectA*.dll" }
            },
            new()
            {
                ProcessId = 2000,
                WantedBinaries = new List<string> { "UnrealEditor*.exe", "UnrealEditor-ProjectB*.dll" }
            }
        };

        var effective = coordinator.ComputeEffectiveSettings(baseline, leases);

        Assert.True(effective.IsManualLoad);
        Assert.False(effective.IsLoadAdjacent);
        Assert.False(effective.AutomaticSymbolLoad);

        // Base.dll + UnrealEditor*.exe (deduped) + ProjectA + ProjectB = 4
        Assert.Equal(4, effective.IncludeList.Count);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var opt in effective.IncludeList)
        {
            names.Add(opt.Name);
        }

        Assert.Contains("Base.dll", names);
        Assert.Contains("UnrealEditor*.exe", names);
        Assert.Contains("UnrealEditor-ProjectA*.dll", names);
        Assert.Contains("UnrealEditor-ProjectB*.dll", names);
    }

    [Fact]
    public void MigrateLegacyBackupIfNeeded_MigratesAndDeletesLegacyFile()
    {
        var coordinator = new HiveCoordinator(_fs, NullLogger.Instance);
        string hiveDir = @"C:\AppData\OnDemandSymbols\17.0_Exp";

        // Save legacy backup
        var oldStore = new SymbolSettingsBackupStore(_fs, _clock, NullLogger.Instance);
        string legacyPath = SymbolSettingsBackupStore.GetBackupFilePath(@"C:\AppData", "17.0_Exp");
        oldStore.SaveBackup(
            legacyPath,
            isManualLoad: false,
            isLoadAdjacent: true,
            automaticSymbolLoad: true,
            processId: 1000,
            includeList: new[]
            {
                new SymbolOptionBackupDto { Name = "LegacyModule.dll", IsEnabled = true }
            });

        Assert.True(_fs.FileExists(legacyPath));

        bool migrated = coordinator.MigrateLegacyBackupIfNeeded(hiveDir, _clock);
        Assert.True(migrated);
        Assert.False(_fs.FileExists(legacyPath));

        var baseline = coordinator.TryLoadBaseline(hiveDir);
        Assert.NotNull(baseline);
        Assert.Single(baseline!.Snapshot.IncludeList);
        Assert.Equal("LegacyModule.dll", baseline.Snapshot.IncludeList[0].Name);
    }
}

