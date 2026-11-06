using System;
using Newtonsoft.Json;

namespace VSOnDemandSymbols.Core.Statistics;

/// <summary>
/// Persisted statistics tracking the number of PDB symbol loads avoided at startup
/// and estimated developer time saved by On-Demand Symbols (SPEC §4.1).
/// </summary>
public sealed class StartupStatistics
{
    [JsonProperty("totalLaunches")]
    public int TotalLaunches { get; set; }

    [JsonProperty("totalPdbsPrevented")]
    public int TotalPdbsPrevented { get; set; }

    [JsonProperty("totalEstimatedTimeSavedSeconds")]
    public double TotalEstimatedTimeSavedSeconds { get; set; }

    [JsonProperty("lastLaunchPdbsPrevented")]
    public int LastLaunchPdbsPrevented { get; set; }

    [JsonProperty("lastLaunchEstimatedTimeSavedSeconds")]
    public double LastLaunchEstimatedTimeSavedSeconds { get; set; }

    [JsonProperty("lastLaunchTimestampUtc")]
    public DateTime? LastLaunchTimestampUtc { get; set; }

    [JsonIgnore]
    public TimeSpan TotalEstimatedTimeSaved => TimeSpan.FromSeconds(Math.Max(0, TotalEstimatedTimeSavedSeconds));

    [JsonIgnore]
    public TimeSpan LastLaunchEstimatedTimeSaved => TimeSpan.FromSeconds(Math.Max(0, LastLaunchEstimatedTimeSavedSeconds));

    [JsonIgnore]
    public string TotalEstimatedTimeSavedFormatted => FormatTimeSpan(TotalEstimatedTimeSaved);

    [JsonIgnore]
    public string LastLaunchEstimatedTimeSavedFormatted => FormatTimeSpan(LastLaunchEstimatedTimeSaved);

    /// <summary>
    /// Formats a time span into a friendly human-readable duration (e.g. "25s", "2m 5s", "1h 15m").
    /// </summary>
    public static string FormatTimeSpan(TimeSpan time)
    {
        if (time <= TimeSpan.Zero)
        {
            return "0s";
        }

        if (time.TotalSeconds < 60)
        {
            int seconds = Math.Max(1, (int)Math.Round(time.TotalSeconds));
            return $"{seconds}s";
        }

        if (time.TotalMinutes < 60)
        {
            int minutes = (int)time.TotalMinutes;
            int seconds = time.Seconds;
            return seconds > 0 ? $"{minutes}m {seconds}s" : $"{minutes}m";
        }

        int hours = (int)time.TotalHours;
        int remainingMinutes = time.Minutes;
        return remainingMinutes > 0 ? $"{hours}h {remainingMinutes}m" : $"{hours}h";
    }

    /// <summary>
    /// Formats a detailed summary for output window logging when an editor launch finishes booting.
    /// </summary>
    public string FormatOutputSummary()
    {
        if (TotalLaunches == 0)
        {
            return "No editor launches recorded yet.";
        }

        string launchPlural = TotalLaunches == 1 ? "launch" : "launches";
        return $"Startup symbol acceleration: avoided loading {LastLaunchPdbsPrevented:N0} PDBs " +
               $"(estimated ~{LastLaunchEstimatedTimeSavedFormatted} saved this launch). " +
               $"Lifetime: {TotalPdbsPrevented:N0} PDBs avoided (~{TotalEstimatedTimeSavedFormatted} saved) across {TotalLaunches} {launchPlural}.";
    }

    /// <summary>
    /// Formats a concise summary string for display on the Unified Settings page.
    /// </summary>
    public string FormatSettingsPageSummary()
    {
        if (TotalLaunches == 0)
        {
            return "No editor launches recorded yet.";
        }

        string launchPlural = TotalLaunches == 1 ? "launch" : "launches";
        return $"{TotalPdbsPrevented:N0} PDBs avoided across {TotalLaunches} {launchPlural} (~{TotalEstimatedTimeSavedFormatted} saved)";
    }

    /// <summary>
    /// Formats a concise lifetime notice for logging when an Unreal solution is opened.
    /// </summary>
    public string FormatLifetimeNotice()
    {
        if (TotalLaunches == 0)
        {
            return string.Empty;
        }

        string launchPlural = TotalLaunches == 1 ? "launch" : "launches";
        return $"(Lifetime startup savings: {TotalPdbsPrevented:N0} PDBs avoided, ~{TotalEstimatedTimeSavedFormatted} saved across {TotalLaunches} {launchPlural})";
    }
}

