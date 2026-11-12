using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using VSOnDemandSymbols.Core.Abstractions;

namespace VSOnDemandSymbols.Core.State;

/// <summary>
/// Manages atomic JSON persistence for the per-VS-hive coordination directory (ADR-16):
/// <c>%LOCALAPPDATA%/OnDemandSymbols/&lt;VS_HIVE&gt;/</c>
/// Stores: <c>baseline.json</c>, <c>leases/&lt;pid&gt;.json</c>, and <c>dirty-windows.json</c>.
/// </summary>
public sealed class CoordinationStore
{
    public const string BaselineFileName = "baseline.json";
    public const string DirtyWindowsFileName = "dirty-windows.json";
    public const string LeasesFolderName = "leases";
    public const string LegacyBackupFileName = "symbol-settings-backup.json";

    private readonly IFileSystem _fileSystem;
    private readonly ILogger _logger;

    public CoordinationStore(IFileSystem fileSystem, ILogger logger)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public static string GetHiveDirectoryPath(string localAppDataDirectory, string vsHiveId)
    {
        if (string.IsNullOrWhiteSpace(localAppDataDirectory))
        {
            throw new ArgumentException("LocalAppData directory cannot be empty.", nameof(localAppDataDirectory));
        }

        string safeHiveId = SanitizeFolderName(string.IsNullOrWhiteSpace(vsHiveId) ? "Default" : vsHiveId.Trim());
        return Path.Combine(localAppDataDirectory, "OnDemandSymbols", safeHiveId);
    }

    public static string SanitizeFolderName(string name)
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

    #region Baseline

    public BaselineDto? TryLoadBaseline(string hiveDir)
    {
        string path = Path.Combine(hiveDir, BaselineFileName);
        return TryReadJson<BaselineDto>(path);
    }

    public void SaveBaseline(string hiveDir, BaselineDto baseline)
    {
        if (baseline == null)
        {
            throw new ArgumentNullException(nameof(baseline));
        }

        string path = Path.Combine(hiveDir, BaselineFileName);
        WriteJsonAtomic(path, baseline);
    }

    public void DeleteBaseline(string hiveDir)
    {
        string path = Path.Combine(hiveDir, BaselineFileName);
        DeleteFileSafe(path);
    }

    #endregion

    #region Leases

    public SessionLeaseDto? TryLoadLease(string hiveDir, int processId)
    {
        string path = GetLeaseFilePath(hiveDir, processId);
        return TryReadJson<SessionLeaseDto>(path);
    }

    public void SaveLease(string hiveDir, SessionLeaseDto lease)
    {
        if (lease == null)
        {
            throw new ArgumentNullException(nameof(lease));
        }

        string path = GetLeaseFilePath(hiveDir, lease.ProcessId);
        WriteJsonAtomic(path, lease);
    }

    public void DeleteLease(string hiveDir, int processId)
    {
        string path = GetLeaseFilePath(hiveDir, processId);
        DeleteFileSafe(path);
    }

