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

        // Sanitization of invalid filesystem characters
        string sanitizedPath = SymbolSettingsBackupStore.GetBackupFilePath(@"C:\Users\Dev\AppData\Local", "18.0:1a2b/Exp*test");
        Assert.Equal(
            @"C:\Users\Dev\AppData\Local\OnDemandSymbols\18.0_1a2b_Exp_test\symbol-settings-backup.json",
            sanitizedPath);

        // Null / whitespace instance ID falls back to "Default"
        string defaultPath = SymbolSettingsBackupStore.GetBackupFilePath(@"C:\Users\Dev\AppData\Local", "   ");
        Assert.Equal(
            @"C:\Users\Dev\AppData\Local\OnDemandSymbols\Default\symbol-settings-backup.json",
            defaultPath);
    }

    [Fact]
    public void SaveLoadDelete_RoundTrip_PreservesFlagsAndPid()
    {
        var store = new SymbolSettingsBackupStore(_fs, _clock, NullLogger.Instance);
        string backupPath = SymbolSettingsBackupStore.GetBackupFilePath(@"C:\LocalAppData", "18.0_Exp");

        var originalInclude = new[]
        {
            new SymbolOptionBackupDto { Name = "MyCustomModule.dll", IsEnabled = true },
            new SymbolOptionBackupDto { Name = "DisabledModule.dll", IsEnabled = false }
        };

        var saved = store.SaveBackup(
            backupPath,
            isManualLoad: false,
            isLoadAdjacent: true,
            automaticSymbolLoad: true,
            processId: 12345,
            includeList: originalInclude);

        Assert.True(_fs.FileExists(backupPath));
        Assert.False(saved.IsManualLoad);
        Assert.True(saved.IsLoadAdjacent);
        Assert.True(saved.AutomaticSymbolLoad);
        Assert.Equal(12345, saved.ProcessId);
        Assert.Equal(2, saved.IncludeList.Count);
        Assert.Equal("MyCustomModule.dll", saved.IncludeList[0].Name);
        Assert.True(saved.IncludeList[0].IsEnabled);
        Assert.Equal("DisabledModule.dll", saved.IncludeList[1].Name);
        Assert.False(saved.IncludeList[1].IsEnabled);

        var loaded = store.TryLoadBackup(backupPath);
        Assert.NotNull(loaded);
        Assert.False(loaded!.IsManualLoad);
        Assert.True(loaded.IsLoadAdjacent);
        Assert.True(loaded.AutomaticSymbolLoad);
        Assert.Equal(12345, loaded.ProcessId);
        Assert.Equal(_clock.UtcNow, loaded.TimestampUtc);
        Assert.Equal(2, loaded.IncludeList.Count);
        Assert.Equal("MyCustomModule.dll", loaded.IncludeList[0].Name);
        Assert.True(loaded.IncludeList[0].IsEnabled);

        store.DeleteBackup(backupPath);
        Assert.False(_fs.FileExists(backupPath));
        Assert.Null(store.TryLoadBackup(backupPath));
    }

    [Fact]
    public void SaveLoad_PreservesSymbolServerFlagsAndPaths()
    {
        var store = new SymbolSettingsBackupStore(_fs, _clock, NullLogger.Instance);
        string backupPath = SymbolSettingsBackupStore.GetBackupFilePath(@"C:\LocalAppData", "18.0_Exp");

        var symbolPaths = new[]
        {
            new SymbolOptionBackupDto { Name = "https://msdl.microsoft.com/download/symbols", IsEnabled = true },
            new SymbolOptionBackupDto { Name = @"D:\Symbols", IsEnabled = false }
        };

        var saved = store.SaveBackup(
            backupPath,
            isManualLoad: true,
            isLoadAdjacent: false,
            automaticSymbolLoad: false,
            processId: 9999,
            includeList: null,
            useMsSymbolServers: true,
            useNugetSymbolServers: true,
            symbolPaths: symbolPaths);

        Assert.True(saved.UseMsSymbolServers);
        Assert.True(saved.UseNugetSymbolServers);
        Assert.Equal(2, saved.SymbolPaths.Count);
        Assert.Equal("https://msdl.microsoft.com/download/symbols", saved.SymbolPaths[0].Name);
        Assert.True(saved.SymbolPaths[0].IsEnabled);

        var loaded = store.TryLoadBackup(backupPath);
        Assert.NotNull(loaded);
        Assert.True(loaded!.UseMsSymbolServers);
        Assert.True(loaded.UseNugetSymbolServers);
        Assert.Equal(2, loaded.SymbolPaths.Count);
        Assert.Equal("https://msdl.microsoft.com/download/symbols", loaded.SymbolPaths[0].Name);
        Assert.True(loaded.SymbolPaths[0].IsEnabled);
        Assert.Equal(@"D:\Symbols", loaded.SymbolPaths[1].Name);
        Assert.False(loaded.SymbolPaths[1].IsEnabled);
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

    [Fact]
    public void CheckCrashRecovery_SameProcessId_ReturnsBackup()
    {
        // When checking crash recovery for the same PID, it belongs to this session and should be recovered
        var store = new SymbolSettingsBackupStore(
            _fs,
            _clock,
            NullLogger.Instance,
            isDevenvProcessAlive: pid => true);

        string backupPath = SymbolSettingsBackupStore.GetBackupFilePath(@"C:\LocalAppData", "18.0_Exp");
        store.SaveBackup(backupPath, isManualLoad: false, isLoadAdjacent: true, automaticSymbolLoad: true, processId: 4000);

        var recovery = store.CheckCrashRecovery(backupPath, currentProcessId: 4000);

        Assert.NotNull(recovery);
        Assert.False(recovery!.IsManualLoad);
    }

    [Fact]
    public void TryLoadBackup_CorruptJson_ReturnsNullAndDoesNotThrow()
    {
        var store = new SymbolSettingsBackupStore(_fs, _clock, NullLogger.Instance);
        string backupPath = SymbolSettingsBackupStore.GetBackupFilePath(@"C:\LocalAppData", "18.0_Exp");

        _fs.WriteAllText(backupPath, "{ corrupt json syntax !!@@ ");

        var result = store.TryLoadBackup(backupPath);
        Assert.Null(result);
    }

    [Fact]
    public void DeleteBackup_NonExistentFile_DoesNotThrow()
    {
        var store = new SymbolSettingsBackupStore(_fs, _clock, NullLogger.Instance);
        string backupPath = SymbolSettingsBackupStore.GetBackupFilePath(@"C:\LocalAppData", "NonExistentInstance");

        // Should complete silently without exception
        store.DeleteBackup(backupPath);
    }
}

