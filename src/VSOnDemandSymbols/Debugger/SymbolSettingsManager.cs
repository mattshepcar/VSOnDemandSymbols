using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.Debugger.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VSOnDemandSymbols.Core.Abstractions;
using VSOnDemandSymbols.Core.Policy;
using VSOnDemandSymbols.Core.State;

namespace VSOnDemandSymbols.Debugger;

/// <summary>
/// Single owner of all writes to Visual Studio's symbol settings (SPEC_V2.md §4.1).
/// Backs up the user's original settings first to disk.
/// Manages manual-load mode (<c>IsManualLoad=true, IsLoadAdjacent=false, AutomaticSymbolLoad=false</c>)
/// and populates <c>IncludeList</c> with wanted binary patterns.
/// Restores the user's exact original settings on close, disable, exit, or crash recovery.
/// </summary>
public sealed class SymbolSettingsManager
{
    private readonly IVsDebuggerSymbolSettingsManager120A _settingsManager;
    private readonly SymbolSettingsBackupStore _backupStore;
    private readonly ILogger _logger;
    private readonly string _backupFilePath;
    private readonly int _currentProcessId;

    private SymbolSettingsBackupDto? _activeBackup;
    private bool _isApplied;

    public bool IsApplied => _isApplied;

    /// <summary>
    /// Resolves the per-VS-instance backup file path:
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

    public SymbolSettingsManager(
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
    /// Checks at startup whether a backup file exists from a previous crashed session and restores it.
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
    /// Puts Visual Studio into manual symbol load mode and merges wanted binaries into <c>IncludeList</c>.
    /// </summary>
    public void ApplyManualMode(IEnumerable<string> wantedBinaries, string reason = "session prepare")
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

            int userCount = existingNames.Count;
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

            _settingsManager.SaveSymbolSettings(updated, loadSymbolsNow: false);
            _isApplied = true;

            _logger.Info(
                $"IncludeList updated ({reason}): {existingNames.Count - userCount} wanted binaries + " +
                $"{userCount} user entries.");
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to apply manual mode to symbol settings.", ex);
        }
    }

    /// <summary>
    /// Restores the user's original symbol settings boolean flags and IncludeList from backup and deletes the backup file.
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
