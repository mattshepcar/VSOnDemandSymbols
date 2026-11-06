using System;
using System.IO;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities.UnifiedSettings;
using VSOnDemandSymbols.Core.Abstractions;
using VSOnDemandSymbols.Core.Statistics;
using VSOnDemandSymbols.Debugger;

namespace VSOnDemandSymbols.Statistics;

/// <summary>
/// Coordinates recording of editor startup acceleration metrics, Output window reporting,
/// and synchronization with Visual Studio's Unified Settings page (SPEC §4.1).
/// </summary>
public sealed class StartupStatisticsManager
{
    private const string StatsSummaryMoniker = "debugging.symbols.onDemandLoading.statsSummary";
    private const string TotalPdbsAvoidedMoniker = "debugging.symbols.onDemandLoading.totalPdbsAvoided";
    private const string EstimatedTimeSavedMoniker = "debugging.symbols.onDemandLoading.estimatedTimeSaved";
    private const string LastLaunchSavingsMoniker = "debugging.symbols.onDemandLoading.lastLaunchSavings";

    private readonly StartupStatisticsStore _store;
    private readonly Func<ISettingsManager?> _settingsManagerProvider;
    private readonly ILogger _logger;
    private readonly JoinableTaskFactory _joinableTaskFactory;

    public StartupStatisticsStore Store => _store;

    /// <summary>
    /// Resolves the default statistics file path for the current Visual Studio instance:
    /// <c>%LOCALAPPDATA%/OnDemandSymbols/&lt;instanceId&gt;/startup-stats.json</c>.
    /// </summary>
    public static string ResolveDefaultStatsFilePath(IVsShell? vsShell = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Path.GetTempPath();
        }

        string instanceId = SymbolSettingsManager.ResolveInstanceId(vsShell);
        return StartupStatisticsStore.GetStatsFilePath(localAppData, instanceId);
    }

    public StartupStatisticsManager(
        StartupStatisticsStore store,
        Func<ISettingsManager?> settingsManagerProvider,
        ILogger logger,
        JoinableTaskFactory joinableTaskFactory)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _settingsManagerProvider = settingsManagerProvider ?? throw new ArgumentNullException(nameof(settingsManagerProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _joinableTaskFactory = joinableTaskFactory ?? throw new ArgumentNullException(nameof(joinableTaskFactory));
    }

    /// <summary>
    /// Loads current statistics from disk and synchronizes them to the Unified Settings page.
    /// </summary>
    public void SynchronizeToSettings()
    {
        try
        {
            var stats = _store.Load();
            UpdateUnifiedSettings(stats);
        }
        catch (Exception ex)
        {
            _logger.Warning($"Could not synchronize startup statistics to settings page: {ex.Message}");
        }
    }

    /// <summary>
    /// Records an editor launch, logs summary to the Output window, and updates the settings page.
    /// </summary>
    public StartupStatistics RecordLaunch(
        int totalModulesInProcess,
        int pdbsLoadedAtStartup,
        TimeSpan startupDuration)
    {
        try
        {
            var stats = _store.RecordLaunch(totalModulesInProcess, pdbsLoadedAtStartup, startupDuration);

            // Log directly to the Output window pane
            _logger.Info(stats.FormatOutputSummary());

            // Update Unified Settings properties
            UpdateUnifiedSettings(stats);

            return stats;
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to record editor startup statistics.", ex);
            return _store.Load();
        }
    }

    private void UpdateUnifiedSettings(StartupStatistics stats)
    {
        try
        {
            var settingsManager = _settingsManagerProvider();
            if (settingsManager == null)
            {
                return;
            }

            var writer = settingsManager.GetWriter("VSOnDemandSymbols");
            if (writer == null)
            {
                return;
            }

            writer.EnqueueChange(StatsSummaryMoniker, stats.FormatSettingsPageSummary());
            writer.EnqueueChange(TotalPdbsAvoidedMoniker, stats.TotalPdbsPrevented);
            writer.EnqueueChange(EstimatedTimeSavedMoniker, stats.TotalEstimatedTimeSavedFormatted);

            string lastLaunch = stats.TotalLaunches > 0
                ? $"{stats.LastLaunchPdbsPrevented:N0} PDBs avoided (~{stats.LastLaunchEstimatedTimeSavedFormatted} saved)"
                : "None";
            writer.EnqueueChange(LastLaunchSavingsMoniker, lastLaunch);

            writer.RequestCommit("On-Demand Symbols startup statistics update");
        }
        catch (Exception ex)
        {
            _logger.Diagnostic($"Could not push statistics to Unified Settings writer: {ex.Message}");
        }
    }
}
