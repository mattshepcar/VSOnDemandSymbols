using System;
using System.Collections.Generic;
using VSOnDemandSymbols.Core.Abstractions;
using VSOnDemandSymbols.Core.State;
using Xunit;

namespace VSOnDemandSymbols.Core.Tests.State;

public class CoordinationStoreTests
{
    private sealed class TestProcessInfo : IProcessInfo
    {
        public int CurrentProcessId { get; set; } = 1234;
        public DateTime CurrentProcessStartTimeUtc { get; set; } = new(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);

        public Dictionary<int, (bool IsAlive, DateTime StartTimeUtc)> Processes { get; } = new();

        public bool IsDevenvProcessAlive(int pid, DateTime expectedStartTimeUtc)
        {
            if (Processes.TryGetValue(pid, out var info))
            {
                if (!info.IsAlive) return false;
                if (expectedStartTimeUtc != DateTime.MinValue)
                {
                    TimeSpan diff = info.StartTimeUtc - expectedStartTimeUtc;
                    if (Math.Abs(diff.TotalSeconds) > 2.0)
                    {
                        return false;
                    }
                }
                return true;
            }
            return false;
        }
    }

    private readonly InMemoryFileSystem _fs = new();
    private readonly TestClock _clock = new(new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc));
    private readonly TestProcessInfo _processInfo = new();

    [Fact]
    public void GetHiveDirectoryPath_SanitizesInvalidChars()
    {
        string dir = CoordinationStore.GetHiveDirectoryPath(@"C:\Users\Dev\AppData\Local", "17.0_1a2b:3c/Exp*");
        Assert.Equal(@"C:\Users\Dev\AppData\Local\OnDemandSymbols\17.0_1a2b_3c_Exp_", dir);
    }

    [Fact]
    public void Baseline_SaveLoadDelete_RoundTripsAtomically()
    {
        var store = new CoordinationStore(_fs, NullLogger.Instance);
        string hiveDir = @"C:\AppData\OnDemandSymbols\17.0_Exp";

        var baseline = new BaselineDto
        {
            State = BaselineState.Active,
            ActiveSinceUtc = _clock.UtcNow,
            Snapshot = new SymbolSettingsSnapshot
            {
                IsManualLoad = false,
                IncludeList = new List<SymbolOptionBackupDto>
                {
                    new() { Name = "Existing.dll", IsEnabled = true }
                }
            }
        };

        store.SaveBaseline(hiveDir, baseline);
        var loaded = store.TryLoadBaseline(hiveDir);

        Assert.NotNull(loaded);
        Assert.Equal(BaselineState.Active, loaded!.State);
        Assert.Equal(_clock.UtcNow, loaded.ActiveSinceUtc);
        Assert.Single(loaded.Snapshot.IncludeList);
        Assert.Equal("Existing.dll", loaded.Snapshot.IncludeList[0].Name);

        store.DeleteBaseline(hiveDir);
        Assert.Null(store.TryLoadBaseline(hiveDir));
    }

    [Fact]
    public void Lease_SaveLoadDelete_RoundTripsAtomically()
    {
        var store = new CoordinationStore(_fs, NullLogger.Instance);
        string hiveDir = @"C:\AppData\OnDemandSymbols\17.0_Exp";

        var lease = new SessionLeaseDto
        {
            ProcessId = 5555,
            ProcessStartTimeUtc = _clock.UtcNow,
            Kind = LeaseKind.Unreal,
            WantedBinaries = new List<string> { "UnrealEditor*.exe" },
            ServerMaskActive = true,
            UpdatedUtc = _clock.UtcNow
        };

        store.SaveLease(hiveDir, lease);
        var loaded = store.TryLoadLease(hiveDir, 5555);

        Assert.NotNull(loaded);
        Assert.Equal(5555, loaded!.ProcessId);
        Assert.True(loaded.ServerMaskActive);
        Assert.Single(loaded.WantedBinaries);

        store.DeleteLease(hiveDir, 5555);
        Assert.Null(store.TryLoadLease(hiveDir, 5555));
    }

    [Fact]
    public void PruneAndListLiveLeases_KeepsAliveAndPrunesDeadLeases()
    {
        var store = new CoordinationStore(_fs, NullLogger.Instance);
        string hiveDir = @"C:\AppData\OnDemandSymbols\17.0_Exp";

        var startTime1 = new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);
        var startTime2 = new DateTime(2026, 11, 12, 10, 5, 0, DateTimeKind.Utc);

        // PID 1000 is alive
        _processInfo.Processes[1000] = (IsAlive: true, StartTimeUtc: startTime1);
        store.SaveLease(hiveDir, new SessionLeaseDto
        {
            ProcessId = 1000,
            ProcessStartTimeUtc = startTime1,
            Kind = LeaseKind.Unreal
        });

        // PID 2000 is dead
        _processInfo.Processes[2000] = (IsAlive: false, StartTimeUtc: startTime2);
        store.SaveLease(hiveDir, new SessionLeaseDto
        {
            ProcessId = 2000,
            ProcessStartTimeUtc = startTime2,
            Kind = LeaseKind.Unreal,
            ServerMaskActive = true
        });

        var live = store.PruneAndListLiveLeases(hiveDir, _processInfo, out bool anyPrunedServerMaskActive);

        Assert.Single(live);
        Assert.Equal(1000, live[0].ProcessId);
        Assert.True(anyPrunedServerMaskActive); // PID 2000 was pruned with ServerMaskActive=true

        // Verify dead lease file was deleted from disk
        Assert.Null(store.TryLoadLease(hiveDir, 2000));
        Assert.NotNull(store.TryLoadLease(hiveDir, 1000));
    }

    [Fact]
    public void PruneAndListLiveLeases_PidReuseStartTimeMismatch_PrunesStaleLease()
    {
        var store = new CoordinationStore(_fs, NullLogger.Instance);
        string hiveDir = @"C:\AppData\OnDemandSymbols\17.0_Exp";

        var originalStartTime = new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);
        var reusedProcessStartTime = new DateTime(2026, 11, 12, 11, 30, 0, DateTimeKind.Utc);

        // PID 3000 exists now, but it's a new process with a completely different start time
        _processInfo.Processes[3000] = (IsAlive: true, StartTimeUtc: reusedProcessStartTime);

        store.SaveLease(hiveDir, new SessionLeaseDto
        {
            ProcessId = 3000,
            ProcessStartTimeUtc = originalStartTime,
            Kind = LeaseKind.Unreal
        });

        var live = store.PruneAndListLiveLeases(hiveDir, _processInfo, out bool anyPrunedServerMaskActive);

        Assert.Empty(live);
        Assert.False(anyPrunedServerMaskActive);
        Assert.Null(store.TryLoadLease(hiveDir, 3000));
    }

    [Fact]
    public void DirtyWindows_RecordsAndCapsAt20Entries()
    {
        var store = new CoordinationStore(_fs, NullLogger.Instance);
        string hiveDir = @"C:\AppData\OnDemandSymbols\17.0_Exp";

        for (int i = 1; i <= 25; i++)
        {
            var start = new DateTime(2026, 11, 12, 10, i, 0, DateTimeKind.Utc);
            var end = start.AddMinutes(5);
            store.RecordDirtyWindow(hiveDir, start, end, new SymbolSettingsSnapshot
            {
                IsManualLoad = false,
                IncludeList = new List<SymbolOptionBackupDto>
                {
                    new() { Name = $"Window{i}.dll", IsEnabled = true }
                }
            });
        }

        var log = store.LoadDirtyWindows(hiveDir);
        Assert.Equal(DirtyWindowLogDto.MaxEntries, log.Windows.Count); // Capped at 20
        Assert.Equal("Window6.dll", log.Windows[0].RestoredBaseline.IncludeList[0].Name); // 1-5 evicted
        Assert.Equal("Window25.dll", log.Windows[19].RestoredBaseline.IncludeList[0].Name);
    }

    [Fact]
    public void FindOverlappingDirtyWindow_MatchesHistoricalAndActiveBaseline()
    {
        var store = new CoordinationStore(_fs, NullLogger.Instance);
        string hiveDir = @"C:\AppData\OnDemandSymbols\17.0_Exp";

        var t1 = new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 11, 12, 10, 30, 0, DateTimeKind.Utc);

        store.RecordDirtyWindow(hiveDir, t1, t2, new SymbolSettingsSnapshot
        {
            IsManualLoad = false,
            IncludeList = new List<SymbolOptionBackupDto>
            {
                new() { Name = "HistoricalBaseline.dll", IsEnabled = true }
            }
        });

        // Process started inside historical dirty window
        var snap = store.FindOverlappingDirtyWindow(hiveDir, new DateTime(2026, 11, 12, 10, 15, 0, DateTimeKind.Utc));
        Assert.NotNull(snap);
        Assert.Equal("HistoricalBaseline.dll", snap!.IncludeList[0].Name);

        // Process started outside dirty window
        var outsideSnap = store.FindOverlappingDirtyWindow(hiveDir, new DateTime(2026, 11, 12, 11, 0, 0, DateTimeKind.Utc));
        Assert.Null(outsideSnap);

        // Active baseline test
        store.SaveBaseline(hiveDir, new BaselineDto
        {
            State = BaselineState.Active,
            ActiveSinceUtc = new DateTime(2026, 11, 12, 11, 30, 0, DateTimeKind.Utc),
            Snapshot = new SymbolSettingsSnapshot
            {
                IncludeList = new List<SymbolOptionBackupDto>
                {
                    new() { Name = "ActiveBaseline.dll", IsEnabled = true }
                }
            }
        });

        var activeSnap = store.FindOverlappingDirtyWindow(hiveDir, new DateTime(2026, 11, 12, 11, 45, 0, DateTimeKind.Utc));
        Assert.NotNull(activeSnap);
        Assert.Equal("ActiveBaseline.dll", activeSnap!.IncludeList[0].Name);
    }

    [Fact]
    public void MigrateLegacyBackupIfNeeded_MigratesAndDeletesLegacyFile()
    {
        var store = new CoordinationStore(_fs, NullLogger.Instance);
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

        // Migrate
        bool migrated = store.MigrateLegacyBackupIfNeeded(hiveDir, _clock);
        Assert.True(migrated);
        Assert.False(_fs.FileExists(legacyPath)); // Legacy deleted

        var baseline = store.TryLoadBaseline(hiveDir);
        Assert.NotNull(baseline);
        Assert.Equal(BaselineState.Active, baseline!.State);
        Assert.Single(baseline.Snapshot.IncludeList);
        Assert.Equal("LegacyModule.dll", baseline.Snapshot.IncludeList[0].Name);
    }
}
