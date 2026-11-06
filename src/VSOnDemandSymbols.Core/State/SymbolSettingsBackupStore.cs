using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;
using VSOnDemandSymbols.Core.Abstractions;

namespace VSOnDemandSymbols.Core.State;

/// <summary>
/// Manages reading, atomic writing, and crash-recovery detection for the global
/// symbol settings backup file (<c>symbol-settings-backup.json</c>, SPEC §7.4, §9.2).
/// </summary>
public sealed class SymbolSettingsBackupStore
{
    public const string BackupFileName = "symbol-settings-backup.json";

    private readonly IFileSystem _fileSystem;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly Func<int, bool> _isDevenvProcessAlive;

    public SymbolSettingsBackupStore(
        IFileSystem fileSystem,
        IClock clock,
        ILogger logger,
        Func<int, bool>? isDevenvProcessAlive = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _isDevenvProcessAlive = isDevenvProcessAlive ?? DefaultIsDevenvProcessAlive;
    }

    public static string GetBackupFilePath(string localAppDataDirectory, string vsInstanceId)
    {
        if (string.IsNullOrWhiteSpace(localAppDataDirectory))
        {
            throw new ArgumentException("LocalAppData directory cannot be empty.", nameof(localAppDataDirectory));
        }

        string safeInstanceId = SanitizeFolderName(string.IsNullOrWhiteSpace(vsInstanceId) ? "Default" : vsInstanceId.Trim());
        return Path.Combine(localAppDataDirectory, "OnDemandSymbols", safeInstanceId, BackupFileName);
    }

    public SymbolSettingsBackupDto SaveBackup(
        string backupFilePath,
        bool isManualLoad,
        bool isLoadAdjacent,
        bool automaticSymbolLoad,
        int processId,
        IEnumerable<SymbolOptionBackupDto>? includeList = null)
    {
        if (string.IsNullOrWhiteSpace(backupFilePath))
        {
            throw new ArgumentException("Backup file path cannot be empty.", nameof(backupFilePath));
        }

        var dto = new SymbolSettingsBackupDto
        {
            IsManualLoad = isManualLoad,
            IsLoadAdjacent = isLoadAdjacent,
            AutomaticSymbolLoad = automaticSymbolLoad,
            IncludeList = includeList != null ? new List<SymbolOptionBackupDto>(includeList) : new List<SymbolOptionBackupDto>(),
            TimestampUtc = _clock.UtcNow,
            ProcessId = processId
        };

        string? dir = Path.GetDirectoryName(backupFilePath);
        if (!string.IsNullOrEmpty(dir) && !_fileSystem.DirectoryExists(dir!))
        {
            _fileSystem.CreateDirectory(dir!);
        }

        string tempPath = Path.Combine(dir ?? "", $"{Path.GetFileName(backupFilePath)}.{Guid.NewGuid():N}.tmp");
        string json = JsonConvert.SerializeObject(dto, Formatting.Indented);

        try
        {
            _fileSystem.WriteAllText(tempPath, json);
            _fileSystem.ReplaceFile(tempPath, backupFilePath, null);
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to write symbol settings backup '{backupFilePath}': {ex.Message}", ex);
            try
            {
                if (_fileSystem.FileExists(tempPath))
                {
                    _fileSystem.DeleteFile(tempPath);
                }
            }
            catch
            {
                // Ignore temp cleanup failure
            }

            throw;
        }

        return dto;
    }

    public SymbolSettingsBackupDto? TryLoadBackup(string backupFilePath)
    {
        if (string.IsNullOrWhiteSpace(backupFilePath) || !_fileSystem.FileExists(backupFilePath))
        {
            return null;
        }

        try
        {
            string json = _fileSystem.ReadAllText(backupFilePath);
            return JsonConvert.DeserializeObject<SymbolSettingsBackupDto>(json);
        }
        catch (Exception ex)
        {
            _logger.Warning($"Failed to read symbol settings backup '{backupFilePath}': {ex.Message}");
            return null;
        }
    }

    public void DeleteBackup(string backupFilePath)
    {
        if (string.IsNullOrWhiteSpace(backupFilePath))
        {
            return;
        }

        try
        {
            if (_fileSystem.FileExists(backupFilePath))
            {
                _fileSystem.DeleteFile(backupFilePath);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Failed to delete symbol settings backup '{backupFilePath}': {ex.Message}");
        }
    }

    /// <summary>
    /// Checks whether a backup file exists from a previous VS process that is no longer running (SPEC §7.4, §9.2).
    /// Returns the backup to restore if crash recovery is needed; otherwise returns null.
    /// </summary>
    public SymbolSettingsBackupDto? CheckCrashRecovery(string backupFilePath, int currentProcessId)
    {
        var backup = TryLoadBackup(backupFilePath);
        if (backup == null)
        {
            return null;
        }

        // If the backup belongs to another live devenv.exe process, do not steal/recover it.
        if (backup.ProcessId != currentProcessId && _isDevenvProcessAlive(backup.ProcessId))
        {
            return null;
        }

        return backup;
    }

    private static bool DefaultIsDevenvProcessAlive(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return false;
            }

            string processName = process.ProcessName ?? string.Empty;
            return string.Equals(processName, "devenv", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string SanitizeFolderName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0 || chars[i] == '\\' || chars[i] == '/')
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }
}

