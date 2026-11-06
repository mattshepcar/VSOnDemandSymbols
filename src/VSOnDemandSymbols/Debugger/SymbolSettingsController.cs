using System;
using System.Diagnostics;
using System.IO;
using Microsoft.VisualStudio.Debugger.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VSOnDemandSymbols.Core.Abstractions;
using VSOnDemandSymbols.Core.State;

namespace VSOnDemandSymbols.Debugger;

/// <summary>
/// Single owner of all writes to Visual Studio's symbol settings (Golden Rule #1, SPEC §7.4, §9.2, D-9, D-13, D-15).
/// Only modifies <c>IsManualLoad</c>, <c>IsLoadAdjacent</c>, and <c>AutomaticSymbolLoad</c>;
/// never touches <c>IncludeList</c>, <c>ExcludeList</c>, symbol paths, servers, or cache.
/// </summary>
public sealed class SymbolSettingsController
{
    private readonly IVsDebuggerSymbolSettingsManager120A _settingsManager;
    private readonly SymbolSettingsBackupStore _backupStore;
    private readonly ILogger _logger;
    private readonly string _backupFilePath;
    private readonly int _currentProcessId;

    private SymbolSettingsBackupDto? _activeBackup;
    private bool _isApplied;

    public bool IsApplied => _isApplied;
    public string BackupFilePath => _backupFilePath;

