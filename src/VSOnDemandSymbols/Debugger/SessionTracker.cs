using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Debugger;
using Microsoft.VisualStudio.Debugger.Breakpoints;
using Microsoft.VisualStudio.Debugger.Symbols;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VSOnDemandSymbols.Core.Abstractions;
using VSOnDemandSymbols.Core.Modules;
using VSOnDemandSymbols.Core.Mru;
using VSOnDemandSymbols.Core.Policy;
using VSOnDemandSymbols.Core.State;

namespace VSOnDemandSymbols.Debugger;

/// <summary>
/// Coordinates solution qualification (SPEC §7.1–§7.3), before-launch wanted-set computation (S5),
/// session start/end logging (SPEC §8.2), and MRU persistence (SPEC §5.6, §9.1).
/// </summary>
public sealed class SessionTracker : IVsSolutionEvents, IDisposable
{
    public const string DefaultAlwaysLoadPatterns = "UnrealEditor.exe;UnrealEditor-Win64-*.exe";

    private static readonly Guid Std97CmdSetGuid = new("5efc7975-14bc-11cf-9b2b-00aa00573819");
    private static readonly Guid VsDebugCmdSetGuid = new("C9DD4A59-47FB-11D2-83E7-00C04F9902C1");

    private readonly AsyncPackage _package;
    private readonly DTE2 _dte;
    private readonly IVsSolution _vsSolution;
    private readonly SymbolSettingsController _symbolSettingsController;
    private readonly ConcordSymbolBridge _bridge;
    private readonly SymbolLoader _symbolLoader;
    private readonly IFileSystem _fileSystem;
    private readonly IClock _clock;
    private readonly ILogger _logger;

    private readonly StateRepository _stateRepository;
    private readonly UnrealOwnerDetector _ownerDetector;
    private readonly UnrealManifestReader _manifestReader;
    private readonly UnrealDllNameParser _dllParser;
    private readonly UnrealResolver _unrealResolver;
    private readonly UnrealSolutionDetector _solutionDetector;
    private readonly MruStore _mruStore;
    private readonly LoadPolicy _loadPolicy;

    private readonly object _sessionLock = new();
    private readonly List<CommandEvents> _commandEventSubscriptions = new();
    private readonly ConcurrentDictionary<Guid, bool> _processQualificationCache = new();
    private readonly ConcurrentDictionary<Guid, string> _processExeNameCache = new();
    private readonly ConcurrentDictionary<Guid, string> _activeQualifyingProcesses = new();
    private readonly HashSet<string> _warnedUnresolvedBreakpointFiles = new(StringComparer.OrdinalIgnoreCase);

    private DebuggerEvents? _debuggerEvents;
    private uint _solutionEventsCookie;

    private UnrealSolutionInfo _currentSolutionInfo = UnrealSolutionInfo.NotUnreal;
    private SolutionState _currentSolutionState = new();
    private string? _currentSolutionPath;
    private string? _currentStateFilePath;
    private DateTime _lastKnownStateWriteUtc;

    private bool _isEnabled = true;
    private bool _isQualifyingSolutionOpen;
    private bool _sessionPreparedForLaunch;
    private bool _sessionStartedLogged;
    private bool _disposed;

    public bool IsEnabled
    {
        get => Volatile.Read(ref _isEnabled);
        set => Volatile.Write(ref _isEnabled, value);
    }

    public bool IsQualifyingSolutionOpen => Volatile.Read(ref _isQualifyingSolutionOpen);
    public UnrealSolutionInfo CurrentSolutionInfo => _currentSolutionInfo;
    public MruStore MruStore => _mruStore;
    public LoadPolicy LoadPolicy => _loadPolicy;
    public UnrealResolver UnrealResolver => _unrealResolver;

    public SessionTracker(
        AsyncPackage package,
        DTE2 dte,
        IVsSolution vsSolution,
        SymbolSettingsController symbolSettingsController,
        ConcordSymbolBridge bridge,
        SymbolLoader symbolLoader,
        IFileSystem fileSystem,
        IClock clock,
        ILogger logger)
    {
        _package = package ?? throw new ArgumentNullException(nameof(package));
        _dte = dte ?? throw new ArgumentNullException(nameof(dte));
        _vsSolution = vsSolution ?? throw new ArgumentNullException(nameof(vsSolution));
        _symbolSettingsController = symbolSettingsController ?? throw new ArgumentNullException(nameof(symbolSettingsController));
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _symbolLoader = symbolLoader ?? throw new ArgumentNullException(nameof(symbolLoader));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _stateRepository = new StateRepository(_fileSystem, _logger);
        _ownerDetector = new UnrealOwnerDetector(_fileSystem);
        _manifestReader = new UnrealManifestReader(_fileSystem, _logger);
        _dllParser = new UnrealDllNameParser();
        _unrealResolver = new UnrealResolver(_fileSystem, _logger, _ownerDetector, _manifestReader, _dllParser);
        _solutionDetector = new UnrealSolutionDetector(_fileSystem, _ownerDetector);
        _mruStore = new MruStore(_clock);
        _loadPolicy = new LoadPolicy(WildcardPattern.ParseMultiple(DefaultAlwaysLoadPatterns));
    }

