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
/// Single owner of all writes to Visual Studio's symbol settings (SPEC.md §4.1).
/// Coordinates multi-instance symbol settings per Visual Studio hive (ADR-16).
/// Backs up original user settings to <c>baseline.json</c> before applying manual-load mode.
/// Tracks active debug sessions via kernel-locked lease files (<c>leases/&lt;pid&gt;.json</c>).
/// Restores the user's exact original settings when the last session ends or on crash recovery.
/// </summary>
public sealed class SymbolSettingsManager : IDisposable
{
    private readonly IVsDebuggerSymbolSettingsManager120A _settingsManager;
    private readonly HiveCoordinator _coordinator;
    private readonly ILogger _logger;
    private readonly IClock _clock;
    private readonly string _hiveDirectory;
    private readonly string _vsHiveId;
    private readonly int _currentProcessId;

    private readonly List<string> _currentWantedBinaries = new();
    private IFileLease? _activeLease;
    private bool _serverMaskActive;
    private bool _isApplied;
    private bool _disposed;

    public bool IsApplied => _isApplied;
    public string HiveDirectory => _hiveDirectory;
    public string VsHiveId => _vsHiveId;

    /// <summary>
    /// Resolves the VS hive ID (e.g. <c>17.0_b29c5e21</c> or <c>17.0_b29c5e21_Exp</c>).
    /// </summary>
    public static string ResolveDefaultHiveId(IVsShell? vsShell = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        string hiveId = "Default";
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
                    hiveId = segment;
                }
            }

            string? rootSuffix = TryGetCommandLineRootSuffix();
            if (!string.IsNullOrWhiteSpace(rootSuffix) &&
                !hiveId.EndsWith(rootSuffix!, StringComparison.OrdinalIgnoreCase))
            {
                hiveId = $"{hiveId}_{rootSuffix}";
            }
        }
        catch
        {
            // Fall back to Default hive
        }

        return HiveCoordinator.SanitizeFolderName(hiveId);
    }

    /// <summary>
    /// Resolves the coordination directory path: <c>%LOCALAPPDATA%/OnDemandSymbols/&lt;VS_HIVE&gt;/</c>.
    /// </summary>
    public static string ResolveDefaultHiveDirectory(IVsShell? vsShell = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Path.GetTempPath();
        }

        string hiveId = ResolveDefaultHiveId(vsShell);
        return HiveCoordinator.GetHiveDirectoryPath(localAppData, hiveId);
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
        HiveCoordinator coordinator,
        ILogger logger,
        string hiveDirectory,
        string vsHiveId,
        IClock? clock = null,
        int? currentProcessId = null)
    {
        _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _hiveDirectory = hiveDirectory ?? throw new ArgumentNullException(nameof(hiveDirectory));
        _vsHiveId = vsHiveId ?? throw new ArgumentNullException(nameof(vsHiveId));
        _clock = clock ?? SystemClock.Instance;
        _currentProcessId = currentProcessId ?? GetCurrentProcessIdSafe();
    }

    /// <summary>
    /// Reconciles symbol settings for the VS hive under the cross-process HiveLock (ADR-16).
    /// If <paramref name="wantedBinaries"/> is non-null: this instance requests manual mode with those candidate patterns.
    /// If <paramref name="wantedBinaries"/> is null: this instance releases its debug lease.
    /// Restores baseline settings if no live leases remain across the hive.
    /// </summary>
    public bool Reconcile(IEnumerable<string>? wantedBinaries, string reason = "")
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        using var hiveLock = HiveLock.TryAcquire(_vsHiveId, _logger);
        if (hiveLock == null)
        {
            _logger.Warning($"Could not acquire hive lock '{_vsHiveId}'; skipping symbol settings reconcile ({reason}).");
            return false;
        }

        try
        {
            // 1. Migrate legacy backup if present
            _coordinator.MigrateLegacyBackupIfNeeded(_hiveDirectory, _clock);

            // 2. Prune dead leases from crashed processes
            var liveLeases = new List<SessionLeaseDto>(
                _coordinator.PruneAndListLiveLeases(_hiveDirectory, out bool anyPrunedServerMaskActive));

            // 3. Update this process's lease
            if (wantedBinaries != null)
            {
                var cleanWanted = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string b in wantedBinaries)
                {
                    string clean = WildcardPattern.ExtractFileNameSafe(b);
                    if (!string.IsNullOrWhiteSpace(clean) && seen.Add(clean))
                    {
                        cleanWanted.Add(clean);
                    }
                }

                _currentWantedBinaries.Clear();
                _currentWantedBinaries.AddRange(cleanWanted);

                // Ensure baseline exists before applying manual mode
                var baseline = _coordinator.TryLoadBaseline(_hiveDirectory);
                if (baseline == null)
                {
                    var current = _settingsManager.GetCurrentSymbolSettings();
                    baseline = new BaselineDto
                    {
                        ActiveSinceUtc = _clock.UtcNow,
                        Snapshot = CaptureSnapshot(current)
                    };
                    _coordinator.SaveBaseline(_hiveDirectory, baseline);
                    _logger.Info($"Captured user symbol settings baseline (IncludeList={baseline.Snapshot.IncludeList.Count}).");
                }

                // Acquire or update lease file
                if (_activeLease == null)
                {
                    _activeLease = _coordinator.TryAcquireLease(
                        _hiveDirectory,
                        _currentProcessId,
                        cleanWanted,
                        _serverMaskActive,
                        _clock);
                }
                else
                {
                    _activeLease.Update(HiveCoordinator.SerializeLeaseContent(
                        _currentProcessId,
                        cleanWanted,
                        _serverMaskActive,
                        _clock));
                }

                // Update live leases list with this process's updated lease
                liveLeases.RemoveAll(l => l.ProcessId == _currentProcessId);
                liveLeases.Add(new SessionLeaseDto
                {
                    ProcessId = _currentProcessId,
                    WantedBinaries = cleanWanted,
                    ServerMaskActive = _serverMaskActive,
                    UpdatedUtc = _clock.UtcNow
                });
            }
            else
            {
                // Ending session: release own lease
                if (_activeLease != null)
                {
                    _activeLease.Dispose();
                    _activeLease = null;
                    _serverMaskActive = false;
                    _currentWantedBinaries.Clear();
                }

                liveLeases.RemoveAll(l => l.ProcessId == _currentProcessId);
            }

            // 4. Compute effective settings and apply
            var baselineDto = _coordinator.TryLoadBaseline(_hiveDirectory);
            if (baselineDto == null)
            {
                // No baseline recorded, nothing to modify
                _isApplied = false;
                return false;
            }

            if (liveLeases.Count == 0)
            {
                // Last lease ended -> restore baseline and delete baseline.json
                RestoreSettingsToVs(baselineDto.Snapshot, restoreSymbolServersAndPaths: anyPrunedServerMaskActive);
                _coordinator.DeleteBaseline(_hiveDirectory);
                _isApplied = false;
                _logger.Info($"Restored user symbol settings baseline ({reason}).");
                return true;
            }
            else
            {
                // Active Unreal lease(s) exist -> compute union of wanted binaries
                var effective = _coordinator.ComputeEffectiveSettings(baselineDto, liveLeases, recoverServerSettings: anyPrunedServerMaskActive);
                var current = _settingsManager.GetCurrentSymbolSettings();
                var currentSnapshot = CaptureSnapshot(current);

                if (!effective.ManagedFieldsEqual(currentSnapshot) || anyPrunedServerMaskActive)
                {
                    ApplySnapshotToVs(effective, applyServersAndPaths: anyPrunedServerMaskActive);
                    _isApplied = true;
                    _logger.Info(
                        $"Effective IncludeList updated ({reason}): {effective.IncludeList.Count} entries " +
                        $"across {liveLeases.Count} active session(s).");
                    return true;
                }

                _isApplied = true;
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to reconcile symbol settings ({reason}).", ex);
            return false;
        }
    }

    /// <summary>
    /// Restores the user's baseline settings if a baseline is currently active in the hive.
    /// Called when a non-Unreal project launches (Rule P1: correctness beats speed).
    /// </summary>
    public bool RestoreBaselineIfActive(string reason = "non-Unreal launch")
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        using var hiveLock = HiveLock.TryAcquire(_vsHiveId, _logger);
        if (hiveLock == null)
        {
            return false;
        }

        try
        {
            var baseline = _coordinator.TryLoadBaseline(_hiveDirectory);
            if (baseline == null)
            {
                return false;
            }

            var current = _settingsManager.GetCurrentSymbolSettings();
            var currentSnapshot = CaptureSnapshot(current);
            if (!baseline.Snapshot.ManagedFieldsEqual(currentSnapshot))
            {
                ApplySnapshotToVs(baseline.Snapshot, applyServersAndPaths: false);
                _logger.Info($"Restored user baseline symbol settings ({reason}) for non-Unreal target (P1).");
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to restore baseline symbol settings ({reason}).", ex);
        }

        return false;
    }

    #region Backward Compatibility Forwarders

    public bool ApplyManualMode(IEnumerable<string> wantedBinaries, string reason = "session prepare")
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return Reconcile(wantedBinaries, reason);
    }

    public void RestoreOriginalSettings()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Reconcile(null, "restore original settings");
    }

    public bool RecoverFromCrashIfNeeded()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return Reconcile(null, "startup crash recovery");
    }

    #endregion

    /// <summary>
    /// Prepares symbol settings for on-demand loading of candidate modules:
    /// temporarily merges candidate modules into <c>IncludeList</c> so Visual Studio's manual-load mode permits loading them,
    /// optionally masks remote symbol servers so only local/cached symbols are resolved,
    /// and restores <c>IncludeList</c> and server flags in <c>finally</c>.
    /// </summary>
    public void ExecuteWithCandidateModules(IEnumerable<string>? candidateModules, bool maskSymbolServers, Action loadAction)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (loadAction == null)
        {
            throw new ArgumentNullException(nameof(loadAction));
        }

        using var hiveLock = HiveLock.TryAcquire(_vsHiveId, _logger);

        var current = _settingsManager.GetCurrentSymbolSettings();
        bool origUseMs = current.UseMSSymbolServers;
        bool origUseNuget = (current as IVsDebuggerSymbolSettings161)?.get_UseNugetSymbolServers() ?? false;
        var origSymbolPaths = maskSymbolServers ? ReadOptionList(current.SymbolPaths) : null;
        var origIncludeList = ReadOptionList(current.IncludeList);

        var existingIncludeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var opt in origIncludeList)
        {
            existingIncludeNames.Add(opt.Name);
        }

        var updated = current.Clone();
        bool settingsChanged = false;

        if (maskSymbolServers)
        {
            updated.UseMSSymbolServers = false;
            if (updated is IVsDebuggerSymbolSettings161 updated161)
            {
                updated161.set_UseNugetSymbolServers(false);
            }

            updated.SymbolPaths.Clear();
            foreach (var opt in origSymbolPaths!)
            {
                bool isRemote = IsRemoteSymbolUrl(opt.Name);
                updated.SymbolPaths.Add(opt.Name, opt.IsEnabled && !isRemote);
            }

            settingsChanged = true;
        }

        bool includeListChanged = false;
        if (candidateModules != null)
        {
            foreach (string candidate in candidateModules)
            {
                string clean = WildcardPattern.ExtractFileNameSafe(candidate);
                if (!string.IsNullOrWhiteSpace(clean) && existingIncludeNames.Add(clean))
                {
                    updated.IncludeList.Add(clean, true);
                    includeListChanged = true;
                    settingsChanged = true;
                }
            }
        }

        try
        {
            if (maskSymbolServers)
            {
                _serverMaskActive = true;
                _activeLease?.Update(HiveCoordinator.SerializeLeaseContent(
                    _currentProcessId,
                    _currentWantedBinaries,
                    serverMaskActive: true,
                    _clock));
            }

            if (settingsChanged)
            {
                _settingsManager.SaveSymbolSettings(updated, loadSymbolsNow: false);
            }

            loadAction();
        }
        finally
        {
            if (maskSymbolServers || includeListChanged)
            {
                try
                {
                    var restored = _settingsManager.GetCurrentSymbolSettings().Clone();
                    if (maskSymbolServers)
                    {
                        restored.UseMSSymbolServers = origUseMs;
                        if (restored is IVsDebuggerSymbolSettings161 restored161)
                        {
                            restored161.set_UseNugetSymbolServers(origUseNuget);
                        }

                        restored.SymbolPaths.Clear();
                        foreach (var opt in origSymbolPaths!)
                        {
                            restored.SymbolPaths.Add(opt.Name, opt.IsEnabled);
                        }
                    }

                    if (includeListChanged)
                    {
                        restored.IncludeList.Clear();
                        foreach (var opt in origIncludeList)
                        {
                            restored.IncludeList.Add(opt.Name, opt.IsEnabled);
                        }
                    }

                    _settingsManager.SaveSymbolSettings(restored, loadSymbolsNow: false);
                }
                catch (Exception ex)
                {
                    _logger.Error("Failed to restore symbol settings after on-demand load pass.", ex);
                }
                finally
                {
                    if (maskSymbolServers)
                    {
                        _serverMaskActive = false;
                        _activeLease?.Update(HiveCoordinator.SerializeLeaseContent(
                            _currentProcessId,
                            _currentWantedBinaries,
                            serverMaskActive: false,
                            _clock));
                    }
                }
            }
        }
    }

    private static bool IsRemoteSymbolUrl(string? pathOrUrl)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl)) return false;
        return pathOrUrl!.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns <see langword="true"/> if remote symbol servers (Microsoft Symbol Servers, NuGet Symbol Servers,
    /// or HTTP/HTTPS symbol paths) are currently enabled in the user's symbol settings.
    /// </summary>
    public bool HasActiveRemoteSymbolServers()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var current = _settingsManager.GetCurrentSymbolSettings();
            if (current.UseMSSymbolServers)
            {
                return true;
            }

            if (current is IVsDebuggerSymbolSettings161 current161 && current161.get_UseNugetSymbolServers())
            {
                return true;
            }

            var paths = ReadOptionList(current.SymbolPaths);
            foreach (var opt in paths)
            {
                if (opt.IsEnabled && IsRemoteSymbolUrl(opt.Name))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Ignore read failure
        }

        return false;
    }

    /// <summary>
    /// Checks whether a local <c>.pdb</c> file exists for <paramref name="moduleFileName"/> either adjacent to
    /// <paramref name="fullModulePath"/> or inside Visual Studio's configured symbol cache / local symbol paths.
    /// Calling <c>EnvDTE90.Module.LoadSymbols(string.Empty)</c> when no local PDB exists and symbol servers are
    /// disabled causes Visual Studio to pop up a modal Win32 "Browse for PDB" file dialog on the UI thread.
    /// </summary>
    public bool HasLocalSymbolFile(string moduleFileName, string? fullModulePath)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            if (!string.IsNullOrWhiteSpace(fullModulePath))
            {
                string adjacentPdb = Path.ChangeExtension(fullModulePath!, ".pdb");
                if (!string.IsNullOrWhiteSpace(adjacentPdb) && File.Exists(adjacentPdb))
                {
                    return true;
                }
            }

            string cleanName = WildcardPattern.ExtractFileNameSafe(moduleFileName);
            if (string.IsNullOrWhiteSpace(cleanName))
            {
                return false;
            }

            string pdbFileName = Path.ChangeExtension(cleanName, ".pdb");
            if (string.IsNullOrWhiteSpace(pdbFileName))
            {
                return false;
            }

            var searchDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var current = _settingsManager.GetCurrentSymbolSettings();
            if (!string.IsNullOrWhiteSpace(current.CachePath))
            {
                searchDirs.Add(current.CachePath);
            }

            string defaultSymCache = Path.Combine(Path.GetTempPath(), "SymbolCache");
            if (!string.IsNullOrWhiteSpace(defaultSymCache))
            {
                searchDirs.Add(defaultSymCache);
            }

            var paths = ReadOptionList(current.SymbolPaths);
            foreach (var opt in paths)
            {
                if (opt.IsEnabled && !string.IsNullOrWhiteSpace(opt.Name) && !IsRemoteSymbolUrl(opt.Name))
                {
                    searchDirs.Add(opt.Name);
                }
            }

            foreach (string dir in searchDirs)
            {
                if (ContainsLocalPdbInDirectory(dir, pdbFileName))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Ignore filesystem or settings errors
        }

        return false;
    }

    private static bool ContainsLocalPdbInDirectory(string rootDir, string pdbFileName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir))
            {
                return false;
            }

            // 1. Flat symbol path: <rootDir>\<module>.pdb
            string flatFile = Path.Combine(rootDir, pdbFileName);
            if (File.Exists(flatFile))
            {
                return true;
            }

            // 2. Two-tier symstore layout: <rootDir>\<module>.pdb\<guidAge>\<module>.pdb
            if (Directory.Exists(flatFile))
            {
                foreach (string subDir in Directory.EnumerateDirectories(flatFile))
                {
                    if (File.Exists(Path.Combine(subDir, pdbFileName)))
                    {
                        return true;
                    }
                }
            }

            // 3. Three-tier symstore layout: <rootDir>\dll\<module>.pdb or <rootDir>\<2-char-prefix>\<module>.pdb
            string prefix = pdbFileName.Length >= 2 ? pdbFileName.Substring(0, 2) : string.Empty;
            string[] prefixes = string.IsNullOrEmpty(prefix)
                ? new[] { "dll", "exe" }
                : new[] { "dll", "exe", prefix };

            foreach (string sub in prefixes)
            {
                string tierPath = Path.Combine(rootDir, sub, pdbFileName);
                if (File.Exists(tierPath))
                {
                    return true;
                }

                if (Directory.Exists(tierPath))
                {
                    foreach (string guidDir in Directory.EnumerateDirectories(tierPath))
                    {
                        if (File.Exists(Path.Combine(guidDir, pdbFileName)))
                        {
                            return true;
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore directory enumeration errors
        }

        return false;
    }

    private static SymbolSettingsSnapshot CaptureSnapshot(IVsDebuggerSymbolSettings120A current)
    {
        return new SymbolSettingsSnapshot
        {
            IsManualLoad = current.IsManualLoad,
            IsLoadAdjacent = current.IsLoadAdjacent,
            AutomaticSymbolLoad = (current as IVsDebuggerSymbolSettings170)?.get_AutomaticSymbolLoad() ?? true,
            UseMsSymbolServers = current.UseMSSymbolServers,
            UseNugetSymbolServers = (current as IVsDebuggerSymbolSettings161)?.get_UseNugetSymbolServers() ?? false,
            IncludeList = ReadOptionList(current.IncludeList),
            SymbolPaths = ReadOptionList(current.SymbolPaths)
        };
    }

    private void ApplySnapshotToVs(SymbolSettingsSnapshot snapshot, bool applyServersAndPaths)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var current = _settingsManager.GetCurrentSymbolSettings();
        var updated = current.Clone();

        updated.IsManualLoad = snapshot.IsManualLoad;
        updated.IsLoadAdjacent = snapshot.IsLoadAdjacent;
        if (updated is IVsDebuggerSymbolSettings170 updated170)
        {
            updated170.set_AutomaticSymbolLoad(snapshot.AutomaticSymbolLoad);
        }

        if (applyServersAndPaths)
        {
            updated.UseMSSymbolServers = snapshot.UseMsSymbolServers;
            if (updated is IVsDebuggerSymbolSettings161 updated161)
            {
                updated161.set_UseNugetSymbolServers(snapshot.UseNugetSymbolServers);
            }

            if (snapshot.SymbolPaths != null && snapshot.SymbolPaths.Count > 0)
            {
                updated.SymbolPaths.Clear();
                foreach (var opt in snapshot.SymbolPaths)
                {
                    if (!string.IsNullOrWhiteSpace(opt.Name))
                    {
                        updated.SymbolPaths.Add(opt.Name, opt.IsEnabled);
                    }
                }
            }
        }

        updated.IncludeList.Clear();
        if (snapshot.IncludeList != null)
        {
            foreach (var opt in snapshot.IncludeList)
            {
                if (!string.IsNullOrWhiteSpace(opt.Name))
                {
                    updated.IncludeList.Add(opt.Name, opt.IsEnabled);
                }
            }
        }

        _settingsManager.SaveSymbolSettings(updated, loadSymbolsNow: false);
    }

    private void RestoreSettingsToVs(SymbolSettingsSnapshot snapshot, bool restoreSymbolServersAndPaths)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        ApplySnapshotToVs(snapshot, restoreSymbolServersAndPaths);
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_activeLease != null)
        {
            try
            {
                if (ThreadHelper.CheckAccess())
                {
#pragma warning disable VSTHRD010 // Accessing main-thread method after CheckAccess verification
                    Reconcile(null, "manager disposed");
#pragma warning restore VSTHRD010
                }
            }
            catch
            {
                // Ignore errors during disposal reconcile
            }

            try
            {
                _activeLease?.Dispose();
            }
            catch
            {
                // Ignore disposal errors
            }
            _activeLease = null;
        }
    }
}