    public SymbolSettingsController(
        IVsDebuggerSymbolSettingsManager120A settingsManager,
        SymbolSettingsBackupStore backupStore,
        ILogger logger,
        string backupFilePath,
        int? currentProcessId = null)
    {
        _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
        _backupStore = backupStore ?? throw new ArgumentNullException(nameof(backupStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _backupFilePath = backupFilePath ?? throw new ArgumentNullException(nameof(backupFilePath));
        _currentProcessId = currentProcessId ?? GetCurrentProcessIdSafe();
    }

    /// <summary>
    /// Resolves the per-VS-instance backup file path (SPEC §9.2):
    /// <c>%LOCALAPPDATA%/OnDemandSymbols/&lt;VS instance id&gt;[_&lt;root suffix&gt;]/symbol-settings-backup.json</c>.
    /// </summary>
    public static string ResolveDefaultBackupFilePath(IVsShell? vsShell = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Path.GetTempPath();
        }

        string instanceId = "Default";
        try
        {
            if (vsShell != null &&
                vsShell.GetProperty((int)__VSSPROPID.VSSPROPID_VirtualRegistryRoot, out object? regRootObj) == 0 &&
                regRootObj is string regRoot &&
                !string.IsNullOrWhiteSpace(regRoot))
            {
                string trimmed = regRoot.TrimEnd('\\', '/');
                string segment = Path.GetFileName(trimmed);
                if (!string.IsNullOrWhiteSpace(segment))
                {
                    instanceId = segment;
                }
            }

            string? rootSuffix = TryGetCommandLineRootSuffix();
            if (!string.IsNullOrWhiteSpace(rootSuffix) &&
                !instanceId.EndsWith(rootSuffix!, StringComparison.OrdinalIgnoreCase))
            {
                instanceId = $"{instanceId}_{rootSuffix}";
            }
        }
        catch
        {
            // Fall back to Default instance folder
        }

        return SymbolSettingsBackupStore.GetBackupFilePath(localAppData, instanceId);
    }

    /// <summary>
    /// Checks at startup whether a backup file exists from a previous crashed session and restores it (SPEC §7.4, §9.2).
    /// </summary>
    public bool RecoverFromCrashIfNeeded()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var crashBackup = _backupStore.CheckCrashRecovery(_backupFilePath, _currentProcessId);
            if (crashBackup == null)
            {
                return false;
            }

            ApplyFlagsToVs(
                crashBackup.IsManualLoad,
                crashBackup.IsLoadAdjacent,
                crashBackup.AutomaticSymbolLoad);

            _backupStore.DeleteBackup(_backupFilePath);
            _activeBackup = null;
            _isApplied = false;

            _logger.Warning(
                $"Restored symbol settings after a previous unexpected exit " +
                $"(IsManualLoad={crashBackup.IsManualLoad}, IsLoadAdjacent={crashBackup.IsLoadAdjacent}, " +
                $"AutomaticSymbolLoad={crashBackup.AutomaticSymbolLoad}).");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to recover symbol settings from crash backup.", ex);
            return false;
        }
    }

    /// <summary>
    /// Backs up the user's current symbol settings flags to disk and applies on-demand manual-load mode:
    /// <c>IsManualLoad = true</c>, <c>IsLoadAdjacent = false</c>, <c>AutomaticSymbolLoad = false</c> (SPEC §7.4, D-13, D-15).
    /// Leaves <c>IncludeList</c> and <c>ExcludeList</c> untouched. Idempotent.
    /// </summary>
    public void ApplyManualLoadMode()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_isApplied)
        {
            return;
        }

        try
        {
            var current = _settingsManager.GetCurrentSymbolSettings();
            bool origManual = current.IsManualLoad;
            bool origAdjacent = current.IsLoadAdjacent;
            bool origAutoLoad = (current as IVsDebuggerSymbolSettings170)?.get_AutomaticSymbolLoad() ?? true;

            // Golden Rule #1: Back up to disk FIRST before modifying VS settings.
            _activeBackup = _backupStore.SaveBackup(
                _backupFilePath,
                origManual,
                origAdjacent,
                origAutoLoad,
                _currentProcessId);

            ApplyFlagsToVs(
                isManualLoad: true,
                isLoadAdjacent: false,
                automaticSymbolLoad: false);

            _isApplied = true;

            _logger.Info(
                "On-demand symbol mode active (VS Symbol Settings set to manual-load mode: " +
                "IsManualLoad=true, IsLoadAdjacent=false, AutomaticSymbolLoad=false; user IncludeList/ExcludeList untouched).");
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to apply manual-load symbol settings; attempting fallback restore.", ex);
            RestoreOriginalSettings();
        }
    }

    /// <summary>
    /// Restores the user's original symbol settings boolean flags from backup and deletes the backup file (SPEC §7.4).
    /// Idempotent.
    /// </summary>
    public void RestoreOriginalSettings()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var backup = _activeBackup ?? _backupStore.TryLoadBackup(_backupFilePath);
        if (backup == null && !_isApplied)
        {
            return;
        }

        try
        {
            if (backup != null)
            {
                ApplyFlagsToVs(
                    backup.IsManualLoad,
                    backup.IsLoadAdjacent,
                    backup.AutomaticSymbolLoad);

                _logger.Info(
                    $"Restored user symbol settings (IsManualLoad={backup.IsManualLoad}, " +
                    $"IsLoadAdjacent={backup.IsLoadAdjacent}, AutomaticSymbolLoad={backup.AutomaticSymbolLoad}).");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to restore original symbol settings.", ex);
        }
        finally
        {
            _backupStore.DeleteBackup(_backupFilePath);
            _activeBackup = null;
            _isApplied = false;
        }
    }

    private void ApplyFlagsToVs(bool isManualLoad, bool isLoadAdjacent, bool automaticSymbolLoad)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var current = _settingsManager.GetCurrentSymbolSettings();
        var updated = current.Clone();

        updated.IsManualLoad = isManualLoad;
        updated.IsLoadAdjacent = isLoadAdjacent;
        if (updated is IVsDebuggerSymbolSettings170 updated170)
        {
            updated170.set_AutomaticSymbolLoad(automaticSymbolLoad);
        }

        _settingsManager.SaveSymbolSettings(updated, loadSymbolsNow: false);
    }

    private static string? TryGetCommandLineRootSuffix()
    {
        try
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "/rootsuffix", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(args[i], "-rootsuffix", StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }
        }
        catch
        {
            // Ignore command line access error
        }

        return null;
    }

    private static int GetCurrentProcessIdSafe()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            return current.Id;
        }
        catch
        {
            return 0;
        }
    }
}

