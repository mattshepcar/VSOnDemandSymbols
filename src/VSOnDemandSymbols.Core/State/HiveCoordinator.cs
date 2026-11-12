using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using VSOnDemandSymbols.Core.Abstractions;
using VSOnDemandSymbols.Core.Policy;

namespace VSOnDemandSymbols.Core.State;

/// <summary>
/// Coordinates multi-instance symbol settings per Visual Studio hive (ADR-16).
/// Uses Windows kernel file handle locks (FileShare.Read) on <c>leases/&lt;pid&gt;.json</c>
/// to track live debug sessions and self-heal dead leases without process inspection.
/// </summary>
public sealed class HiveCoordinator
{
    public const string BaselineFileName = "baseline.json";
    public const string LeasesFolderName = "leases";
    public const string LegacyBackupFileName = "symbol-settings-backup.json";

    private readonly IFileSystem _fileSystem;
    private readonly ILogger _logger;

    public HiveCoordinator(IFileSystem fileSystem, ILogger logger)
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

    #region Leases & Liveness

    public IFileLease? TryAcquireLease(
        string hiveDir,
        int processId,
        IEnumerable<string>? wantedBinaries,
        bool serverMaskActive,
        IClock clock)
    {
        string path = GetLeaseFilePath(hiveDir, processId);
        var dto = new SessionLeaseDto
        {
            ProcessId = processId,
            WantedBinaries = wantedBinaries != null ? new List<string>(wantedBinaries) : new List<string>(),
            ServerMaskActive = serverMaskActive,
            UpdatedUtc = clock.UtcNow
        };

        string json = JsonConvert.SerializeObject(dto, Formatting.Indented);
        return _fileSystem.TryAcquireLease(path, json);
    }

    public static string SerializeLeaseContent(
        int processId,
        IEnumerable<string>? wantedBinaries,
        bool serverMaskActive,
        IClock clock)
    {
        var dto = new SessionLeaseDto
        {
            ProcessId = processId,
            WantedBinaries = wantedBinaries != null ? new List<string>(wantedBinaries) : new List<string>(),
            ServerMaskActive = serverMaskActive,
            UpdatedUtc = clock.UtcNow
        };
        return JsonConvert.SerializeObject(dto, Formatting.Indented);
    }

    public IReadOnlyList<SessionLeaseDto> PruneAndListLiveLeases(
        string hiveDir,
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
            var lease = TryReadSharedJson<SessionLeaseDto>(file);

            // Attempt to delete the file.
            // If the owning devenv.exe is still alive, it holds an open handle (FileShare.ReadWrite)
            // and TryDeleteFile will return false (sharing violation).
            // If the process has exited or crashed, the OS kernel released the handle and TryDeleteFile succeeds!
            bool deleted = _fileSystem.TryDeleteFile(file);
            if (deleted)
            {
                // File was deleted -> dead lease from crashed process was successfully pruned!
                if (lease != null && lease.ServerMaskActive)
                {
                    anyPrunedServerMaskActive = true;
                }
                _logger.Warning($"Pruned dead lease '{Path.GetFileName(file)}' (serverMaskActive={lease?.ServerMaskActive ?? false}).");
            }
            else
            {
                // File is locked by an actively running process -> lease is LIVE
                if (lease != null)
                {
                    liveLeases.Add(lease);
                }
            }
        }

        return liveLeases;
    }

    private static string GetLeaseFilePath(string hiveDir, int processId)
    {
        return Path.Combine(hiveDir, LeasesFolderName, $"{processId}.json");
    }

    #endregion

    #region Effective Settings Calculation

    public SymbolSettingsSnapshot ComputeEffectiveSettings(
        BaselineDto baseline,
        IReadOnlyList<SessionLeaseDto>? liveLeases,
        bool recoverServerSettings = false)
    {
        if (baseline == null)
        {
            throw new ArgumentNullException(nameof(baseline));
        }

        var baselineSnapshot = baseline.Snapshot ?? new SymbolSettingsSnapshot();

        // If no live leases remain -> restore baseline settings
        if (liveLeases == null || liveLeases.Count == 0)
        {
            return baselineSnapshot.Clone();
        }

        // Live Unreal leases exist -> apply manual mode and merge wanted binaries
        var merged = baselineSnapshot.Clone();
        merged.IsManualLoad = true;
        merged.IsLoadAdjacent = false;
        merged.AutomaticSymbolLoad = false;

        var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var opt in merged.IncludeList)
        {
            if (!string.IsNullOrWhiteSpace(opt.Name))
            {
                existingNames.Add(opt.Name);
            }
        }

        foreach (var lease in liveLeases)
        {
            if (lease.WantedBinaries != null)
            {
                foreach (string binary in lease.WantedBinaries)
                {
                    string clean = WildcardPattern.ExtractFileNameSafe(binary);
                    if (!string.IsNullOrWhiteSpace(clean) && existingNames.Add(clean))
                    {
                        merged.IncludeList.Add(new SymbolOptionBackupDto
                        {
                            Name = clean,
                            IsEnabled = true
                        });
                    }
                }
            }
        }

        if (recoverServerSettings)
        {
            merged.UseMsSymbolServers = baselineSnapshot.UseMsSymbolServers;
            merged.UseNugetSymbolServers = baselineSnapshot.UseNugetSymbolServers;
            merged.SymbolPaths = new List<SymbolOptionBackupDto>(baselineSnapshot.SymbolPaths.Count);
            foreach (var p in baselineSnapshot.SymbolPaths)
            {
                merged.SymbolPaths.Add(new SymbolOptionBackupDto { Name = p.Name, IsEnabled = p.IsEnabled });
            }
        }

        return merged;
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

    private T? TryReadSharedJson<T>(string filePath) where T : class
    {
        if (string.IsNullOrWhiteSpace(filePath) || !_fileSystem.FileExists(filePath))
        {
            return null;
        }

        try
        {
            string json = _fileSystem.ReadSharedText(filePath);
            return JsonConvert.DeserializeObject<T>(json);
        }
        catch (Exception ex)
        {
            _logger.Warning($"Failed to read shared JSON file '{filePath}': {ex.Message}");
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

