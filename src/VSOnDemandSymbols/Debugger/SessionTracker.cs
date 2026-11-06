using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Debugger;
using Microsoft.VisualStudio.Debugger.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using VSOnDemandSymbols.Core.Abstractions;
using VSOnDemandSymbols.Core.Modules;
using VSOnDemandSymbols.Core.Mru;
using VSOnDemandSymbols.Core.Policy;
using VSOnDemandSymbols.Core.State;
using VSOnDemandSymbols.Projects;

namespace VSOnDemandSymbols.Debugger;

/// <summary>
/// Coordinates solution qualification (SPEC §7.1–§7.3), before-launch wanted-set computation (S5),
/// breakpoint triggers (BP-1–BP-7), call-stack triggers (CS-1–CS-6), frame-activation triggers (FA-1–FA-2),
/// manual-load recording (ML-1), session start/end logging (SPEC §8.2), and MRU persistence (SPEC §5.6, §9.1).
/// </summary>
public sealed class SessionTracker : IVsSolutionEvents, IDisposable
{
    public const string DefaultAlwaysLoadPatterns = "UnrealEditor.exe;UnrealEditor-Win64-*.exe";

    private static readonly Guid Std97CmdSetGuid = new("5efc7975-14bc-11cf-9b2b-00aa00573819");
    private static readonly Guid VsDebugCmdSetGuid = new("C9DD4A59-47FB-11D2-83E7-00C04F9902C1");

    private const int CmdIdBreakAll = 246;

    private readonly AsyncPackage _package;
    private readonly JoinableTaskFactory _joinableTaskFactory;
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
    private readonly ProjectModelProvider _projectModelProvider;
    private readonly MsBuildResolver _msBuildResolver;
    private readonly CompositeResolver _compositeResolver;
    private readonly UnrealSolutionDetector _solutionDetector;
    private readonly MruStore _mruStore;
    private readonly LoadPolicy _loadPolicy;
    private readonly CallStackEvaluator _callStackEvaluator;

    private readonly object _sessionLock = new();
    private readonly List<CommandEvents> _commandEventSubscriptions = new();
    private readonly ConcurrentDictionary<Guid, bool> _processQualificationCache = new();
    private readonly ConcurrentDictionary<Guid, string> _processExeNameCache = new();
    private readonly ConcurrentDictionary<Guid, string> _activeQualifyingProcesses = new();
    private readonly HashSet<string> _warnedUnresolvedBreakpointFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<uint> _examinedThreadIdsThisBreak = new();
    private HashSet<string> _lastDesignModeBreakpointSignature = new(StringComparer.OrdinalIgnoreCase);

    private DebuggerEvents? _debuggerEvents;
    private SelectionEvents? _selectionEvents;
    private uint _solutionEventsCookie;

    private UnrealSolutionInfo _currentSolutionInfo = UnrealSolutionInfo.NotUnreal;
    private SolutionState _currentSolutionState = new();
    private string? _currentSolutionPath;
    private string? _currentStateFilePath;
    private DateTime _lastKnownStateWriteUtc;

    private IDebugProgram2? _activeBreakProgram;
    private IDebugThread2? _activeBreakThread;
    private dbgEventReason _lastDteBreakReason = dbgEventReason.dbgEventReasonNone;
    private bool _userBreakRequested;
    private bool _ignoreInitialBreakContextChange;
    private bool _breakEvaluatedThisPause;
    private int _lastSeenDesignModeBpCount = -1;

    private SymbolScope _scope = SymbolScope.UnrealOnly;
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

    public SymbolScope Scope
    {
        get => _scope;
        set
        {
            _scope = value;
            _compositeResolver.Scope = value;
        }
    }

