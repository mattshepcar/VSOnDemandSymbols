using System;
using System.IO;
using Newtonsoft.Json;
using VSOnDemandSymbols.Core.Abstractions;

namespace VSOnDemandSymbols.Core.Statistics;

/// <summary>
/// Manages loading, atomic persisting, and calculating editor startup acceleration
/// metrics (<c>startup-stats.json</c>, SPEC §4.1).
/// </summary>
public sealed class StartupStatisticsStore
{
    public const string StatsFileName = "startup-stats.json";

    /// <summary>
    /// Typical number of mapped Unreal Editor modules during startup (SPEC §1.1).
    /// Used as a fallback when process modules cannot be enumerated directly.
    /// </summary>
    public const int DefaultUnrealEditorModuleCount = 950;

    /// <summary>
    /// Conservative default time savings (in seconds) per launch when duration is not measured.
    /// Based on developer experience that symbol loading accounts for ~50% of editor startup time.
    /// </summary>
    public const double DefaultStartupTimeSeconds = 25.0;

    /// <summary>
    /// Maximum boot duration (in seconds) credited towards time savings in a single session,
    /// preventing paused breakpoints or idle runs from distorting statistics.
    /// </summary>
    public const double MaxCreditedStartupSeconds = 90.0;

    private readonly IFileSystem _fileSystem;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly string _statsFilePath;
    private readonly object _sync = new();

    public string StatsFilePath => _statsFilePath;

    public StartupStatisticsStore(
        IFileSystem fileSystem,
        IClock clock,
        ILogger logger,
        string statsFilePath)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _statsFilePath = !string.IsNullOrWhiteSpace(statsFilePath)
            ? statsFilePath
            : throw new ArgumentException("Stats file path cannot be empty.", nameof(statsFilePath));
    }

    /// <summary>
    /// Resolves the default statistics file path:
    /// <c>%LOCALAPPDATA%/OnDemandSymbols/[&lt;instanceId&gt;/]startup-stats.json</c>.
    /// </summary>
    public static string GetStatsFilePath(string localAppDataDirectory, string? vsInstanceId = null)
    {
        if (string.IsNullOrWhiteSpace(localAppDataDirectory))
        {
            throw new ArgumentException("LocalAppData directory cannot be empty.", nameof(localAppDataDirectory));
        }

        if (!string.IsNullOrWhiteSpace(vsInstanceId))
        {
            return Path.Combine(localAppDataDirectory, "OnDemandSymbols", vsInstanceId!.Trim(), StatsFileName);
        }

        return Path.Combine(localAppDataDirectory, "OnDemandSymbols", StatsFileName);
    }

    /// <summary>
    /// Loads the stored statistics from disk, or returns a new default instance if missing or corrupted.
    /// </summary>
    public StartupStatistics Load()
    {
        lock (_sync)
        {
            try
            {
                if (!_fileSystem.FileExists(_statsFilePath))
                {
                    return new StartupStatistics();
                }

                string json = _fileSystem.ReadAllText(_statsFilePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new StartupStatistics();
                }

                var stats = JsonConvert.DeserializeObject<StartupStatistics>(json);
                return stats ?? new StartupStatistics();
            }
            catch (Exception ex)
            {
                _logger.Warning($"Could not load startup statistics from '{_statsFilePath}'; starting with fresh statistics. {ex.Message}");
                return new StartupStatistics();
            }
        }
    }

    /// <summary>
    /// Persists statistics atomically to disk.
    /// </summary>
    public void Save(StartupStatistics stats)
    {
        if (stats == null)
        {
            throw new ArgumentNullException(nameof(stats));
        }

        lock (_sync)
        {
            try
            {
                string? directory = Path.GetDirectoryName(_statsFilePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    _fileSystem.CreateDirectory(directory!);
                }

                string json = JsonConvert.SerializeObject(stats, Formatting.Indented);
                string tempFilePath = _statsFilePath + ".tmp." + Guid.NewGuid().ToString("N");

                _fileSystem.WriteAllText(tempFilePath, json);

                if (_fileSystem.FileExists(_statsFilePath))
                {
                    _fileSystem.DeleteFile(_statsFilePath);
                }

                _fileSystem.MoveFile(tempFilePath, _statsFilePath);
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to save startup statistics to '{_statsFilePath}'.", ex);
            }
        }
    }

    /// <summary>
    /// Records a completed editor startup, computes avoided PDB count and estimated time saved,
    /// saves the updated totals to disk, and returns the result.
    /// </summary>
    /// <param name="totalModulesInProcess">Total modules loaded by the editor process (or 0 to use default ~950).</param>
    /// <param name="pdbsLoadedAtStartup">Number of modules for which symbols were loaded at launch.</param>
    /// <param name="measuredStartupDuration">Measured time between launch and boot completion.</param>
    public StartupStatistics RecordLaunch(
        int totalModulesInProcess,
        int pdbsLoadedAtStartup,
        TimeSpan measuredStartupDuration)
    {
        lock (_sync)
        {
            var stats = Load();

            int modules = totalModulesInProcess > 0
                ? totalModulesInProcess
                : DefaultUnrealEditorModuleCount;

            int loaded = Math.Max(0, pdbsLoadedAtStartup);
            int pdbsPrevented = Math.Max(0, modules - loaded);

            double estimatedSeconds;
            if (measuredStartupDuration.TotalSeconds >= 3.0)
            {
                // Loading symbols accounted for about half of editor startup time (T_unoptimized ≈ 2 * T_boot).
                // Omitting symbols therefore saved ≈ T_boot.
                estimatedSeconds = Math.Min(MaxCreditedStartupSeconds, measuredStartupDuration.TotalSeconds);
            }
            else
            {
                estimatedSeconds = DefaultStartupTimeSeconds;
            }

            stats.TotalLaunches += 1;
            stats.TotalPdbsPrevented += pdbsPrevented;
            stats.TotalEstimatedTimeSavedSeconds += estimatedSeconds;

            stats.LastLaunchPdbsPrevented = pdbsPrevented;
            stats.LastLaunchEstimatedTimeSavedSeconds = estimatedSeconds;
            stats.LastLaunchTimestampUtc = _clock.UtcNow;

            Save(stats);
            return stats;
        }
    }

    /// <summary>
    /// Resets all statistics to zero.
    /// </summary>
    public StartupStatistics Reset()
    {
        lock (_sync)
        {
            var empty = new StartupStatistics();
            Save(empty);
            return empty;
        }
    }
}