    public IReadOnlyList<SessionLeaseDto> PruneAndListLiveLeases(
        string hiveDir,
        IProcessInfo processInfo,
        out bool anyPrunedServerMaskActive)
    {
        anyPrunedServerMaskActive = false;
        var liveLeases = new List<SessionLeaseDto>();

        string leasesDir = Path.Combine(hiveDir, LeasesFolderName);
        if (!_fileSystem.DirectoryExists(leasesDir))
        {
            return liveLeases;
        }

        IEnumerable<string> files;
        try
        {
            files = _fileSystem.EnumerateFiles(leasesDir, "*.json", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            _logger.Warning($"Failed to enumerate leases in '{leasesDir}': {ex.Message}");
            return liveLeases;
        }

        foreach (string file in files)
        {
            var lease = TryReadJson<SessionLeaseDto>(file);
            if (lease == null)
            {
                DeleteFileSafe(file);
                continue;
            }

            bool isAlive = processInfo.IsDevenvProcessAlive(lease.ProcessId, lease.ProcessStartTimeUtc);
            if (isAlive)
            {
                liveLeases.Add(lease);
            }
            else
            {
                if (lease.ServerMaskActive)
                {
                    anyPrunedServerMaskActive = true;
                }

                _logger.Warning($"Pruned dead lease for PID {lease.ProcessId} (kind={lease.Kind}, serverMaskActive={lease.ServerMaskActive}).");
                DeleteFileSafe(file);
            }
        }

        return liveLeases;
    }

    private static string GetLeaseFilePath(string hiveDir, int processId)
    {
        return Path.Combine(hiveDir, LeasesFolderName, $"{processId}.json");
    }

    #endregion

    #region Dirty Windows

    public DirtyWindowLogDto LoadDirtyWindows(string hiveDir)
    {
        string path = Path.Combine(hiveDir, DirtyWindowsFileName);
        return TryReadJson<DirtyWindowLogDto>(path) ?? new DirtyWindowLogDto();
    }

    public void RecordDirtyWindow(
        string hiveDir,
        DateTime activeSinceUtc,
        DateTime releasedUtc,
        SymbolSettingsSnapshot restoredBaseline)
    {
        var log = LoadDirtyWindows(hiveDir);
        log.Windows.Add(new DirtyWindowDto
        {
            ActiveSinceUtc = activeSinceUtc,
            ReleasedUtc = releasedUtc,
            RestoredBaseline = restoredBaseline?.Clone() ?? new SymbolSettingsSnapshot()
        });

        // Cap at MaxEntries (keep newest)
        if (log.Windows.Count > DirtyWindowLogDto.MaxEntries)
        {
            log.Windows.RemoveRange(0, log.Windows.Count - DirtyWindowLogDto.MaxEntries);
        }

        string path = Path.Combine(hiveDir, DirtyWindowsFileName);
        WriteJsonAtomic(path, log);
    }

    public SymbolSettingsSnapshot? FindOverlappingDirtyWindow(string hiveDir, DateTime processStartTimeUtc)
    {
        if (processStartTimeUtc == DateTime.MinValue)
        {
            return null;
        }

        // 1. Check if baseline is currently Active and the process started after ActiveSinceUtc
        var baseline = TryLoadBaseline(hiveDir);
        if (baseline != null && baseline.State == BaselineState.Active)
        {
            if (processStartTimeUtc >= baseline.ActiveSinceUtc)
            {
                return baseline.Snapshot;
            }
        }

        // 2. Check historical dirty windows
        var log = LoadDirtyWindows(hiveDir);
        for (int i = log.Windows.Count - 1; i >= 0; i--)
        {
            var w = log.Windows[i];
            if (processStartTimeUtc >= w.ActiveSinceUtc && processStartTimeUtc <= w.ReleasedUtc)
            {
                return w.RestoredBaseline;
            }
        }

        return null;
    }

    #endregion

    #region Legacy Migration

    public bool MigrateLegacyBackupIfNeeded(string hiveDir, IClock clock)
    {
        string legacyPath = Path.Combine(hiveDir, LegacyBackupFileName);
        if (!_fileSystem.FileExists(legacyPath))
        {
            return false;
        }

        try
        {
            string baselinePath = Path.Combine(hiveDir, BaselineFileName);
            if (!_fileSystem.FileExists(baselinePath))
            {
                var legacy = TryReadJson<SymbolSettingsBackupDto>(legacyPath);
                if (legacy != null)
                {
                    var baseline = new BaselineDto
                    {
                        State = BaselineState.Active,
                        ActiveSinceUtc = legacy.TimestampUtc != default ? legacy.TimestampUtc : clock.UtcNow,
                        Snapshot = new SymbolSettingsSnapshot
                        {
                            IsManualLoad = legacy.IsManualLoad,
                            IsLoadAdjacent = legacy.IsLoadAdjacent,
                            AutomaticSymbolLoad = legacy.AutomaticSymbolLoad,
                            UseMsSymbolServers = legacy.UseMsSymbolServers,
                            UseNugetSymbolServers = legacy.UseNugetSymbolServers,
                            IncludeList = legacy.IncludeList ?? new List<SymbolOptionBackupDto>(),
                            SymbolPaths = legacy.SymbolPaths ?? new List<SymbolOptionBackupDto>()
                        }
                    };

                    SaveBaseline(hiveDir, baseline);
                    _logger.Info($"Migrated legacy symbol settings backup to '{baselinePath}'.");
                }
            }

            DeleteFileSafe(legacyPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning($"Failed to migrate legacy symbol settings backup '{legacyPath}': {ex.Message}");
            return false;
        }
    }

    #endregion

    #region Helpers

    private T? TryReadJson<T>(string filePath) where T : class
    {
        if (string.IsNullOrWhiteSpace(filePath) || !_fileSystem.FileExists(filePath))
        {
            return null;
        }

        try
        {
            string json = _fileSystem.ReadAllText(filePath);
            return JsonConvert.DeserializeObject<T>(json);
        }
        catch (Exception ex)
        {
            _logger.Warning($"Failed to read JSON file '{filePath}': {ex.Message}");
            return null;
        }
    }

    private void WriteJsonAtomic<T>(string destinationPath, T data)
    {
        string? dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir) && !_fileSystem.DirectoryExists(dir!))
        {
            _fileSystem.CreateDirectory(dir!);
        }

        string tempPath = Path.Combine(dir ?? "", $"{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        string json = JsonConvert.SerializeObject(data, Formatting.Indented);

        try
        {
            _fileSystem.WriteAllText(tempPath, json);
            _fileSystem.ReplaceFile(tempPath, destinationPath, null);
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to write atomic file '{destinationPath}': {ex.Message}", ex);
            try
            {
                if (_fileSystem.FileExists(tempPath))
                {
                    _fileSystem.DeleteFile(tempPath);
                }
            }
            catch
            {
                // Ignore temp cleanup error
            }
            throw;
        }
    }

    private void DeleteFileSafe(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (_fileSystem.FileExists(path))
            {
                _fileSystem.DeleteFile(path);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Failed to delete file '{path}': {ex.Message}");
        }
    }

    #endregion
}
