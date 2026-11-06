using System;
using VSOnDemandSymbols.Core.Abstractions;
using VSOnDemandSymbols.Core.State;
using Xunit;

namespace VSOnDemandSymbols.Core.Tests.State;

public class SymbolSettingsBackupStoreTests
{
    private readonly InMemoryFileSystem _fs = new();
    private readonly TestClock _clock = new(new DateTime(2026, 11, 5, 20, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void GetBackupFilePath_IncludesInstanceIdAndSanitizesPath()
    {
        string path = SymbolSettingsBackupStore.GetBackupFilePath(@"C:\Users\Dev\AppData\Local", "18.0_1a2b3c4d_Exp");
        Assert.Equal(
            @"C:\Users\Dev\AppData\Local\OnDemandSymbols\18.0_1a2b3c4d_Exp\symbol-settings-backup.json",
            path);
    }

    [Fact]
    public void SaveLoadDelete_RoundTrip_PreservesFlagsAndPid()
    {
        var store = new SymbolSettingsBackupStore(_fs, _clock, NullLogger.Instance);
        string backupPath = SymbolSettingsBackupStore.GetBackupFilePath(@"C:\LocalAppData", "18.0_Exp");

        var saved = store.SaveBackup(
            backupPath,
            isManualLoad: false,
            isLoadAdjacent: true,
            automaticSymbolLoad: true,
            processId: 12345);

        Assert.True(_fs.FileExists(backupPath));
        Assert.False(saved.IsManualLoad);
        Assert.True(saved.IsLoadAdjacent);
        Assert.True(saved.AutomaticSymbolLoad);
        Assert.Equal(12345, saved.ProcessId);

        var loaded = store.TryLoadBackup(backupPath);
        Assert.NotNull(loaded);
        Assert.False(loaded!.IsManualLoad);
        Assert.True(loaded.IsLoadAdjacent);
        Assert.True(loaded.AutomaticSymbolLoad);
        Assert.Equal(12345, loaded.ProcessId);
        Assert.Equal(_clock.UtcNow, loaded.TimestampUtc);

        store.DeleteBackup(backupPath);
        Assert.False(_fs.FileExists(backupPath));
        Assert.Null(store.TryLoadBackup(backupPath));
    }

    [Fact]
    public void CheckCrashRecovery_DeadProcess_ReturnsBackupForRecovery()
    {
        var store = new SymbolSettingsBackupStore(
            _fs,
            _clock,
            NullLogger.Instance,
            isDevenvProcessAlive: _ => false);

        string backupPath = SymbolSettingsBackupStore.GetBackupFilePath(@"C:\LocalAppData", "18.0_Exp");
        store.SaveBackup(backupPath, isManualLoad: false, isLoadAdjacent: true, automaticSymbolLoad: true, processId: 4000);

        var recovery = store.CheckCrashRecovery(backupPath, currentProcessId: 5000);

        Assert.NotNull(recovery);
        Assert.False(recovery!.IsManualLoad);
        Assert.True(recovery.IsLoadAdjacent);
        Assert.True(recovery.AutomaticSymbolLoad);
    }

    [Fact]
    public void CheckCrashRecovery_OtherLiveDevenvProcess_ReturnsNull()
    {
        var store = new SymbolSettingsBackupStore(
            _fs,
            _clock,
            NullLogger.Instance,
            isDevenvProcessAlive: pid => pid == 4000);

        string backupPath = SymbolSettingsBackupStore.GetBackupFilePath(@"C:\LocalAppData", "18.0_Exp");
        store.SaveBackup(backupPath, isManualLoad: false, isLoadAdjacent: true, automaticSymbolLoad: true, processId: 4000);

        var recovery = store.CheckCrashRecovery(backupPath, currentProcessId: 5000);

        Assert.Null(recovery);
    }
}

