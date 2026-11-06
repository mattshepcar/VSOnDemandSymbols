using System;
using VSOnDemandSymbols.Core.Abstractions;
using VSOnDemandSymbols.Core.Statistics;
using Xunit;

namespace VSOnDemandSymbols.Core.Tests.Statistics;

public class StartupStatisticsTests
{
    private readonly InMemoryFileSystem _fs = new();
    private readonly TestClock _clock = new(new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void FormatTimeSpan_ZeroAndNegative_ReturnsZeroSeconds()
    {
        Assert.Equal("0s", StartupStatistics.FormatTimeSpan(TimeSpan.Zero));
        Assert.Equal("0s", StartupStatistics.FormatTimeSpan(TimeSpan.FromSeconds(-5)));
    }

    [Theory]
    [InlineData(1, "1s")]
    [InlineData(25, "25s")]
    [InlineData(59, "59s")]
    [InlineData(60, "1m")]
    [InlineData(65, "1m 5s")]
    [InlineData(125, "2m 5s")]
    [InlineData(3600, "1h")]
    [InlineData(3660, "1h 1m")]
    [InlineData(4500, "1h 15m")]
    public void FormatTimeSpan_VariousSeconds_ReturnsExpectedFormat(int totalSeconds, string expected)
    {
        Assert.Equal(expected, StartupStatistics.FormatTimeSpan(TimeSpan.FromSeconds(totalSeconds)));
    }

    [Fact]
    public void FormatSummary_WhenNoLaunches_ReturnsFriendlyPlaceholder()
    {
        var stats = new StartupStatistics();
        Assert.Equal("No editor launches recorded yet.", stats.FormatOutputSummary());
        Assert.Equal("No editor launches recorded yet.", stats.FormatSettingsPageSummary());
        Assert.Equal(string.Empty, stats.FormatLifetimeNotice());
    }

    [Fact]
    public void FormatSummary_WithSingleLaunch_FormatsCorrectly()
    {
        var stats = new StartupStatistics
        {
            TotalLaunches = 1,
            TotalPdbsPrevented = 948,
            TotalEstimatedTimeSavedSeconds = 25,
            LastLaunchPdbsPrevented = 948,
            LastLaunchEstimatedTimeSavedSeconds = 25
        };

        string output = stats.FormatOutputSummary();
        Assert.Contains("948 PDBs", output);
        Assert.Contains("~25s saved this launch", output);
        Assert.Contains("across 1 launch", output);

        string settings = stats.FormatSettingsPageSummary();
        Assert.Equal("948 PDBs avoided across 1 launch (~25s saved)", settings);

        string notice = stats.FormatLifetimeNotice();
        Assert.Equal("(Lifetime startup savings: 948 PDBs avoided, ~25s saved across 1 launch)", notice);
    }

    [Fact]
    public void FormatSummary_WithMultipleLaunches_FormatsPluralAndFormattedTimes()
    {
        var stats = new StartupStatistics
        {
            TotalLaunches = 5,
            TotalPdbsPrevented = 4740,
            TotalEstimatedTimeSavedSeconds = 125,
            LastLaunchPdbsPrevented = 950,
            LastLaunchEstimatedTimeSavedSeconds = 26
        };

        string output = stats.FormatOutputSummary();
        Assert.Contains("950 PDBs", output);
        Assert.Contains("~26s saved this launch", output);
        Assert.Contains("4,740 PDBs avoided (~2m 5s saved) across 5 launches", output);

        string settings = stats.FormatSettingsPageSummary();
        Assert.Equal("4,740 PDBs avoided across 5 launches (~2m 5s saved)", settings);
    }

    [Fact]
    public void GetStatsFilePath_FormatsExpectedDirectoryPaths()
    {
        string pathWithoutInstance = StartupStatisticsStore.GetStatsFilePath(@"C:\AppData");
        Assert.Equal(@"C:\AppData\OnDemandSymbols\startup-stats.json", pathWithoutInstance);

        string pathWithInstance = StartupStatisticsStore.GetStatsFilePath(@"C:\AppData", "18.0_Exp");
        Assert.Equal(@"C:\AppData\OnDemandSymbols\18.0_Exp\startup-stats.json", pathWithInstance);
    }

    [Fact]
    public void Load_NonExistentFile_ReturnsFreshInstance()
    {
        var store = new StartupStatisticsStore(_fs, _clock, NullLogger.Instance, @"C:\AppData\startup-stats.json");
        var stats = store.Load();

        Assert.Equal(0, stats.TotalLaunches);
        Assert.Equal(0, stats.TotalPdbsPrevented);
        Assert.Equal(0, stats.TotalEstimatedTimeSavedSeconds);
    }

    [Fact]
    public void Load_CorruptedJson_ReturnsFreshInstanceWithoutCrashing()
    {
        string filePath = @"C:\AppData\startup-stats.json";
        _fs.WriteAllText(filePath, "{ invalid: json content ... ");

        var store = new StartupStatisticsStore(_fs, _clock, NullLogger.Instance, filePath);
        var stats = store.Load();

        Assert.NotNull(stats);
        Assert.Equal(0, stats.TotalLaunches);
    }

    [Fact]
    public void SaveAndLoad_RoundTrip_PreservesAllFields()
    {
        string filePath = @"C:\AppData\startup-stats.json";
        var store = new StartupStatisticsStore(_fs, _clock, NullLogger.Instance, filePath);

        var original = new StartupStatistics
        {
            TotalLaunches = 3,
            TotalPdbsPrevented = 2850,
            TotalEstimatedTimeSavedSeconds = 75.5,
            LastLaunchPdbsPrevented = 945,
            LastLaunchEstimatedTimeSavedSeconds = 24.5,
            LastLaunchTimestampUtc = _clock.UtcNow
        };

        store.Save(original);
        Assert.True(_fs.FileExists(filePath));

        var loaded = store.Load();
        Assert.Equal(3, loaded.TotalLaunches);
        Assert.Equal(2850, loaded.TotalPdbsPrevented);
        Assert.Equal(75.5, loaded.TotalEstimatedTimeSavedSeconds);
        Assert.Equal(945, loaded.LastLaunchPdbsPrevented);
        Assert.Equal(24.5, loaded.LastLaunchEstimatedTimeSavedSeconds);
        Assert.Equal(_clock.UtcNow, loaded.LastLaunchTimestampUtc);
    }

    [Fact]
    public void RecordLaunch_WithZeroModules_UsesDefaultModuleCount()
    {
        string filePath = @"C:\AppData\startup-stats.json";
        var store = new StartupStatisticsStore(_fs, _clock, NullLogger.Instance, filePath);

        var stats = store.RecordLaunch(
            totalModulesInProcess: 0,
            pdbsLoadedAtStartup: 2,
            measuredStartupDuration: TimeSpan.FromSeconds(20));

        Assert.Equal(1, stats.TotalLaunches);
        Assert.Equal(StartupStatisticsStore.DefaultUnrealEditorModuleCount - 2, stats.TotalPdbsPrevented);
        Assert.Equal(StartupStatisticsStore.DefaultUnrealEditorModuleCount - 2, stats.LastLaunchPdbsPrevented);
        Assert.Equal(20, stats.LastLaunchEstimatedTimeSavedSeconds);
        Assert.Equal(20, stats.TotalEstimatedTimeSavedSeconds);
        Assert.Equal(_clock.UtcNow, stats.LastLaunchTimestampUtc);
    }

    [Fact]
    public void RecordLaunch_WithCustomModuleCount_CalculatesAccurately()
    {
        string filePath = @"C:\AppData\startup-stats.json";
        var store = new StartupStatisticsStore(_fs, _clock, NullLogger.Instance, filePath);

        var stats = store.RecordLaunch(
            totalModulesInProcess: 1024,
            pdbsLoadedAtStartup: 4,
            measuredStartupDuration: TimeSpan.FromSeconds(35));

        Assert.Equal(1, stats.TotalLaunches);
        Assert.Equal(1020, stats.TotalPdbsPrevented);
        Assert.Equal(35, stats.TotalEstimatedTimeSavedSeconds);
    }

    [Fact]
    public void RecordLaunch_ClampsLongStartupDuration()
    {
        string filePath = @"C:\AppData\startup-stats.json";
        var store = new StartupStatisticsStore(_fs, _clock, NullLogger.Instance, filePath);

        // User paused on breakpoint for 10 minutes during boot
        var stats = store.RecordLaunch(
            totalModulesInProcess: 950,
            pdbsLoadedAtStartup: 1,
            measuredStartupDuration: TimeSpan.FromMinutes(10));

        Assert.Equal(StartupStatisticsStore.MaxCreditedStartupSeconds, stats.LastLaunchEstimatedTimeSavedSeconds);
        Assert.Equal(StartupStatisticsStore.MaxCreditedStartupSeconds, stats.TotalEstimatedTimeSavedSeconds);
    }

    [Fact]
    public void RecordLaunch_ShortDuration_UsesDefaultTimeSaving()
    {
        string filePath = @"C:\AppData\startup-stats.json";
        var store = new StartupStatisticsStore(_fs, _clock, NullLogger.Instance, filePath);

        var stats = store.RecordLaunch(
            totalModulesInProcess: 950,
            pdbsLoadedAtStartup: 1,
            measuredStartupDuration: TimeSpan.FromSeconds(1)); // Duration < 3.0s

        Assert.Equal(StartupStatisticsStore.DefaultStartupTimeSeconds, stats.LastLaunchEstimatedTimeSavedSeconds);
    }

    [Fact]
    public void RecordLaunch_MultipleLaunches_AccumulatesTotals()
    {
        string filePath = @"C:\AppData\startup-stats.json";
        var store = new StartupStatisticsStore(_fs, _clock, NullLogger.Instance, filePath);

        store.RecordLaunch(950, 2, TimeSpan.FromSeconds(20));
        var stats = store.RecordLaunch(960, 5, TimeSpan.FromSeconds(30));

        Assert.Equal(2, stats.TotalLaunches);
        Assert.Equal((950 - 2) + (960 - 5), stats.TotalPdbsPrevented);
        Assert.Equal(20 + 30, stats.TotalEstimatedTimeSavedSeconds);
        Assert.Equal(960 - 5, stats.LastLaunchPdbsPrevented);
        Assert.Equal(30, stats.LastLaunchEstimatedTimeSavedSeconds);
    }

    [Fact]
    public void Reset_ClearsAllStatistics()
    {
        string filePath = @"C:\AppData\startup-stats.json";
        var store = new StartupStatisticsStore(_fs, _clock, NullLogger.Instance, filePath);

        store.RecordLaunch(950, 2, TimeSpan.FromSeconds(20));
        var resetStats = store.Reset();

        Assert.Equal(0, resetStats.TotalLaunches);
        Assert.Equal(0, resetStats.TotalPdbsPrevented);
        Assert.Equal(0, resetStats.TotalEstimatedTimeSavedSeconds);

        var loaded = store.Load();
        Assert.Equal(0, loaded.TotalLaunches);
    }
}