    public void Initialize()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        _vsSolution.AdviseSolutionEvents(this, out _solutionEventsCookie);
        SubscribeBeforeLaunchAndDebuggerEvents();

        EvaluateOpenSolution();
    }

    /// <summary>
    /// Evaluates whether the currently open solution qualifies as an Unreal solution (SPEC §7.2)
    /// and applies or restores VS symbol settings accordingly (SPEC §7.4).
    /// </summary>
    public void EvaluateOpenSolution()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            string? solutionFullName = _dte.Solution?.FullName;
            if (string.IsNullOrWhiteSpace(solutionFullName))
            {
                HandleSolutionClosed();
                return;
            }

            bool isSameSolution = string.Equals(_currentSolutionPath, solutionFullName, StringComparison.OrdinalIgnoreCase);
            if (!isSameSolution)
            {
                _currentSolutionPath = solutionFullName;
                _currentStateFilePath = StateRepository.GetSolutionStateFilePath(solutionFullName!);
                LoadStateFromDisk(_currentStateFilePath);
            }

            IReadOnlyList<string> projectPaths = CollectSolutionProjectPaths();
            _currentSolutionInfo = _solutionDetector.Detect(
                solutionFullName,
                projectPaths,
                _currentSolutionState.ForceUnrealMode);

            _unrealResolver.EngineRoot = _currentSolutionInfo.EngineRoot;
            _unrealResolver.ProjectRoot = _currentSolutionInfo.ProjectRoot;

            if (!string.IsNullOrWhiteSpace(_currentSolutionInfo.ProjectName))
            {
                string projName = _currentSolutionInfo.ProjectName!;
                _dllParser.RegisterAppPrefix($"{projName}Editor");
                _dllParser.RegisterAppPrefix(projName);
            }

            bool qualifies = IsEnabled && _currentSolutionInfo.IsUnreal;
            if (qualifies)
            {
                bool newlyQualified = !_isQualifyingSolutionOpen || !isSameSolution;
                _isQualifyingSolutionOpen = true;

                // Ensure the per-solution state file exists on disk so users and tests can inspect or edit it.
                if (_currentStateFilePath != null && !_fileSystem.FileExists(_currentStateFilePath))
                {
                    SaveStateToDisk();
                }

                _symbolSettingsController.ApplyManualLoadMode();

                if (newlyQualified)
                {
                    _logger.Info(
                        $"Unreal solution detected: '{Path.GetFileName(solutionFullName)}' " +
                        $"(ProjectRoot={_currentSolutionInfo.ProjectRoot ?? "none"}, " +
                        $"EngineRoot={_currentSolutionInfo.EngineRoot ?? "none"}, " +
                        $"MRU entries={_mruStore.GetEntries().Count}).");

                    PublishWantedSetSnapshot(pruneMru: false);
                }
            }
            else if (_isQualifyingSolutionOpen)
            {
                _isQualifyingSolutionOpen = false;
                _bridge.Clear();
                _symbolSettingsController.RestoreOriginalSettings();
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to evaluate open solution.", ex);
        }
    }

    /// <summary>
    /// Runs before debug launch or attach (S5) to prune the MRU, resolve enabled breakpoints,
    /// and publish an immutable <see cref="WantedSetSnapshot"/> to <see cref="ConcordSymbolBridge"/>.
    /// </summary>
    public void PrepareSessionBeforeLaunch()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!IsQualifyingSolutionOpen || !IsEnabled)
        {
            return;
        }

        try
        {
            var sw = Stopwatch.StartNew();

            // Reload state.json if it was modified on disk outside the extension (e.g. manual test setup).
            ReloadStateIfModifiedExternally();

            _symbolSettingsController.ApplyManualLoadMode();

            lock (_sessionLock)
            {
                _processQualificationCache.Clear();
                _processExeNameCache.Clear();
                _activeQualifyingProcesses.Clear();
                _warnedUnresolvedBreakpointFiles.Clear();
                _bridge.ResetForNewSession();
                _symbolLoader.ResetForNewSession();

                PublishWantedSetSnapshot(pruneMru: true);

                _sessionPreparedForLaunch = true;
                _sessionStartedLogged = false;
            }

            sw.Stop();
            _logger.Diagnostic($"Pre-launch wanted-set preparation completed in {sw.Elapsed.TotalMilliseconds:F2} ms.");
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to prepare wanted set before launch.", ex);
        }
    }

    /// <summary>
    /// Called on the Concord worker thread to check whether a debugged process qualifies (SPEC §7.3).
    /// Cached per <see cref="DkmProcess.UniqueId"/> so pattern matching runs only once per process.
    /// </summary>
    public bool IsQualifyingProcess(DkmProcess? process, DkmModuleInstance? candidateModule = null)
    {
        if (process == null || !IsQualifyingSolutionOpen || !IsEnabled)
        {
            return false;
        }

        if (_processQualificationCache.TryGetValue(process.UniqueId, out bool cached))
        {
            return cached;
        }

        string exeName = GetProcessExecutableName(process, candidateModule);
        string? projectName = _currentSolutionInfo.ProjectName;
        bool qualifies = UnrealSolutionDetector.IsQualifyingUnrealProcess(exeName, null, projectName);

        if (qualifies || exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            _processQualificationCache[process.UniqueId] = qualifies;
        }

        return qualifies;
    }

    /// <summary>
    /// Resolves the executable file name for a <see cref="DkmProcess"/>, falling back to the loaded <c>.exe</c>
    /// <see cref="DkmModuleInstance"/> or the OS process table (<c>process.LivePart.Id</c>) when Concord's
    /// <see cref="DkmProcess.Path"/> contains a truncated/NUL-padded Win32 <c>CREATE_PROCESS_DEBUG_INFO</c> buffer.
    /// </summary>
    public string GetProcessExecutableName(DkmProcess? process, DkmModuleInstance? candidateModule = null)
    {
        if (process == null)
        {
            return string.Empty;
        }

        if (_processExeNameCache.TryGetValue(process.UniqueId, out string? cached) &&
            !string.IsNullOrEmpty(cached))
        {
            return cached!;
        }

        string resolved = ResolveProcessExecutableNameCore(process, candidateModule);
        if (resolved.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            _processExeNameCache[process.UniqueId] = resolved;
        }

        return resolved;
    }

    private static string ResolveProcessExecutableNameCore(DkmProcess process, DkmModuleInstance? candidateModule)
    {
        string fromProcessPath = WildcardPattern.ExtractFileNameSafe(process.Path);
        if (fromProcessPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return fromProcessPath;
        }

        if (candidateModule != null)
        {
            string fromModName = WildcardPattern.ExtractFileNameSafe(candidateModule.Name);
            if (fromModName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return fromModName;
            }

            string fromModFull = WildcardPattern.ExtractFileNameSafe(candidateModule.FullName);
            if (fromModFull.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return fromModFull;
            }
        }

        try
        {
            foreach (var runtime in process.GetRuntimeInstances())
            {
                foreach (var mod in runtime.GetModuleInstances())
                {
                    string modName = WildcardPattern.ExtractFileNameSafe(mod.Name);
                    if (modName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        return modName;
                    }
                }
            }
        }
        catch
        {
            // Ignore runtime enumeration errors
        }

        try
        {
            int pid = process.LivePart?.Id ?? 0;
            if (pid > 0)
            {
                using var osProc = System.Diagnostics.Process.GetProcessById(pid);
                string procName = WildcardPattern.SanitizePathString(osProc.ProcessName);
                if (!string.IsNullOrEmpty(procName))
                {
                    return procName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        ? procName
                        : procName + ".exe";
                }
            }
        }
        catch
        {
            // Ignore OS process lookup errors
        }

        return fromProcessPath;
    }

    /// <summary>
    /// Called on the Concord worker thread when the first module of a qualifying process loads.
    /// Logs the session start summary (SPEC §8.2).
    /// </summary>
    public void EnsureProcessSessionStarted(DkmProcess process, DkmModuleInstance? candidateModule = null)
    {
        if (process == null)
        {
            return;
        }

        string exeName = GetProcessExecutableName(process, candidateModule);
        if (string.IsNullOrWhiteSpace(exeName) || !exeName!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            exeName = "UnrealEditor.exe";
        }

        if (!_activeQualifyingProcesses.TryAdd(process.UniqueId, exeName))
        {
            return;
        }

        lock (_sessionLock)
        {
            if (!_sessionPreparedForLaunch)
            {
                // If launch bypassed CommandEvents.BeforeExecute, prune MRU and build snapshot from in-memory state.
                _bridge.ResetForNewSession();
                _symbolLoader.ResetForNewSession();
                IReadOnlyList<ModuleKey> pruned = _mruStore.StartSessionAndPrune();
                BuildAndPublishSnapshotCore(pruned, breakpointKeys: Array.Empty<ModuleKey>(), breakpointReasons: null);
                _sessionPreparedForLaunch = true;
            }

            if (!_sessionStartedLogged)
            {
                _sessionStartedLogged = true;
                _currentSolutionState.SessionCount++;

                var snapshot = _bridge.CurrentSnapshot;
                _logger.Info(
                    $"Session #{_currentSolutionState.SessionCount} — {exeName} — " +
                    $"wanted {snapshot.WantedKeys.Count} modules " +
                    $"(pinned {snapshot.PinnedCount}, MRU {snapshot.MruCount}, " +
                    $"breakpoints {snapshot.BreakpointCount}, always {snapshot.AlwaysCount}); " +
                    $"pruned {snapshot.PrunedCount} from MRU.");

                if (snapshot.WantedKeys.Count > 0)
                {
                    var wantedDescriptions = new List<string>();
                    foreach (var wantedKey in snapshot.WantedKeys)
                    {
                        string reason = snapshot.KeyReasons.TryGetValue(wantedKey, out string? r) ? r : "WantedSet";
                        var candidates = _unrealResolver.GetCandidateFileNames(wantedKey);
                        string binariesDesc = candidates.Count > 0
                            ? $" [{string.Join(", ", candidates.Take(3))}]"
                            : string.Empty;
                        wantedDescriptions.Add($"{wantedKey.Name}{binariesDesc} ({reason})");
                    }

                    _logger.Info($"Wanted modules: {string.Join("; ", wantedDescriptions)}");
                }
            }
        }
    }

    /// <summary>
    /// Called when a debugged process exits (<see cref="ConcordSymbolMonitor.OnProcessExit"/>).
    /// </summary>
    public void OnProcessExited(Guid processUniqueId)
    {
        _processQualificationCache.TryRemove(processUniqueId, out _);
        _processExeNameCache.TryRemove(processUniqueId, out _);
        if (_activeQualifyingProcesses.TryRemove(processUniqueId, out _) &&
            _activeQualifyingProcesses.IsEmpty)
        {
            FinalizeSessionEndIfStarted();
        }
    }

    /// <summary>
    /// Called when a user manually loads symbols for a module (e.g. from the Modules window, SPEC §5.5, ML-1).
    /// </summary>
    public void OnManualSymbolLoadDetected(DkmModuleInstance moduleInstance, ModuleKey? resolvedKey)
    {
        if (moduleInstance == null)
        {
            return;
        }

        string fileName = WildcardPattern.ExtractFileNameSafe(moduleInstance.Name);
        ModuleKey key = resolvedKey
            ?? _bridge.CurrentSnapshot.TryResolveLoadedModule(fileName)
            ?? new ModuleKey(ModuleKind.External, fileName);

        _mruStore.RecordHit(key, TriggerReason.Manual, fileName, new[] { fileName });
        _bridge.AddWantedKeyLive(key, "Manual", new[] { fileName });
        _logger.Info($"Recorded manual symbol load: {fileName} ({key}) — Manual");
    }

    /// <summary>
    /// Called when Concord reports an unbound pending breakpoint during a live session (BP-2, S4-A).
    /// </summary>
    public void OnConcordBreakpointUnbound(DkmPendingBreakpoint pendingBreakpoint)
    {
        if (pendingBreakpoint == null || !IsQualifyingSolutionOpen || !IsEnabled)
        {
            return;
        }

        if (pendingBreakpoint is DkmPendingFileLineBreakpoint fileLineBp)
        {
            DkmSourcePosition? pos = fileLineBp.GetCurrentSourcePosition();
            string docName = WildcardPattern.SanitizePathString(pos?.DocumentName);
            if (!string.IsNullOrWhiteSpace(docName))
            {
                int line = pos!.TextSpan.StartLine;
                string detail = $"{WildcardPattern.ExtractFileNameSafe(docName)}({line})";
                var keys = _unrealResolver.Resolve(docName);
                if (keys.Count == 0)
                {
                    WarnUnresolvedBreakpointOnce(docName);
                    return;
                }

                foreach (var key in keys)
                {
                    var candidates = _unrealResolver.GetCandidateFileNames(key);
                    if (!_loadPolicy.IsNeverLoad(key, candidates))
                    {
                        _mruStore.RecordHit(key, TriggerReason.Breakpoint, detail, candidates);
                    }

                    _symbolLoader.EnqueueLoad(key, TriggerReason.Breakpoint, detail, candidates);
                }
            }
        }
        else if (pendingBreakpoint is DkmPendingFunctionBreakpoint funcBp &&
                 !string.IsNullOrWhiteSpace(funcBp.ModuleName))
        {
            string modName = funcBp.ModuleName!;
            ModuleKey? key = _unrealResolver.TryParseLoadedModule(modName);
            if (key.HasValue)
            {
                string detail = $"{modName}!{funcBp.FunctionName}";
                var candidates = _unrealResolver.GetCandidateFileNames(key.Value);
                if (!_loadPolicy.IsNeverLoad(key.Value, candidates))
                {
                    _mruStore.RecordHit(key.Value, TriggerReason.Breakpoint, detail, candidates);
                }

                _symbolLoader.EnqueueLoad(key.Value, TriggerReason.Breakpoint, detail, candidates);
            }
        }
    }

    #region IVsSolutionEvents

    public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!IsQualifyingSolutionOpen || string.IsNullOrEmpty(_currentSolutionInfo.EngineRoot))
        {
            EvaluateOpenSolution();
        }

        return VSConstants.S_OK;
    }

    public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => VSConstants.S_OK;
    public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => VSConstants.S_OK;
    public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) => VSConstants.S_OK;
    public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => VSConstants.S_OK;
    public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => VSConstants.S_OK;

    public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        EvaluateOpenSolution();
        return VSConstants.S_OK;
    }

    public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => VSConstants.S_OK;

    public int OnBeforeCloseSolution(object pUnkReserved)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        HandleSolutionClosed();
        return VSConstants.S_OK;
    }

    public int OnAfterCloseSolution(object pUnkReserved)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        HandleSolutionClosed();
        return VSConstants.S_OK;
    }

    #endregion

    public void Dispose()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (_solutionEventsCookie != 0)
            {
                _vsSolution.UnadviseSolutionEvents(_solutionEventsCookie);
                _solutionEventsCookie = 0;
            }

            HandleSolutionClosed();
        }
        catch
        {
            // Ignore shutdown disposal errors
        }

        _commandEventSubscriptions.Clear();
        _debuggerEvents = null;
    }

    private void HandleSolutionClosed()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        FinalizeSessionEndIfStarted();

        if (_isQualifyingSolutionOpen)
        {
            SaveStateToDisk();
        }

        _isQualifyingSolutionOpen = false;
        _currentSolutionPath = null;
        _currentStateFilePath = null;
        _currentSolutionInfo = UnrealSolutionInfo.NotUnreal;
        _processQualificationCache.Clear();
        _processExeNameCache.Clear();
        _activeQualifyingProcesses.Clear();

        _bridge.Clear();
        _symbolSettingsController.RestoreOriginalSettings();
    }

    private void FinalizeSessionEndIfStarted()
    {
        lock (_sessionLock)
        {
            if (!_sessionStartedLogged)
            {
                _sessionPreparedForLaunch = false;
                return;
            }

            _sessionStartedLogged = false;
            _sessionPreparedForLaunch = false;
            _activeQualifyingProcesses.Clear();
            _processQualificationCache.Clear();
            _processExeNameCache.Clear();

            int totalModules = _bridge.ModuleRegistry.TotalModulesLoadedCount;
            int atLoad = _bridge.ModuleRegistry.SymbolsLoadedAtLoadCount;
            int onDemand = _bridge.ModuleRegistry.SymbolsLoadedOnDemandCount;
            int totalSymbols = atLoad + onDemand;

            _mruStore.EndSession();
            SaveStateToDisk();

            int mruCount = _mruStore.GetEntries().Count;
            _logger.Info(
                $"Session ended — {totalModules} modules loaded, symbols for {totalSymbols} " +
                $"({atLoad} at load, {onDemand} on demand). MRU: {mruCount} entries.");

            _bridge.ResetForNewSession();
            _symbolLoader.ResetForNewSession();
        }
    }

    private void PublishWantedSetSnapshot(bool pruneMru)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        IReadOnlyList<ModuleKey> pruned = pruneMru
            ? _mruStore.StartSessionAndPrune()
            : Array.Empty<ModuleKey>();

        var breakpointReasons = new Dictionary<ModuleKey, string>();
        var breakpointKeys = ResolveDteBreakpoints(recordMruHits: pruneMru, breakpointReasons);
        BuildAndPublishSnapshotCore(pruned, breakpointKeys, breakpointReasons);
    }

    private void BuildAndPublishSnapshotCore(
        IReadOnlyList<ModuleKey> pruned,
        IReadOnlyList<ModuleKey> breakpointKeys,
        IReadOnlyDictionary<ModuleKey, string>? breakpointReasons)
    {
        var bpKeySet = new HashSet<ModuleKey>(breakpointKeys);
        var entries = _mruStore.GetEntries();
        var pinnedKeys = new List<ModuleKey>();
        var mruKeys = new List<ModuleKey>();
        var keyReasons = new Dictionary<ModuleKey, string>();
        var wantedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (entry.Pinned)
            {
                pinnedKeys.Add(entry.Key);
                keyReasons[entry.Key] = "Pinned";
            }
            else if (_mruStore.Options.MruEnabled)
            {
                // Only count as an MRU carry-over if it had prior triggers or wasn't solely added by this session's breakpoint scan
                if (!bpKeySet.Contains(entry.Key) || entry.TriggerCount > 1)
                {
                    mruKeys.Add(entry.Key);
                }

                string detailSuffix = !string.IsNullOrWhiteSpace(entry.LastReasonDetail)
                    ? $": {entry.LastReasonDetail}"
                    : string.Empty;
                keyReasons[entry.Key] = $"MRU ({entry.LastReason}{detailSuffix})";
            }
        }

        foreach (var bpKey in breakpointKeys)
        {
            if (breakpointReasons != null && breakpointReasons.TryGetValue(bpKey, out string? bpReason))
            {
                keyReasons[bpKey] = bpReason;
            }
            else
            {
                var entry = _mruStore.TryGetEntry(bpKey);
                if (entry != null && entry.LastReason == TriggerReason.Breakpoint)
                {
                    keyReasons[bpKey] = SymbolLoader.FormatReason(TriggerReason.Breakpoint, entry.LastReasonDetail);
                }
                else
                {
                    keyReasons[bpKey] = "Breakpoint";
                }
            }
        }

        int alwaysCount = _loadPolicy.AlwaysLoadPatterns.Count;
        foreach (var p in _loadPolicy.AlwaysLoadPatterns)
        {
            if (p.IsUnrealModulePattern && !p.Pattern.Contains('*') && !p.Pattern.Contains('?'))
            {
                var alwaysKey = new ModuleKey(ModuleKind.Unreal, p.Pattern);
                if (!keyReasons.ContainsKey(alwaysKey))
                {
                    keyReasons[alwaysKey] = "AlwaysLoad";
                }
            }
        }

        HashSet<ModuleKey> wantedKeys = _loadPolicy.ComputeWantedSet(
            pinnedKeys,
            mruKeys,
            breakpointKeys,
            sessionTriggeredKeys: null,
            getCandidateBinaries: key => _unrealResolver.GetCandidateFileNames(key));

        foreach (var wantedKey in wantedKeys)
        {
            foreach (string candidate in _unrealResolver.GetCandidateFileNames(wantedKey))
            {
                wantedFileNames.Add(WildcardPattern.ExtractFileNameSafe(candidate));
            }

            var entry = _mruStore.TryGetEntry(wantedKey);
            if (entry != null)
            {
                foreach (string bin in entry.Binaries)
                {
                    wantedFileNames.Add(WildcardPattern.ExtractFileNameSafe(bin));
                }
            }
        }

        Dictionary<string, ModuleKey> manifestMap = BuildManifestDllToModuleMap();

        var snapshot = new WantedSetSnapshot(
            wantedKeys: wantedKeys,
            wantedFileNames: wantedFileNames,
            keyReasons: keyReasons,
            policy: _loadPolicy,
            dllParser: _dllParser,
            manifestDllToModuleMap: manifestMap,
            isAllNativeScope: false,
            pinnedCount: pinnedKeys.Count,
            mruCount: mruKeys.Count,
            breakpointCount: breakpointKeys.Count,
            alwaysCount: alwaysCount,
            prunedCount: pruned.Count);

        _bridge.PublishSnapshot(snapshot);
    }

    private IReadOnlyList<ModuleKey> ResolveDteBreakpoints(
        bool recordMruHits,
        Dictionary<ModuleKey, string> breakpointReasons)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var breakpointKeys = new HashSet<ModuleKey>();
        try
        {
            Breakpoints? bps = _dte.Debugger?.Breakpoints;
            if (bps == null)
            {
                return Array.Empty<ModuleKey>();
            }

            foreach (Breakpoint bp in bps)
            {
                if (!bp.Enabled)
                {
                    continue;
                }

                if (bp.LocationType == dbgBreakpointLocationType.dbgBreakpointLocationTypeFile)
                {
                    string? file = bp.File;
                    if (string.IsNullOrWhiteSpace(file))
                    {
                        continue;
                    }

                    var resolved = _unrealResolver.Resolve(file!);
                    if (resolved.Count == 0)
                    {
                        WarnUnresolvedBreakpointOnce(file!);
                        continue;
                    }

                    string detail = $"{WildcardPattern.ExtractFileNameSafe(file)}({bp.FileLine})";
                    foreach (var key in resolved)
                    {
                        var candidates = _unrealResolver.GetCandidateFileNames(key);
                        if (!_loadPolicy.IsNeverLoad(key, candidates))
                        {
                            if (recordMruHits)
                            {
                                _mruStore.RecordHit(key, TriggerReason.Breakpoint, detail, candidates);
                            }

                            breakpointReasons[key] = SymbolLoader.FormatReason(TriggerReason.Breakpoint, detail);
                            breakpointKeys.Add(key);
                        }
                    }
                }
                else if (bp.LocationType == dbgBreakpointLocationType.dbgBreakpointLocationTypeFunction)
                {
                    string? func = bp.FunctionName;
                    if (string.IsNullOrWhiteSpace(func))
                    {
                        continue;
                    }

                    if (TryExtractModuleFromQualifiedFunction(func!, out string? moduleDllName) &&
                        !string.IsNullOrWhiteSpace(moduleDllName))
                    {
                        ModuleKey? key = _unrealResolver.TryParseLoadedModule(moduleDllName!);
                        if (key.HasValue)
                        {
                            var candidates = _unrealResolver.GetCandidateFileNames(key.Value);
                            if (!_loadPolicy.IsNeverLoad(key.Value, candidates))
                            {
                                if (recordMruHits)
                                {
                                    _mruStore.RecordHit(key.Value, TriggerReason.Breakpoint, func, candidates);
                                }

                                breakpointReasons[key.Value] = SymbolLoader.FormatReason(TriggerReason.Breakpoint, func);
                                breakpointKeys.Add(key.Value);
                            }
                        }
                    }
                    else
                    {
                        _logger.Verbose($"Unqualified function breakpoint '{func}' cannot be pre-resolved (BP-4).");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Failed to inspect breakpoints before launch: {ex.Message}");
        }

        return breakpointKeys.ToList();
    }

    private static bool TryExtractModuleFromQualifiedFunction(string functionExpr, out string? moduleName)
    {
        moduleName = null;
        string trimmed = functionExpr.Trim();

        // Format 1: UnrealEditor-MyGame.dll!AMyActor::Tick
        int bangIndex = trimmed.IndexOf('!');
        if (bangIndex > 0)
        {
            string prefix = trimmed.Substring(0, bangIndex).Trim();
            if (!prefix.StartsWith("{", StringComparison.Ordinal))
            {
                moduleName = prefix;
                return true;
            }
        }

        // Format 2: {,,UnrealEditor-MyGame.dll}AMyActor::Tick
        if (trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            int closeBrace = trimmed.IndexOf('}');
            if (closeBrace > 1)
            {
                string inside = trimmed.Substring(1, closeBrace - 1);
                string[] parts = inside.Split(',');
                if (parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2]))
                {
                    moduleName = parts[2].Trim();
                    return true;
                }
            }
        }

        return false;
    }

    private Dictionary<string, ModuleKey> BuildManifestDllToModuleMap()
    {
        var map = new Dictionary<string, ModuleKey>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string dir in _unrealResolver.GetKnownBinariesDirectories())
            {
                if (!_fileSystem.DirectoryExists(dir))
                {
                    continue;
                }

                foreach (string manifestPath in _fileSystem.EnumerateFiles(dir, "*.modules", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        string json = _fileSystem.ReadAllText(manifestPath);
                        var dto = Newtonsoft.Json.JsonConvert.DeserializeObject<UnrealModuleManifestDto>(json);
                        if (dto?.Modules != null)
                        {
                            foreach (var kvp in dto.Modules)
                            {
                                if (!string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value))
                                {
                                    map[Path.GetFileName(kvp.Value)] = new ModuleKey(ModuleKind.Unreal, kvp.Key);
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Ignore unreadable manifest
                    }
                }
            }
        }
        catch
        {
            // Ignore directory enumeration errors
        }

        return map;
    }

    private void SubscribeBeforeLaunchAndDebuggerEvents()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        int[] stdLaunchCmdIds = { 295, 296, 248, 249, 251, 213 };
        foreach (int id in stdLaunchCmdIds)
        {
            SubscribeLaunchCommand(Std97CmdSetGuid, id);
        }

        int[] dbgLaunchCmdIds = { 0xF010, 0x0156, 0x0157 };
        foreach (int id in dbgLaunchCmdIds)
        {
            SubscribeLaunchCommand(VsDebugCmdSetGuid, id);
        }

        _debuggerEvents = _dte.Events.DebuggerEvents;
        if (_debuggerEvents != null)
        {
            _debuggerEvents.OnEnterDesignMode += OnDebuggerEnterDesignMode;
        }
    }

    private void SubscribeLaunchCommand(Guid cmdSet, int cmdId)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var cmdEvents = _dte.Events.CommandEvents[cmdSet.ToString("B"), cmdId];
        cmdEvents.BeforeExecute += OnLaunchCommandBeforeExecute;
        _commandEventSubscriptions.Add(cmdEvents);
    }

    private void OnLaunchCommandBeforeExecute(string guid, int id, object customIn, object customOut, ref bool cancelDefault)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            if (!IsQualifyingSolutionOpen || !IsEnabled)
            {
                return;
            }

            dbgDebugMode mode = _dte.Debugger?.CurrentMode ?? dbgDebugMode.dbgDesignMode;
            bool isRestart = string.Equals(guid, Std97CmdSetGuid.ToString("B"), StringComparison.OrdinalIgnoreCase) && id == 296;
            if (mode == dbgDebugMode.dbgDesignMode || isRestart)
            {
                PrepareSessionBeforeLaunch();
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Error in OnLaunchCommandBeforeExecute.", ex);
        }
    }

    private void OnDebuggerEnterDesignMode(dbgEventReason reason)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            FinalizeSessionEndIfStarted();
        }
        catch (Exception ex)
        {
            _logger.Error("Error in OnDebuggerEnterDesignMode.", ex);
        }
    }

    private IReadOnlyList<string> CollectSolutionProjectPaths()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var paths = new List<string>();
        try
        {
            Projects? projects = _dte.Solution?.Projects;
            if (projects == null)
            {
                return paths;
            }

            foreach (Project project in projects)
            {
                CollectProjectPathsRecursive(project, paths);
            }
        }
        catch
        {
            // Ignore errors from unloaded/unavailable projects
        }

        return paths;
    }

    private static void CollectProjectPathsRecursive(Project? project, List<string> paths)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (project == null)
        {
            return;
        }

        try
        {
            string? fullName = project.FullName;
            if (!string.IsNullOrWhiteSpace(fullName))
            {
                paths.Add(fullName!);
            }

            if (string.Equals(project.Kind, ProjectKinds.vsProjectKindSolutionFolder, StringComparison.OrdinalIgnoreCase) &&
                project.ProjectItems != null)
            {
                foreach (ProjectItem item in project.ProjectItems)
                {
                    if (item?.SubProject != null)
                    {
                        CollectProjectPathsRecursive(item.SubProject, paths);
                    }
                }
            }
        }
        catch
        {
            // Ignore inaccessible subproject
        }
    }

    private void LoadStateFromDisk(string stateFilePath)
    {
        _currentSolutionState = _stateRepository.Load(stateFilePath);
        StateRepository.PopulateMruStore(_currentSolutionState, _mruStore);
        _lastKnownStateWriteUtc = _fileSystem.FileExists(stateFilePath)
            ? _fileSystem.GetLastWriteTimeUtc(stateFilePath)
            : DateTime.MinValue;
    }

    private void ReloadStateIfModifiedExternally()
    {
        if (string.IsNullOrWhiteSpace(_currentStateFilePath) || !_fileSystem.FileExists(_currentStateFilePath!))
        {
            return;
        }

        DateTime diskWriteUtc = _fileSystem.GetLastWriteTimeUtc(_currentStateFilePath!);
        if (diskWriteUtc > _lastKnownStateWriteUtc)
        {
            LoadStateFromDisk(_currentStateFilePath!);
        }
    }

    private void SaveStateToDisk()
    {
        if (string.IsNullOrWhiteSpace(_currentStateFilePath))
        {
            return;
        }

        try
        {
            var updatedState = StateRepository.CreateFromMru(
                _mruStore,
                _currentSolutionState.SessionCount,
                _currentSolutionState.ForceUnrealMode);

            _currentSolutionState = updatedState;
            _stateRepository.Save(_currentStateFilePath!, updatedState);
            if (_fileSystem.FileExists(_currentStateFilePath!))
            {
                _lastKnownStateWriteUtc = _fileSystem.GetLastWriteTimeUtc(_currentStateFilePath!);
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to save solution state to '{_currentStateFilePath}'.", ex);
        }
    }

    private void WarnUnresolvedBreakpointOnce(string filePath)
    {
        lock (_warnedUnresolvedBreakpointFiles)
        {
            if (_warnedUnresolvedBreakpointFiles.Add(filePath))
            {
                _logger.Warning($"Could not resolve breakpoint file '{filePath}' to an Unreal module (BP-6).");
            }
        }
    }
}

