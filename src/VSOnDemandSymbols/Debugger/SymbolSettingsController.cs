using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.VisualStudio.Debugger.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VSOnDemandSymbols.Core.Abstractions;
using VSOnDemandSymbols.Core.Policy;
using VSOnDemandSymbols.Core.State;

namespace VSOnDemandSymbols.Debugger;

/// <summary>
/// Single owner of all writes to Visual Studio's symbol settings (Golden Rule #1, SPEC §7.4, §9.2).
/// Backs up the user's original settings (flags and IncludeList) first.
/// While active, puts VS into manual-load mode (<c>IsManualLoad=true, IsLoadAdjacent=false, AutomaticSymbolLoad=false</c>)
/// and populates <c>IncludeList</c> with the wanted binaries so Visual Studio natively loads their symbols on map.
/// Restores the user's exact original flags and IncludeList on close, disable, exit, or crash recovery.
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
    public SymbolSettingsBackupDto? ActiveBackup => _activeBackup;

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
                string segment = WildcardPattern.ExtractFileNameSafe(trimmed);
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

            RestoreSettingsToVs(crashBackup);

            _backupStore.DeleteBackup(_backupFilePath);
            _activeBackup = null;
            _isApplied = false;

            _logger.Warning(
                $"Restored symbol settings after a previous unexpected exit " +
                $"(IsManualLoad={crashBackup.IsManualLoad}, IsLoadAdjacent={crashBackup.IsLoadAdjacent}, " +
                $"AutomaticSymbolLoad={crashBackup.AutomaticSymbolLoad}, IncludeList.Count={crashBackup.IncludeList.Count}).");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to recover symbol settings from crash backup.", ex);
            return false;
        }
    }

    /// <summary>
    /// Backs up the user's current symbol settings flags and IncludeList to disk and applies on-demand manual-load mode:
    /// <c>IsManualLoad = true</c>, <c>IsLoadAdjacent = false</c>, <c>AutomaticSymbolLoad = false</c> (SPEC §7.4, D-13, D-15).
    /// Idempotent.
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
            var origIncludeList = ReadOptionList(current.IncludeList);

            // Golden Rule #1: Back up to disk FIRST before modifying VS settings.
            _activeBackup = _backupStore.SaveBackup(
                _backupFilePath,
                origManual,
                origAdjacent,
                origAutoLoad,
                _currentProcessId,
                origIncludeList);

            ApplyFlagsToVs(
                isManualLoad: true,
                isLoadAdjacent: false,
                automaticSymbolLoad: false);

            _isApplied = true;

            _logger.Info(
                "On-demand symbol mode active (VS Symbol Settings set to manual-load mode: " +
                "IsManualLoad=true, IsLoadAdjacent=false, AutomaticSymbolLoad=false).");
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to apply manual-load symbol settings; attempting fallback restore.", ex);
            RestoreOriginalSettings();
        }
    }

    /// <summary>
    /// Merges the wanted module binaries into VS's <c>IncludeList</c> alongside the user's original entries,
    /// enabling Visual Studio's native debugger to load symbols for them at module map time.
    /// </summary>
    public void ApplyWantedSetToIncludeList(IEnumerable<string> wantedBinaries, bool loadSymbolsNow = false)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            EnsureBackupExists();

            var current = _settingsManager.GetCurrentSymbolSettings();
            var updated = current.Clone();

            updated.IsManualLoad = true;
            updated.IsLoadAdjacent = false;
            if (updated is IVsDebuggerSymbolSettings170 updated170)
            {
                updated170.set_AutomaticSymbolLoad(false);
            }

            // Start with the user's original IncludeList from backup
            var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            updated.IncludeList.Clear();

            if (_activeBackup?.IncludeList != null)
            {
                foreach (var opt in _activeBackup.IncludeList)
                {
                    if (!string.IsNullOrWhiteSpace(opt.Name) && existingNames.Add(opt.Name))
                    {
                        updated.IncludeList.Add(opt.Name, opt.IsEnabled);
                    }
                }
            }

            // Union with the wanted set binaries
            if (wantedBinaries != null)
            {
                foreach (string binary in wantedBinaries)
                {
                    string clean = WildcardPattern.ExtractFileNameSafe(binary);
                    if (!string.IsNullOrWhiteSpace(clean) && existingNames.Add(clean))
                    {
                        updated.IncludeList.Add(clean, true);
                    }
                }
            }

            _settingsManager.SaveSymbolSettings(updated, loadSymbolsNow);
            _isApplied = true;
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to update symbol settings IncludeList for wanted set.", ex);
        }
    }

    /// <summary>
    /// Adds one or more module candidate names to <c>IncludeList</c> and optionally triggers an immediate symbol load.
    /// </summary>
    public void AddModuleToIncludeList(IEnumerable<string> candidateNames, bool loadSymbolsNow = true)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (candidateNames == null)
        {
            return;
        }

        try
        {
            EnsureBackupExists();

            var current = _settingsManager.GetCurrentSymbolSettings();
            var updated = current.Clone();

            var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < updated.IncludeList.Count; i++)
            {
                var item = updated.IncludeList[i];
                if (!string.IsNullOrWhiteSpace(item.Name))
                {
                    existingNames.Add(item.Name);
                }
            }

            bool addedAny = false;
            foreach (string name in candidateNames)
            {
                string clean = WildcardPattern.ExtractFileNameSafe(name);
                if (!string.IsNullOrWhiteSpace(clean) && existingNames.Add(clean))
                {
                    updated.IncludeList.Add(clean, true);
                    addedAny = true;
                }
            }

            if (addedAny || loadSymbolsNow)
            {
                _settingsManager.SaveSymbolSettings(updated, loadSymbolsNow);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to add module to IncludeList.", ex);
        }
    }

    /// <summary>
    /// Restores the user's original symbol settings boolean flags and IncludeList from backup and deletes the backup file (SPEC §7.4).
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
                RestoreSettingsToVs(backup);

                _logger.Info(
                    $"Restored user symbol settings (IsManualLoad={backup.IsManualLoad}, " +
                    $"IsLoadAdjacent={backup.IsLoadAdjacent}, AutomaticSymbolLoad={backup.AutomaticSymbolLoad}, " +
                    $"IncludeList.Count={backup.IncludeList.Count}).");
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

    private void EnsureBackupExists()
    {
        if (_activeBackup == null)
        {
            var current = _settingsManager.GetCurrentSymbolSettings();
            bool origManual = current.IsManualLoad;
            bool origAdjacent = current.IsLoadAdjacent;
            bool origAutoLoad = (current as IVsDebuggerSymbolSettings170)?.get_AutomaticSymbolLoad() ?? true;
            var origIncludeList = ReadOptionList(current.IncludeList);

            _activeBackup = _backupStore.SaveBackup(
                _backupFilePath,
                origManual,
                origAdjacent,
                origAutoLoad,
                _currentProcessId,
                origIncludeList);
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

    private void RestoreSettingsToVs(SymbolSettingsBackupDto backup)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var current = _settingsManager.GetCurrentSymbolSettings();
        var updated = current.Clone();

        updated.IsManualLoad = backup.IsManualLoad;
        updated.IsLoadAdjacent = backup.IsLoadAdjacent;
        if (updated is IVsDebuggerSymbolSettings170 updated170)
        {
            updated170.set_AutomaticSymbolLoad(backup.AutomaticSymbolLoad);
        }

        updated.IncludeList.Clear();
        if (backup.IncludeList != null)
        {
            foreach (var opt in backup.IncludeList)
            {
                if (!string.IsNullOrWhiteSpace(opt.Name))
                {
                    updated.IncludeList.Add(opt.Name, opt.IsEnabled);
                }
            }
        }

        _settingsManager.SaveSymbolSettings(updated, loadSymbolsNow: false);
    }

    private static List<SymbolOptionBackupDto> ReadOptionList(IDebugOptionList120A? optionList)
    {
        var result = new List<SymbolOptionBackupDto>();
        if (optionList == null)
        {
            return result;
        }

        int count = optionList.Count;
        for (int i = 0; i < count; i++)
        {
            try
            {
                var item = optionList[i];
                if (!string.IsNullOrWhiteSpace(item.Name))
                {
                    result.Add(new SymbolOptionBackupDto
                    {
                        Name = item.Name,
                        IsEnabled = item.IsEnabled
                    });
                }
            }
            catch
            {
                // Ignore individual item read failure
            }
        }

        return result;
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