    public bool IsQualifyingSolutionOpen => Volatile.Read(ref _isQualifyingSolutionOpen);
    public UnrealSolutionInfo CurrentSolutionInfo => _currentSolutionInfo;
    public MruStore MruStore => _mruStore;
    public LoadPolicy LoadPolicy => _loadPolicy;
    public UnrealResolver UnrealResolver => _unrealResolver;
    public MsBuildResolver MsBuildResolver => _msBuildResolver;
    public CompositeResolver CompositeResolver => _compositeResolver;
    public ProjectModelProvider ProjectModelProvider => _projectModelProvider;

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
        _joinableTaskFactory = package.JoinableTaskFactory;
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
        _projectModelProvider = new ProjectModelProvider(_fileSystem, _logger);
        _msBuildResolver = new MsBuildResolver(_projectModelProvider);
        _compositeResolver = new CompositeResolver(_unrealResolver, _msBuildResolver)
        {
            Scope = _scope
        };
        _solutionDetector = new UnrealSolutionDetector(_fileSystem, _ownerDetector);
        _mruStore = new MruStore(_clock);
        _loadPolicy = new LoadPolicy(WildcardPattern.ParseMultiple(DefaultAlwaysLoadPatterns));
        _callStackEvaluator = new CallStackEvaluator(_loadPolicy);
    }

    public void Initialize()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        _vsSolution.AdviseSolutionEvents(this, out _solutionEventsCookie);
        SubscribeBeforeLaunchAndDebuggerEvents();

        EvaluateOpenSolution();
    }

    /// <summary>
    /// Evaluates whether the currently open solution qualifies under <see cref="Scope"/> (SPEC §7.1, §7.2)
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

            var msBuildGraph = _projectModelProvider.RebuildSnapshot(_dte);
            _msBuildResolver.InvalidateCache();

            _compositeResolver.IsUnrealSolution = _currentSolutionInfo.IsUnreal;
            _compositeResolver.Scope = _scope;

            bool qualifies = IsEnabled &&
                _scope != SymbolScope.Off &&
                (_currentSolutionInfo.IsUnreal ||
                 (_scope == SymbolScope.AllNative && (msBuildGraph.Projects.Count > 0 || projectPaths.Count > 0)));

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
                    string modeLabel = _currentSolutionInfo.IsUnreal ? "Unreal" : "Native C++";
                    _logger.Info(
                        $"{modeLabel} solution detected: '{Path.GetFileName(solutionFullName)}' " +
                        $"(ProjectRoot={_currentSolutionInfo.ProjectRoot ?? "none"}, " +
                        $"EngineRoot={_currentSolutionInfo.EngineRoot ?? "none"}, " +
                        $"MRU entries={_mruStore.GetEntries().Count}).");

                    PublishWantedSetSnapshot(pruneMru: false);
                    _lastDesignModeBreakpointSignature = CaptureCurrentBreakpointSignature();
                    _lastSeenDesignModeBpCount = _dte.Debugger?.Breakpoints?.Count ?? 0;
                    _symbolSettingsController.ApplyWantedSetToIncludeList(_bridge.CurrentSnapshot.WantedFileNames, "solution opened");
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

            if (_scope == SymbolScope.AllNative)
            {
                _projectModelProvider.RebuildSnapshot(_dte);
                _msBuildResolver.InvalidateCache();
            }

            _symbolSettingsController.ApplyManualLoadMode();

            lock (_sessionLock)
            {
                _processQualificationCache.Clear();
                _processExeNameCache.Clear();
                _activeQualifyingProcesses.Clear();
                _warnedUnresolvedBreakpointFiles.Clear();
                ResetBreakState();
                _bridge.ResetForNewSession();
                _symbolLoader.ResetForNewSession();

                PublishWantedSetSnapshot(pruneMru: true);

                _sessionPreparedForLaunch = true;
                _sessionStartedLogged = false;
            }

            var snapshot = _bridge.CurrentSnapshot;
            _symbolSettingsController.ApplyWantedSetToIncludeList(snapshot.WantedFileNames, "before launch");

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
        if (process == null || !IsQualifyingSolutionOpen || !IsEnabled || _scope == SymbolScope.Off)
        {
            return false;
        }

        if (_processQualificationCache.TryGetValue(process.UniqueId, out bool cached))
        {
            return cached;
        }

        string exeName = GetProcessExecutableName(process, candidateModule);
        bool qualifies;
        if (_scope == SymbolScope.AllNative && !_currentSolutionInfo.IsUnreal)
        {
            qualifies = !string.IsNullOrWhiteSpace(exeName);
        }
        else
        {
            string? projectName = _currentSolutionInfo.ProjectName;
            qualifies = UnrealSolutionDetector.IsQualifyingUnrealProcess(exeName, null, projectName) ||
                        (_scope == SymbolScope.AllNative && !string.IsNullOrWhiteSpace(exeName));
        }

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
            exeName = _currentSolutionInfo.IsUnreal ? "UnrealEditor.exe" : "NativeTarget.exe";
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

                _logger.Warning(
                    "Before-launch hook did not run for this session; IncludeList is being applied late " +
                    "(modules already mapped will be loaded on demand).");
                _ = _joinableTaskFactory.RunAsync(ApplyIncludeListLateAsync);
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
                        var candidates = _compositeResolver.GetCandidateFileNames(wantedKey);
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
    /// Called (coalesced, on the UI thread) when the debugger reports a breakpoint that could not bind
    /// during a live session or when a breakpoint command executes while debugging (BP-2, D-19).
    /// Re-resolves the DTE breakpoints and queues on-demand loads for their modules.
    /// </summary>
    public void OnBreakpointBindErrorReported()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!IsQualifyingSolutionOpen || !IsEnabled || !_loadPolicy.LoadOnBreakpoints)
        {
            return;
        }

        lock (_sessionLock)
        {
            if (!_sessionStartedLogged)
            {
                return;
            }
        }

        const string prefix = "Breakpoint: ";
        var reasons = new Dictionary<ModuleKey, string>();
        foreach (var key in ResolveDteBreakpoints(recordMruHits: true, reasons))
        {
            string? detail = reasons.TryGetValue(key, out string? r) && r.StartsWith(prefix, StringComparison.Ordinal)
                ? r.Substring(prefix.Length)
                : null;
            _symbolLoader.EnqueueLoad(key, TriggerReason.Breakpoint, detail, _compositeResolver.GetCandidateFileNames(key));
        }
    }

    /// <summary>
    /// Called asynchronously on the UI thread by <see cref="Ad7BreakpointEventListener"/> when execution stops
    /// on a break, exception, breakpoint, or step event (SPEC §5.2 CS-1–CS-6).
    /// </summary>
    public void OnAd7BreakEvent(
        IDebugProgram2? program,
        IDebugThread2? thread,
        bool isExceptionEvent,
        bool isHardBreakCandidate)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            if (!IsQualifyingSolutionOpen || !IsEnabled)
            {
                return;
            }

            lock (_sessionLock)
            {
                if (!_sessionStartedLogged)
                {
                    return;
                }
            }

            if (program == null && thread != null)
            {
                thread.GetProgram(out program);
            }

            _activeBreakProgram = program;
            _activeBreakThread = thread;
            _ignoreInitialBreakContextChange = false;

            bool isDteException =
                _lastDteBreakReason == dbgEventReason.dbgEventReasonExceptionThrown ||
                _lastDteBreakReason == dbgEventReason.dbgEventReasonExceptionNotHandled;

            bool isUserOrNormalDteBreak =
                _userBreakRequested ||
                _lastDteBreakReason == dbgEventReason.dbgEventReasonUserBreak ||
                _lastDteBreakReason == dbgEventReason.dbgEventReasonBreakpoint ||
                _lastDteBreakReason == dbgEventReason.dbgEventReasonStep;

            bool isExceptionOrHardBreak = isExceptionEvent || isDteException || (isHardBreakCandidate && !isUserOrNormalDteBreak);
            _userBreakRequested = false;
            _breakEvaluatedThisPause = true;

            EvaluateCallStacksOnBreakCore(program, thread, isExceptionOrHardBreak);
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to evaluate call stack on break.", ex);
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
        else if (_scope == SymbolScope.AllNative)
        {
            _projectModelProvider.RebuildSnapshot(_dte);
            _msBuildResolver.InvalidateCache();
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

            if (_debuggerEvents != null)
            {
                _debuggerEvents.OnEnterDesignMode -= OnDebuggerEnterDesignMode;
                _debuggerEvents.OnEnterBreakMode -= OnDebuggerEnterBreakMode;
                _debuggerEvents.OnEnterRunMode -= OnDebuggerEnterRunMode;
                _debuggerEvents.OnContextChanged -= OnDebuggerContextChanged;
            }

            if (_selectionEvents != null)
            {
                _selectionEvents.OnChange -= OnSelectionChanged;
            }

            HandleSolutionClosed();
        }
        catch
        {
            // Ignore shutdown disposal errors
        }

        _commandEventSubscriptions.Clear();
        _debuggerEvents = null;
        _selectionEvents = null;
    }

    private void EvaluateCallStacksOnBreakCore(
        IDebugProgram2? program,
        IDebugThread2? breakingThread,
        bool isExceptionOrHardBreak)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!_loadPolicy.ShouldExamineCallStackOnBreak())
        {
            return;
        }

        bool examineAllThreads = _loadPolicy.ShouldExamineAllThreadsOnBreak(isExceptionOrHardBreak);
        int maxFrames = Math.Max(1, _loadPolicy.CallStackMaxFrames);

        var threadInputs = CollectAd7CallStackThreads(program, breakingThread, examineAllThreads, maxFrames);
        if (threadInputs.Count == 0)
        {
            threadInputs = CollectDteCallStackThreads(examineAllThreads, maxFrames);
        }

        if (threadInputs.Count == 0)
        {
            return;
        }

        var snapshot = _bridge.CurrentSnapshot;
        var decisions = _callStackEvaluator.EvaluateBreak(
            threadInputs,
            isExceptionOrHardBreak,
            _examinedThreadIdsThisBreak,
            fileName => snapshot.TryResolveLoadedModule(fileName),
            key => _compositeResolver.GetCandidateFileNames(key));

        foreach (var decision in decisions)
        {
            _mruStore.RecordHit(decision.Key, TriggerReason.CallStack, decision.Detail, decision.CandidateFileNames);
            _symbolLoader.EnqueueLoad(decision.Key, TriggerReason.CallStack, decision.Detail, decision.CandidateFileNames);
        }
    }

    private IReadOnlyList<CallStackThreadInput> CollectAd7CallStackThreads(
        IDebugProgram2? program,
        IDebugThread2? breakingThread,
        bool examineAllThreads,
        int maxFrames)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var results = new List<CallStackThreadInput>();
        uint breakingThreadId = 0;
        if (breakingThread != null)
        {
            breakingThread.GetThreadId(out breakingThreadId);
        }

        if (!examineAllThreads)
        {
            if (breakingThread != null)
            {
                var frames = ReadAd7ThreadFrames(breakingThread, maxFrames);
                results.Add(new CallStackThreadInput(breakingThreadId, isCurrentThread: true, frames));
            }

            return results;
        }

        // Examine all threads in the program
        if (program != null &&
            ErrorHandler.Succeeded(program.EnumThreads(out IEnumDebugThreads2? enumThreads)) &&
            enumThreads != null)
        {
            var threadArr = new IDebugThread2[1];
            uint fetched = 0;
            var seenThreadIds = new HashSet<uint>();

            while (ErrorHandler.Succeeded(enumThreads.Next(1, threadArr, ref fetched)) &&
                   fetched == 1 &&
                   threadArr[0] != null)
            {
                IDebugThread2 t = threadArr[0];
                t.GetThreadId(out uint tid);
                if (!seenThreadIds.Add(tid))
                {
                    continue;
                }

                bool isCurrent = breakingThreadId != 0 && tid == breakingThreadId;
                var frames = ReadAd7ThreadFrames(t, maxFrames);
                results.Add(new CallStackThreadInput(tid, isCurrent, frames));
            }
        }
        else if (breakingThread != null)
        {
            var frames = ReadAd7ThreadFrames(breakingThread, maxFrames);
            results.Add(new CallStackThreadInput(breakingThreadId, isCurrentThread: true, frames));
        }

        return results;
    }

    private IReadOnlyList<CallStackFrameInput> ReadAd7ThreadFrames(IDebugThread2 thread, int maxFrames)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var frames = new List<CallStackFrameInput>();
        try
        {
            const enum_FRAMEINFO_FLAGS flags =
                enum_FRAMEINFO_FLAGS.FIF_MODULE |
                enum_FRAMEINFO_FLAGS.FIF_DEBUG_MODULEP |
                enum_FRAMEINFO_FLAGS.FIF_FUNCNAME;

            if (!ErrorHandler.Succeeded(thread.EnumFrameInfo(flags, 10, out IEnumDebugFrameInfo2? enumFrames)) ||
                enumFrames == null)
            {
                return frames;
            }

            var frameArr = new FRAMEINFO[1];
            uint fetched = 0;
            int count = 0;

            while (count < maxFrames &&
                   ErrorHandler.Succeeded(enumFrames.Next(1, frameArr, ref fetched)) &&
                   fetched == 1)
            {
                count++;
                FRAMEINFO fi = frameArr[0];
                string moduleName = WildcardPattern.ExtractFileNameSafe(fi.m_bstrModule);
                string? funcName = fi.m_bstrFuncName;
                bool hasSymbols = false;

                if (fi.m_pModule != null)
                {
                    var modInfo = new MODULE_INFO[1];
                    if (ErrorHandler.Succeeded(fi.m_pModule.GetInfo(
                        enum_MODULE_INFO_FIELDS.MIF_NAME | enum_MODULE_INFO_FIELDS.MIF_FLAGS,
                        modInfo)))
                    {
                        if (string.IsNullOrEmpty(moduleName))
                        {
                            moduleName = WildcardPattern.ExtractFileNameSafe(modInfo[0].m_bstrName);
                        }

                        hasSymbols = (modInfo[0].m_dwModuleFlags & enum_MODULE_FLAGS.MODULE_FLAG_SYMBOLS) != 0;
                    }

                    if (!string.IsNullOrEmpty(moduleName))
                    {
                        _symbolLoader.RegisterAd7Module(moduleName, fi.m_pModule);
                    }
                }

                if (!hasSymbols && !string.IsNullOrEmpty(moduleName))
                {
                    hasSymbols = IsModuleSymbolsLoadedInRegistry(moduleName);
                }

                if (!string.IsNullOrEmpty(moduleName))
                {
                    frames.Add(new CallStackFrameInput(moduleName, funcName, hasSymbols));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Verbose($"Failed to enumerate AD7 stack frames: {ex.Message}");
        }

        return frames;
    }

    private IReadOnlyList<CallStackThreadInput> CollectDteCallStackThreads(bool examineAllThreads, int maxFrames)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var results = new List<CallStackThreadInput>();
        try
        {
            EnvDTE.Thread? currentThread = _dte.Debugger?.CurrentThread;
            uint currentTid = (uint)(currentThread?.ID ?? 0);

            if (!examineAllThreads)
            {
                if (currentThread != null)
                {
                    results.Add(new CallStackThreadInput(
                        currentTid,
                        isCurrentThread: true,
                        ReadDteThreadFrames(currentThread, maxFrames)));
                }

                return results;
            }

            Threads? allThreads = _dte.Debugger?.CurrentProgram?.Threads;
            if (allThreads != null)
            {
                foreach (EnvDTE.Thread t in allThreads)
                {
                    uint tid = (uint)t.ID;
                    bool isCurrent = currentTid != 0 && tid == currentTid;
                    results.Add(new CallStackThreadInput(tid, isCurrent, ReadDteThreadFrames(t, maxFrames)));
                }
            }
            else if (currentThread != null)
            {
                results.Add(new CallStackThreadInput(
                    currentTid,
                    isCurrentThread: true,
                    ReadDteThreadFrames(currentThread, maxFrames)));
            }
        }
        catch (Exception ex)
        {
            _logger.Verbose($"Failed to enumerate DTE stack frames: {ex.Message}");
        }

        return results;
    }

    private IReadOnlyList<CallStackFrameInput> ReadDteThreadFrames(EnvDTE.Thread dteThread, int maxFrames)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var frames = new List<CallStackFrameInput>();
        try
        {
            StackFrames? stackFrames = dteThread.StackFrames;
            if (stackFrames == null)
            {
                return frames;
            }

            int count = 0;
            foreach (EnvDTE.StackFrame sf in stackFrames)
            {
                if (count >= maxFrames)
                {
                    break;
                }

                count++;
                string modName = WildcardPattern.ExtractFileNameSafe(sf.Module);
                if (string.IsNullOrEmpty(modName))
                {
                    continue;
                }

                bool hasSymbols = IsModuleSymbolsLoadedInRegistry(modName);
                frames.Add(new CallStackFrameInput(modName, sf.FunctionName, hasSymbols));
            }
        }
        catch
        {
            // Ignore DTE frame enumeration errors
        }

        return frames;
    }

    private bool IsModuleSymbolsLoadedInRegistry(string moduleFileName)
    {
        var matches = _bridge.ModuleRegistry.GetLoadedModulesByFileName(moduleFileName);
        if (matches.Count == 0)
        {
            return false;
        }

        foreach (var match in matches)
        {
            if (match.HasSymbols)
            {
                return true;
            }
        }

        return false;
    }

    private void ResetBreakState()
    {
        _examinedThreadIdsThisBreak.Clear();
        _activeBreakProgram = null;
        _activeBreakThread = null;
        _lastDteBreakReason = dbgEventReason.dbgEventReasonNone;
        _userBreakRequested = false;
        _ignoreInitialBreakContextChange = false;
        _breakEvaluatedThisPause = false;
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
        _projectModelProvider.Clear();
        _msBuildResolver.InvalidateCache();
        _processQualificationCache.Clear();
        _processExeNameCache.Clear();
        _activeQualifyingProcesses.Clear();
        _lastDesignModeBreakpointSignature.Clear();
        _lastSeenDesignModeBpCount = -1;
        ResetBreakState();

        _bridge.Clear();
        _symbolSettingsController.RestoreOriginalSettings();
    }

    private void FinalizeSessionEndIfStarted()
    {
        lock (_sessionLock)
        {
            ResetBreakState();

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

        // D-18: Do NOT clear IncludeList at session end. Re-populate it with the next session's wanted set
        // (now including this session's MRU hits) so it is already in place before the next launch,
        // independent of whether the before-launch hook fires. The user's original IncludeList is only
        // restored on solution close, disable, VS exit, or crash recovery.
        _ = _joinableTaskFactory.RunAsync(RefreshIncludeListForNextSessionAsync);
    }

    private async Task RefreshIncludeListForNextSessionAsync()
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync();

        try
        {
            if (!IsQualifyingSolutionOpen || !IsEnabled)
            {
                return;
            }

            lock (_sessionLock)
            {
                // A new launch was already prepared (e.g. Restart); its fresher wanted set must not be overwritten.
                if (_sessionPreparedForLaunch || _sessionStartedLogged)
                {
                    return;
                }
            }

            PublishWantedSetSnapshot(pruneMru: false);
            _lastDesignModeBreakpointSignature = CaptureCurrentBreakpointSignature();
            _lastSeenDesignModeBpCount = _dte.Debugger?.Breakpoints?.Count ?? 0;
            _symbolSettingsController.ApplyWantedSetToIncludeList(
                _bridge.CurrentSnapshot.WantedFileNames,
                "session ended; pre-populating for next launch");
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to refresh IncludeList after session end.", ex);
        }
    }

    private async Task ApplyIncludeListLateAsync()
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync();

        try
        {
            _symbolSettingsController.ApplyWantedSetToIncludeList(
                _bridge.CurrentSnapshot.WantedFileNames,
                "late apply: before-launch hook was missed",
                loadSymbolsNow: true);
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to apply IncludeList after missed before-launch hook.", ex);
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
            getCandidateBinaries: key => _compositeResolver.GetCandidateFileNames(key));

        foreach (var wantedKey in wantedKeys)
        {
            foreach (string candidate in _compositeResolver.GetCandidateFileNames(wantedKey))
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
            isAllNativeScope: _scope == SymbolScope.AllNative,
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

        if (!_loadPolicy.LoadOnBreakpoints)
        {
            return Array.Empty<ModuleKey>();
        }

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
                // BP-3: Disabled breakpoints are ignored by default unless IncludeDisabledBreakpoints is true.
                if (!bp.Enabled && !_loadPolicy.IncludeDisabledBreakpoints)
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

                    var resolved = _compositeResolver.Resolve(file!);
                    if (resolved.Count == 0)
                    {
                        WarnUnresolvedBreakpointOnce(file!);
                        continue;
                    }

                    string detail = $"{WildcardPattern.ExtractFileNameSafe(file)}({bp.FileLine})";
                    foreach (var key in resolved)
                    {
                        var candidates = _compositeResolver.GetCandidateFileNames(key);
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

                    if (FunctionBreakpointParser.TryExtractModuleName(func!, out string? moduleDllName) &&
                        !string.IsNullOrWhiteSpace(moduleDllName))
                    {
                        string candidateModuleName = moduleDllName!;
                        ModuleKey? key = _compositeResolver.TryParseLoadedModule(candidateModuleName);
                        if (!key.HasValue && !candidateModuleName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                            !candidateModuleName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            key = _compositeResolver.TryParseLoadedModule(candidateModuleName + ".dll");
                        }

                        if (key.HasValue)
                        {
                            var candidates = _compositeResolver.GetCandidateFileNames(key.Value);
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
            _logger.Warning($"Failed to inspect breakpoints: {ex.Message}");
        }

        return breakpointKeys.ToList();
    }

    private Dictionary<string, ModuleKey> BuildManifestDllToModuleMap()
    {
        var map = new Dictionary<string, ModuleKey>(StringComparer.OrdinalIgnoreCase);
        if (!_currentSolutionInfo.IsUnreal)
        {
            return map;
        }

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

        // Launch commands (BeforeExecute)
        int[] stdLaunchCmdIds = { 295, 296, 248, 249, 251, 213, 356 };
        foreach (int id in stdLaunchCmdIds)
        {
            SubscribeCommand(Std97CmdSetGuid, id, beforeLaunch: true, afterBreakpoint: false);
        }

        int[] dbgLaunchCmdIds = { 0xF010, 0x0156, 0x0157 };
        foreach (int id in dbgLaunchCmdIds)
        {
            SubscribeCommand(VsDebugCmdSetGuid, id, beforeLaunch: true, afterBreakpoint: false);
        }

        // BreakAll command (BeforeExecute) to distinguish user Pause (M5) from target __debugbreak() (M6)
        var breakCmdEvents = _dte.Events.CommandEvents[Std97CmdSetGuid.ToString("B"), CmdIdBreakAll];
        breakCmdEvents.BeforeExecute += OnBreakAllCommandBeforeExecute;
        _commandEventSubscriptions.Add(breakCmdEvents);

        // Breakpoint toggle / add / delete / enable / disable commands (AfterExecute, Strategy B)
        int[] stdBpCmdIds = { 255, 256, 375, 376 };
        foreach (int id in stdBpCmdIds)
        {
            SubscribeCommand(Std97CmdSetGuid, id, beforeLaunch: false, afterBreakpoint: true);
        }

        int[] dbgBpCmdIds = { 0x0122, 0x0123, 0x0124, 0x0137, 0x0041, 0x1003, 0x1014, 0x1015 };
        foreach (int id in dbgBpCmdIds)
        {
            SubscribeCommand(VsDebugCmdSetGuid, id, beforeLaunch: false, afterBreakpoint: true);
        }

        _debuggerEvents = _dte.Events.DebuggerEvents;
        if (_debuggerEvents != null)
        {
            _debuggerEvents.OnEnterDesignMode += OnDebuggerEnterDesignMode;
            _debuggerEvents.OnEnterBreakMode += OnDebuggerEnterBreakMode;
            _debuggerEvents.OnEnterRunMode += OnDebuggerEnterRunMode;
            _debuggerEvents.OnContextChanged += OnDebuggerContextChanged;
        }

        _selectionEvents = _dte.Events.SelectionEvents;
        if (_selectionEvents != null)
        {
            _selectionEvents.OnChange += OnSelectionChanged;
        }
    }

    private void SubscribeCommand(Guid cmdSet, int cmdId, bool beforeLaunch, bool afterBreakpoint)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var cmdEvents = _dte.Events.CommandEvents[cmdSet.ToString("B"), cmdId];
        if (beforeLaunch)
        {
            cmdEvents.BeforeExecute += OnLaunchCommandBeforeExecute;
        }

        if (afterBreakpoint)
        {
            cmdEvents.AfterExecute += OnBreakpointCommandAfterExecute;
        }

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

    private void OnBreakAllCommandBeforeExecute(string guid, int id, object customIn, object customOut, ref bool cancelDefault)
    {
        _userBreakRequested = true;
    }

    private void OnBreakpointCommandAfterExecute(string guid, int id, object customIn, object customOut)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            if (!IsQualifyingSolutionOpen || !IsEnabled)
            {
                return;
            }

            dbgDebugMode mode = _dte.Debugger?.CurrentMode ?? dbgDebugMode.dbgDesignMode;
            if (mode == dbgDebugMode.dbgDesignMode)
            {
                RefreshDesignModeBreakpointsIfChanged("breakpoint command");
            }
            else
            {
                OnBreakpointBindErrorReported();
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Error in OnBreakpointCommandAfterExecute.", ex);
        }
    }

    private void OnSelectionChanged()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            if (!IsQualifyingSolutionOpen || !IsEnabled)
            {
                return;
            }

            dbgDebugMode mode = _dte.Debugger?.CurrentMode ?? dbgDebugMode.dbgDesignMode;
            if (mode == dbgDebugMode.dbgDesignMode)
            {
                int currentBpCount = _dte.Debugger?.Breakpoints?.Count ?? 0;
                if (currentBpCount != _lastSeenDesignModeBpCount)
                {
                    _lastSeenDesignModeBpCount = currentBpCount;
                    RefreshDesignModeBreakpointsIfChanged("editor margin / breakpoint list change");
                }
            }
            else if (mode == dbgDebugMode.dbgBreakMode &&
                     !_ignoreInitialBreakContextChange &&
                     _breakEvaluatedThisPause &&
                     _loadPolicy.LoadOnFrameActivation)
            {
                string? activeWindowCaption = _dte.ActiveWindow?.Caption;
                if (!string.IsNullOrEmpty(activeWindowCaption) &&
                    (activeWindowCaption!.IndexOf("Call Stack", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     activeWindowCaption.IndexOf("Threads", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     activeWindowCaption.IndexOf("Parallel Stacks", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    EvaluateDteFrameActivation(_dte.Debugger?.CurrentThread, _dte.Debugger?.CurrentStackFrame);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Verbose($"Error in OnSelectionChanged: {ex.Message}");
        }
    }

    private void RefreshDesignModeBreakpointsIfChanged(string reasonDetail)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var currentSignature = CaptureCurrentBreakpointSignature();
        if (currentSignature.SetEquals(_lastDesignModeBreakpointSignature))
        {
            return;
        }

        _lastDesignModeBreakpointSignature = currentSignature;
        _lastSeenDesignModeBpCount = _dte.Debugger?.Breakpoints?.Count ?? 0;

        PublishWantedSetSnapshot(pruneMru: false);
        _symbolSettingsController.ApplyWantedSetToIncludeList(
            _bridge.CurrentSnapshot.WantedFileNames,
            $"design-mode {reasonDetail}");
    }

    private HashSet<string> CaptureCurrentBreakpointSignature()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var sig = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Breakpoints? bps = _dte.Debugger?.Breakpoints;
            if (bps != null)
            {
                foreach (Breakpoint bp in bps)
                {
                    sig.Add($"{bp.LocationType}|{bp.File}|{bp.FileLine}|{bp.FunctionName}|{bp.Enabled}");
                }
            }
        }
        catch
        {
            // Ignore transient DTE breakpoint read failures
        }

        return sig;
    }

    private void OnDebuggerEnterBreakMode(dbgEventReason reason, ref dbgExecutionAction executionAction)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            if (!IsQualifyingSolutionOpen || !IsEnabled)
            {
                return;
            }

            _examinedThreadIdsThisBreak.Clear();
            _lastDteBreakReason = reason;
            _ignoreInitialBreakContextChange = true;
            _breakEvaluatedThisPause = false;

            if (reason == dbgEventReason.dbgEventReasonUserBreak)
            {
                _userBreakRequested = true;
            }

            // Fallback in case AD7 break event did not fire
            _ = _joinableTaskFactory.RunAsync(async () =>
            {
                await _joinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: true);
                try
                {
                    if (!_breakEvaluatedThisPause &&
                        _dte.Debugger?.CurrentMode == dbgDebugMode.dbgBreakMode)
                    {
                        OnAd7BreakEvent(
                            _activeBreakProgram,
                            _activeBreakThread,
                            isExceptionEvent: reason == dbgEventReason.dbgEventReasonExceptionThrown ||
                                              reason == dbgEventReason.dbgEventReasonExceptionNotHandled,
                            isHardBreakCandidate: false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error("Error in fallback break-mode evaluation.", ex);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.Error("Error in OnDebuggerEnterBreakMode.", ex);
        }
    }

    private void OnDebuggerEnterRunMode(dbgEventReason reason)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            ResetBreakState();
        }
        catch (Exception ex)
        {
            _logger.Error("Error in OnDebuggerEnterRunMode.", ex);
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

    private void OnDebuggerContextChanged(
        EnvDTE.Process newProcess,
        EnvDTE.Program newProgram,
        EnvDTE.Thread newThread,
        EnvDTE.StackFrame newStackFrame)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            if (!IsQualifyingSolutionOpen || !IsEnabled || !_loadPolicy.LoadOnFrameActivation)
            {
                return;
            }

            if (_dte.Debugger?.CurrentMode != dbgDebugMode.dbgBreakMode)
            {
                return;
            }

            if (_ignoreInitialBreakContextChange)
            {
                _ignoreInitialBreakContextChange = false;
                return;
            }

            EvaluateDteFrameActivation(newThread, newStackFrame);
        }
        catch (Exception ex)
        {
            _logger.Error("Error in OnDebuggerContextChanged (FA-1/FA-2).", ex);
        }
    }

    private void EvaluateDteFrameActivation(EnvDTE.Thread? dteThread, EnvDTE.StackFrame? dteFrame)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        CallStackFrameInput? activatedFrame = null;
        if (dteFrame != null)
        {
            string modName = WildcardPattern.ExtractFileNameSafe(dteFrame.Module);
            if (!string.IsNullOrEmpty(modName))
            {
                bool hasSymbols = IsModuleSymbolsLoadedInRegistry(modName);
                activatedFrame = new CallStackFrameInput(modName, dteFrame.FunctionName, hasSymbols);
            }
        }

        CallStackThreadInput? activatedThread = null;
        if (dteThread != null)
        {
            uint tid = (uint)dteThread.ID;
            if (!_examinedThreadIdsThisBreak.Contains(tid))
            {
                int maxFrames = Math.Max(1, _loadPolicy.CallStackMaxFrames);
                IReadOnlyList<CallStackFrameInput>? ad7Frames = TryReadAd7FramesForThreadId(tid, maxFrames);
                IReadOnlyList<CallStackFrameInput> frames = (ad7Frames != null && ad7Frames.Count > 0)
                    ? ad7Frames
                    : ReadDteThreadFrames(dteThread, maxFrames);
                activatedThread = new CallStackThreadInput(tid, isCurrentThread: true, frames);
            }
            else
            {
                activatedThread = new CallStackThreadInput(tid, isCurrentThread: true, Array.Empty<CallStackFrameInput>());
            }
        }

        if (!activatedFrame.HasValue && activatedThread == null)
        {
            return;
        }

        var snapshot = _bridge.CurrentSnapshot;
        var decisions = _callStackEvaluator.EvaluateFrameActivation(
            activatedFrame,
            activatedThread,
            _examinedThreadIdsThisBreak,
            fileName => snapshot.TryResolveLoadedModule(fileName),
            key => _compositeResolver.GetCandidateFileNames(key));

        foreach (var decision in decisions)
        {
            _mruStore.RecordHit(decision.Key, TriggerReason.FrameActivated, decision.Detail, decision.CandidateFileNames);
            _symbolLoader.EnqueueLoad(decision.Key, TriggerReason.FrameActivated, decision.Detail, decision.CandidateFileNames);
        }
    }

    private IReadOnlyList<CallStackFrameInput>? TryReadAd7FramesForThreadId(uint targetThreadId, int maxFrames)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_activeBreakProgram == null || targetThreadId == 0)
        {
            return null;
        }

        try
        {
            if (ErrorHandler.Succeeded(_activeBreakProgram.EnumThreads(out IEnumDebugThreads2? enumThreads)) &&
                enumThreads != null)
            {
                var threadArr = new IDebugThread2[1];
                uint fetched = 0;
                while (ErrorHandler.Succeeded(enumThreads.Next(1, threadArr, ref fetched)) &&
                       fetched == 1 &&
                       threadArr[0] != null)
                {
                    threadArr[0].GetThreadId(out uint tid);
                    if (tid == targetThreadId)
                    {
                        return ReadAd7ThreadFrames(threadArr[0], maxFrames);
                    }
                }
            }
        }
        catch
        {
            // Fall back to DTE thread frames
        }

        return null;
    }

    private IReadOnlyList<string> CollectSolutionProjectPaths()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var paths = new List<string>();
        try
        {
            EnvDTE.Projects? projects = _dte.Solution?.Projects;
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
                _logger.Warning($"Could not resolve breakpoint file '{filePath}' to a module (BP-6).");
            }
        }
    }
}
