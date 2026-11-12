using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Stopwatch = System.Diagnostics.Stopwatch;
using EnvDTE;
using EnvDTE80;
using EnvDTE90a;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Debugger.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using VSOnDemandSymbols.Core.Abstractions;
using VSOnDemandSymbols.Core.Modules;
using VSOnDemandSymbols.Core.Policy;

namespace VSOnDemandSymbols.Debugger;

/// <summary>
/// Monitors debugger launch (F5), break events, and live breakpoint modifications
/// to maintain <c>IncludeList</c> and trigger on-demand symbol loading (SPEC.md §4.1).
/// </summary>
public sealed class DebuggerSessionMonitor : IDebugEventCallback2, IDisposable
{
    private readonly DTE2 _dte;
    private readonly IVsDebugger _vsDebugger;
    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly SymbolSettingsManager _settings;
    private readonly UnrealModuleResolver _resolver;
    private readonly ILogger _logger;

    private readonly CommandEvents _launchCommandEvents;
    private readonly DebuggerEvents _debuggerEvents;
    private readonly HashSet<string> _loadedModulesThisSession = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _symbolizedModulesThisSession = new(StringComparer.OrdinalIgnoreCase);

    private int _rescanPending;
    private int _lastInspectedThreadId;
    private IDebugProgram2? _activeAd7Program;
    private IDebugProcess2? _activeAd7Process;
    private IDebugThread2? _lastAd7EventThread;
    private bool _advised;
    private bool _disposed;

    /// <summary>
    /// Gets or sets whether session monitoring and symbol management are currently active.
    /// Automatically activated when an Unreal solution is open and deactivated when closed.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// Gets or sets the delegate providing the current <see cref="BreakSymbolLoadMode"/>.
    /// Defaults to <see cref="BreakSymbolLoadMode.LocalOnly"/>.
    /// </summary>
    public Func<BreakSymbolLoadMode> BreakSymbolLoadModeProvider { get; set; } = () => BreakSymbolLoadMode.LocalOnly;

    /// <summary>
    /// Current break-mode symbol loading mode, evaluated on demand from <see cref="BreakSymbolLoadModeProvider"/>.
    /// </summary>
    public BreakSymbolLoadMode BreakSymbolLoadMode => BreakSymbolLoadModeProvider();

    public DebuggerSessionMonitor(
        DTE2 dte,
        IVsDebugger vsDebugger,
        JoinableTaskFactory joinableTaskFactory,
        SymbolSettingsManager settings,
        UnrealModuleResolver resolver,
        ILogger logger)
    {
        _dte = dte ?? throw new ArgumentNullException(nameof(dte));
        _vsDebugger = vsDebugger ?? throw new ArgumentNullException(nameof(vsDebugger));
        _joinableTaskFactory = joinableTaskFactory ?? throw new ArgumentNullException(nameof(joinableTaskFactory));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ThreadHelper.ThrowIfNotOnUIThread();

        // Intercept F5 / Debug.Start as fallback pre-launch hook (ADR-15 Spike S2).
        // Uses Standard Command Set 97 (guidVSStd97 from stdidcmd.h) and cmdidStart (295).
        _launchCommandEvents = _dte.Events.get_CommandEvents(
            VSConstants.CMDSETID.StandardCommandSet97_string,
            (int)VSConstants.VSStd97CmdID.Start);
        _launchCommandEvents.BeforeExecute += OnBeforeLaunch;

        // Debugger event subscriptions (SPEC.md §4.1)
        _debuggerEvents = _dte.Events.DebuggerEvents;
        _debuggerEvents.OnEnterBreakMode += OnEnterBreakMode;
        _debuggerEvents.OnEnterDesignMode += OnEnterDesignMode;
        _debuggerEvents.OnContextChanged += OnContextChanged;

        _advised = ErrorHandler.Succeeded(_vsDebugger.AdviseDebugEventCallback(this));
        if (!_advised)
        {
            _logger.Warning("Could not subscribe to debugger events; new breakpoints during a session will not trigger symbol loads.");
        }
    }

    /// <summary>
    /// Resolves breakpoints (both enabled and disabled) to candidate binary names.
    /// Always includes the UnrealEditor executable wildcard pattern.
    /// </summary>
    public IReadOnlyList<string> ResolveWantedBinariesFromBreakpoints(out int breakpointCount)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        breakpointCount = 0;

        var wantedBinaries = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "UnrealEditor*.exe"
        };

        if (_dte.Debugger?.Breakpoints != null)
        {
            foreach (Breakpoint bp in _dte.Debugger.Breakpoints)
            {
                if (string.IsNullOrWhiteSpace(bp.File))
                {
                    continue;
                }

                string? moduleName = _resolver.ResolveModuleFromSource(bp.File);
                if (!string.IsNullOrWhiteSpace(moduleName))
                {
                    breakpointCount++;
                    foreach (string binary in _resolver.GetCandidateBinaries(moduleName!))
                    {
                        wantedBinaries.Add(binary);
                    }
                }
            }
        }

        return new List<string>(wantedBinaries);
    }

    /// <summary>
    /// Resolves breakpoints (both enabled and disabled) to candidate binary names and populates Visual Studio's <c>IncludeList</c>.
    /// Returns the list of candidate binary wildcard patterns for modules with breakpoints.
    /// </summary>
    public IReadOnlyList<string> UpdateIncludeListFromBreakpoints(string reason = "before launch")
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!IsEnabled)
        {
            return Array.Empty<string>();
        }

        try
        {
            var totalSw = Stopwatch.StartNew();
            var wantedBinaries = ResolveWantedBinariesFromBreakpoints(out int breakpointCount);
            long resolveMs = totalSw.ElapsedMilliseconds;

            var applySw = Stopwatch.StartNew();
            bool applied = _settings.Reconcile(wantedBinaries, reason: $"{reason} (breakpoints: {breakpointCount})");
            long applyMs = applySw.ElapsedMilliseconds;
            totalSw.Stop();

            if (applied || !string.Equals(reason, "before launch", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Info(
                    $"[Profile] UpdateIncludeList ({reason}): total={totalSw.ElapsedMilliseconds}ms " +
                    $"(resolve={resolveMs}ms [breakpoints={breakpointCount}], apply={applyMs}ms)");
            }
            else
            {
                _logger.Diagnostic(
                    $"[Profile] UpdateIncludeList ({reason}, unchanged): total={totalSw.ElapsedMilliseconds}ms " +
                    $"(resolve={resolveMs}ms [breakpoints={breakpointCount}], apply={applyMs}ms)");
            }

            var candidatePatterns = new List<string>();
            foreach (string b in wantedBinaries)
            {
                if (!b.StartsWith("UnrealEditor*", StringComparison.OrdinalIgnoreCase))
                {
                    candidatePatterns.Add(b);
                }
            }
            return candidatePatterns;
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to update IncludeList from breakpoints.", ex);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Handles AD7 debug events asynchronously to observe breakpoint binding errors mid-session.
    /// </summary>
    public int Event(
        IDebugEngine2 pEngine,
        IDebugProcess2 pProcess,
        IDebugProgram2 pProgram,
        IDebugThread2 pThread,
        IDebugEvent2 pEvent,
        ref Guid riidEvent,
        uint dwAttrib)
    {
        try
        {
            if (!IsEnabled)
            {
                return VSConstants.S_OK;
            }

            if (pProgram != null)
            {
                _activeAd7Program = pProgram;
            }

            if (pProcess != null)
            {
                _activeAd7Process = pProcess;
            }

            if (pThread != null)
            {
                _lastAd7EventThread = pThread;
            }

            if (riidEvent == typeof(IDebugBreakpointErrorEvent2).GUID &&
                Interlocked.CompareExchange(ref _rescanPending, 1, 0) == 0)
            {
                // Coalesce bursts of error events into a single rescan on the UI thread
                _ = _joinableTaskFactory.RunAsync(OnBreakpointErrorAsync);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Error handling debugger event.", ex);
        }

        return VSConstants.S_OK;
    }

    private async Task OnBreakpointErrorAsync()
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: true);
        Interlocked.Exchange(ref _rescanPending, 0);

        if (!IsEnabled)
        {
            return;
        }

        try
        {
            HandleBreakpointBindError();
        }
        catch (Exception ex)
        {
            _logger.Error("Error handling breakpoint bind error.", ex);
        }
    }

    private void HandleBreakpointBindError()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!IsEnabled)
        {
            return;
        }

        var totalSw = Stopwatch.StartNew();
        var includeListSw = Stopwatch.StartNew();
        var candidatePatterns = UpdateIncludeListFromBreakpoints("breakpoint bind error");
        long includeListMs = includeListSw.ElapsedMilliseconds;
        if (candidatePatterns.Count == 0 || _dte.Debugger?.DebuggedProcesses == null)
        {
            return;
        }

        var compiledPatterns = new List<WildcardPattern>(candidatePatterns.Count);
        for (int i = 0; i < candidatePatterns.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(candidatePatterns[i]))
            {
                compiledPatterns.Add(new WildcardPattern(candidatePatterns[i]));
            }
        }

        if (compiledPatterns.Count == 0)
        {
            return;
        }

        try
        {
            var scanSw = Stopwatch.StartNew();
            long symbolLoadMs = 0;
            int totalModulesScanned = 0;
            int loadedCount = 0;

            foreach (EnvDTE.Process proc in _dte.Debugger.DebuggedProcesses)
            {
                if (proc is EnvDTE90.Process3 proc3 && proc3.Modules != null)
                {
                    foreach (EnvDTE90.Module mod in proc3.Modules)
                    {
                        totalModulesScanned++;
                        string modFileName = WildcardPattern.ExtractFileNameSafe(mod.Name);
                        if (string.IsNullOrWhiteSpace(modFileName) || _loadedModulesThisSession.Contains(modFileName))
                        {
                            continue;
                        }

                        if (HasSymbolsLoaded(mod))
                        {
                            _loadedModulesThisSession.Add(modFileName);
                            _symbolizedModulesThisSession.Add(modFileName);
                            continue;
                        }

                        bool matches = false;
                        for (int i = 0; i < compiledPatterns.Count; i++)
                        {
                            if (compiledPatterns[i].IsMatch(modFileName))
                            {
                                matches = true;
                                break;
                            }
                        }

                        if (matches)
                        {
                            string? fullPath = GetModulePathSafe(mod);
                            if (!_settings.HasLocalSymbolFile(modFileName, fullPath) &&
                                !_settings.HasActiveRemoteSymbolServers())
                            {
                                _loadedModulesThisSession.Add(modFileName);
                                _logger.Diagnostic($"Skipping breakpoint symbol load for {modFileName}: no local PDB found and no remote symbol servers enabled.");
                                continue;
                            }

                            var modSw = Stopwatch.StartNew();
                            try
                            {
                                mod.LoadSymbols(string.Empty);
                                modSw.Stop();
                                symbolLoadMs += modSw.ElapsedMilliseconds;
                                loadedCount++;
                                _loadedModulesThisSession.Add(modFileName);
                                if (HasSymbolsLoaded(mod))
                                {
                                    _symbolizedModulesThisSession.Add(modFileName);
                                }
                                _logger.Info($"Loading symbols on demand: {modFileName} — Breakpoint ({modSw.ElapsedMilliseconds} ms)");
                            }
                            catch (Exception ex)
                            {
                                modSw.Stop();
                                symbolLoadMs += modSw.ElapsedMilliseconds;
                                _logger.Error($"Failed to load symbols for {modFileName}.", ex);
                            }
                        }
                    }
                }
            }

            scanSw.Stop();
            totalSw.Stop();
            long moduleScanMs = Math.Max(0, scanSw.ElapsedMilliseconds - symbolLoadMs);
            if (loadedCount > 0)
            {
                _logger.Info(
                    $"[Profile] BreakpointBind: total={totalSw.ElapsedMilliseconds}ms " +
                    $"(includeList={includeListMs}ms, moduleScan={moduleScanMs}ms [modules={totalModulesScanned}], " +
                    $"symbolLoad={symbolLoadMs}ms [loaded={loadedCount}])");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Error loading symbols for active breakpoints during debug session.", ex);
        }
    }


    private void OnBeforeLaunch(string guid, int id, object customIn, object customOut, ref bool cancelDefault)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!IsEnabled)
        {
            return;
        }

        UpdateIncludeListFromBreakpoints("before launch");
    }

    private void OnEnterBreakMode(dbgEventReason reason, ref dbgExecutionAction executionAction)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _lastInspectedThreadId = 0;

        if (!IsEnabled || BreakSymbolLoadMode == BreakSymbolLoadMode.Off)
        {
            return;
        }

        try
        {
            var debugger = _dte.Debugger;
            if (debugger == null)
            {
                return;
            }

            var currentThread = debugger.CurrentThread;
            if (currentThread != null)
            {
                _lastInspectedThreadId = GetThreadIdSafe(currentThread);
            }

            // Only inspect background threads on non-step breaks (breakpoint hits, exceptions, manual breaks).
            // Single-stepping (F10/F11) restricts inspection to CurrentThread to keep stepping fast and responsive.
            bool inspectBackgroundThreads = reason != dbgEventReason.dbgEventReasonStep;
            LoadSymbolsForBreak(currentThread, inspectBackgroundThreads, "BreakMode");
        }
        catch (Exception ex)
        {
            // Never disrupt the debug session (Golden Rule #2)
            _logger.Error("Error handling break mode symbol loads.", ex);
        }
    }

    private void OnContextChanged(Process newProcess, Program newProgram, EnvDTE.Thread newThread, StackFrame newStackFrame)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var debugger = _dte.Debugger;
        if (!IsEnabled || BreakSymbolLoadMode == BreakSymbolLoadMode.Off || debugger?.CurrentMode != dbgDebugMode.dbgBreakMode)
        {
            return;
        }

        try
        {
            var targetThread = newThread ?? debugger.CurrentThread;
            if (targetThread != null)
            {
                int threadId = GetThreadIdSafe(targetThread);
                if (threadId != 0 && threadId == _lastInspectedThreadId)
                {
                    return;
                }

                _lastInspectedThreadId = threadId;
                LoadSymbolsForBreak(targetThread, inspectBackgroundThreads: false, "ContextChanged");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Error handling context changed symbol loads.", ex);
        }
    }

    private void LoadSymbolsForBreak(EnvDTE.Thread? targetThread, bool inspectBackgroundThreads, string reason)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var debugger = _dte.Debugger;
        if (!IsEnabled || BreakSymbolLoadMode == BreakSymbolLoadMode.Off || debugger?.DebuggedProcesses == null)
        {
            return;
        }

        try
        {
            var totalSw = Stopwatch.StartNew();
            var stackWalkSw = Stopwatch.StartNew();
            var currentThread = targetThread ?? debugger.CurrentThread;
            uint targetThreadId = (uint)GetThreadIdSafe(currentThread);
            var missingModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var currentThreadModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? topCurrentThreadUserModule = null;
            const int maxNonCurrentThreadFrames = 10;
            int threadsInspected = 0;
            int framesInspected = 0;

            bool walkedViaAd7 = TryCollectMissingModulesViaAd7(
                targetThreadId,
                inspectBackgroundThreads,
                maxNonCurrentThreadFrames,
                missingModules,
                currentThreadModules,
                out topCurrentThreadUserModule,
                out threadsInspected,
                out framesInspected);

            if (!walkedViaAd7)
            {
                CollectMissingModulesViaDte(
                    debugger,
                    currentThread,
                    inspectBackgroundThreads,
                    maxNonCurrentThreadFrames,
                    missingModules,
                    currentThreadModules,
                    out topCurrentThreadUserModule,
                    out threadsInspected,
                    out framesInspected);
            }

            stackWalkSw.Stop();
            long stackWalkMs = stackWalkSw.ElapsedMilliseconds;

            if (missingModules.Count == 0)
            {
                totalSw.Stop();
                _logger.Diagnostic(
                    $"[Profile] {reason} (no new modules): total={totalSw.ElapsedMilliseconds}ms " +
                    $"(stackWalk={stackWalkMs}ms [threads={threadsInspected}, frames={framesInspected}])");
                return;
            }

            var moduleScanSw = Stopwatch.StartNew();
            var targetModules = new List<(EnvDTE90.Module Module, string FileName, string? FullPath)>();
            var candidateBinaries = new List<string>();
            int totalModulesScanned = 0;
            bool maskServers = (BreakSymbolLoadMode == BreakSymbolLoadMode.LocalOnly);
            bool requireLocalPdb = maskServers || !_settings.HasActiveRemoteSymbolServers();

            foreach (EnvDTE.Process proc in debugger.DebuggedProcesses)
            {
                if (proc is EnvDTE90.Process3 proc3 && proc3.Modules != null)
                {
                    foreach (EnvDTE90.Module mod in proc3.Modules)
                    {
                        totalModulesScanned++;
                        string modFileName = WildcardPattern.ExtractFileNameSafe(mod.Name);
                        if (string.IsNullOrWhiteSpace(modFileName) || _loadedModulesThisSession.Contains(modFileName))
                        {
                            continue;
                        }

                        if (HasSymbolsLoaded(mod))
                        {
                            _loadedModulesThisSession.Add(modFileName);
                            _symbolizedModulesThisSession.Add(modFileName);
                            continue;
                        }

                        if (missingModules.Contains(modFileName))
                        {
                            string? fullPath = GetModulePathSafe(mod);
                            if (requireLocalPdb && !_settings.HasLocalSymbolFile(modFileName, fullPath))
                            {
                                _loadedModulesThisSession.Add(modFileName);
                                _logger.Diagnostic($"Skipping on-demand symbol load for {modFileName}: no local PDB found.");
                                continue;
                            }

                            targetModules.Add((mod, modFileName, fullPath));
                            candidateBinaries.Add(modFileName);
                        }
                    }
                }
            }

            moduleScanSw.Stop();
            long moduleScanMs = moduleScanSw.ElapsedMilliseconds;

            if (targetModules.Count == 0)
            {
                return;
            }

            string modeDesc = maskServers ? " (local-only)" : string.Empty;
            var newlyLoadedModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var symbolLoadSw = Stopwatch.StartNew();
            // Load symbols using configured server policy (masked or unmasked)
            _settings.ExecuteWithCandidateModules(candidateBinaries, maskSymbolServers: maskServers, () =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                foreach (var (mod, modFileName, fullPath) in targetModules)
                {
                    if (_loadedModulesThisSession.Contains(modFileName))
                    {
                        continue;
                    }

                    var modSw = Stopwatch.StartNew();
                    try
                    {
                        mod.LoadSymbols(string.Empty);
                        modSw.Stop();
                        if (HasSymbolsLoaded(mod))
                        {
                            newlyLoadedModules.Add(modFileName);
                            _symbolizedModulesThisSession.Add(modFileName);
                        }
                        _logger.Info($"Loading symbols on demand{modeDesc}: {modFileName} — {reason} ({modSw.ElapsedMilliseconds} ms)");
                    }
                    catch (Exception ex)
                    {
                        modSw.Stop();
                        // Expected when symbols are not available locally (e.g. OS DLLs not in local cache)
                        if (WildcardPattern.IsUserCodeModule(fullPath ?? modFileName))
                        {
                            _logger.Warning($"Could not load symbols for {modFileName}: {ex.Message}");
                        }
                        else
                        {
                            _logger.Diagnostic($"Symbols not available for system module {modFileName}: {ex.Message}");
                        }
                    }
                    finally
                    {
                        _loadedModulesThisSession.Add(modFileName);
                    }
                }
            });
            symbolLoadSw.Stop();
            long symbolLoadMs = symbolLoadSw.ElapsedMilliseconds;

            long contextRefreshMs = 0;
            bool shouldRefreshContext =
                newlyLoadedModules.Count > 0 &&
                currentThread != null &&
                (currentThreadModules.Count == 0 || newlyLoadedModules.Overlaps(currentThreadModules)) &&
                (topCurrentThreadUserModule == null ||
                 newlyLoadedModules.Contains(topCurrentThreadUserModule) ||
                 !_symbolizedModulesThisSession.Contains(topCurrentThreadUserModule));

            if (shouldRefreshContext)
            {
                var contextSw = Stopwatch.StartNew();
                RefreshBreakingFrameContext(debugger, currentThread!, newlyLoadedModules);
                contextSw.Stop();
                contextRefreshMs = contextSw.ElapsedMilliseconds;
            }

            totalSw.Stop();
            _logger.Info(
                $"[Profile] {reason}: total={totalSw.ElapsedMilliseconds}ms " +
                $"(stackWalk={stackWalkMs}ms [threads={threadsInspected}, frames={framesInspected}], " +
                $"moduleScan={moduleScanMs}ms [modules={totalModulesScanned}], " +
                $"symbolLoad={symbolLoadMs}ms [loaded={newlyLoadedModules.Count}/{targetModules.Count}], " +
                $"contextRefresh={contextRefreshMs}ms)");
        }
        catch (Exception ex)
        {
            _logger.Error($"Error loading symbols for threads ({reason}).", ex);
        }
    }

    /// <summary>
    /// Walks thread stacks using AD7 <see cref="IDebugThread2.EnumFrameInfo"/> with <see cref="enum_FRAMEINFO_FLAGS.FIF_MODULE"/>
    /// only, avoiding DTE's full-stack function/argument formatting overhead and per-frame COM automation calls.
    /// </summary>
    private bool TryCollectMissingModulesViaAd7(
        uint targetThreadId,
        bool inspectBackgroundThreads,
        int maxNonCurrentThreadFrames,
        HashSet<string> missingModules,
        HashSet<string> currentThreadModules,
        out string? topCurrentThreadUserModule,
        out int threadsInspected,
        out int framesInspected)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        topCurrentThreadUserModule = null;
        threadsInspected = 0;
        framesInspected = 0;

        try
        {
            var program = _activeAd7Program;
            if (program == null && _activeAd7Process != null &&
                ErrorHandler.Succeeded(_activeAd7Process.EnumPrograms(out IEnumDebugPrograms2? enumProgs)) &&
                enumProgs != null)
            {
                var progBuf = new IDebugProgram2[1];
                uint progFetched = 0;
                if (ErrorHandler.Succeeded(enumProgs.Next(1, progBuf, ref progFetched)) && progFetched == 1 && progBuf[0] != null)
                {
                    program = progBuf[0];
                    _activeAd7Program = program;
                }
            }

            var frameBuffer = new FRAMEINFO[32];

            // Fast path when inspecting only the active/target thread (e.g. single-stepping or OnContextChanged)
            if (!inspectBackgroundThreads && _lastAd7EventThread != null && targetThreadId != 0 &&
                ErrorHandler.Succeeded(_lastAd7EventThread.GetThreadId(out uint lastTid)) && lastTid == targetThreadId)
            {
                InspectAd7ThreadFrames(
                    _lastAd7EventThread,
                    isCurrentThread: true,
                    maxNonCurrentThreadFrames,
                    missingModules,
                    currentThreadModules,
                    ref topCurrentThreadUserModule,
                    ref threadsInspected,
                    ref framesInspected,
                    frameBuffer);
                if (threadsInspected > 0)
                {
                    return true;
                }
            }

            if (program == null ||
                !ErrorHandler.Succeeded(program.EnumThreads(out IEnumDebugThreads2? enumThreads)) ||
                enumThreads == null)
            {
                return false;
            }

            var threadBatch = new IDebugThread2[64];
            while (true)
            {
                uint threadsFetched = 0;
                int hr = enumThreads.Next((uint)threadBatch.Length, threadBatch, ref threadsFetched);
                if (!ErrorHandler.Succeeded(hr) || threadsFetched == 0)
                {
                    break;
                }

                for (uint i = 0; i < threadsFetched; i++)
                {
                    var ad7Thread = threadBatch[i];
                    threadBatch[i] = null!;
                    if (ad7Thread == null)
                    {
                        continue;
                    }

                    uint tid = 0;
                    try
                    {
                        ad7Thread.GetThreadId(out tid);
                    }
                    catch
                    {
                    }

                    bool isCurrentThread = targetThreadId != 0 && tid == targetThreadId;
                    if (!inspectBackgroundThreads && !isCurrentThread)
                    {
                        continue;
                    }

                    InspectAd7ThreadFrames(
                        ad7Thread,
                        isCurrentThread,
                        maxNonCurrentThreadFrames,
                        missingModules,
                        currentThreadModules,
                        ref topCurrentThreadUserModule,
                        ref threadsInspected,
                        ref framesInspected,
                        frameBuffer);

                    if (!inspectBackgroundThreads && isCurrentThread)
                    {
                        return threadsInspected > 0;
                    }
                }
            }

            return threadsInspected > 0;
        }
        catch
        {
            return false;
        }
    }

    private void InspectAd7ThreadFrames(
        IDebugThread2 ad7Thread,
        bool isCurrentThread,
        int maxNonCurrentThreadFrames,
        HashSet<string> missingModules,
        HashSet<string> currentThreadModules,
        ref string? topCurrentThreadUserModule,
        ref int threadsInspected,
        ref int framesInspected,
        FRAMEINFO[] frameBuffer)
    {
        try
        {
            if (!ErrorHandler.Succeeded(ad7Thread.EnumFrameInfo(
                    enum_FRAMEINFO_FLAGS.FIF_MODULE,
                    10,
                    out IEnumDebugFrameInfo2? enumFrames)) ||
                enumFrames == null)
            {
                return;
            }

            threadsInspected++;

            if (isCurrentThread)
            {
                while (true)
                {
                    uint fetched = 0;
                    int hr = enumFrames.Next((uint)frameBuffer.Length, frameBuffer, ref fetched);
                    if (!ErrorHandler.Succeeded(hr) || fetched == 0)
                    {
                        break;
                    }

                    for (uint i = 0; i < fetched; i++)
                    {
                        framesInspected++;
                        string? rawMod = (frameBuffer[i].m_dwValidFields & enum_FRAMEINFO_FLAGS.FIF_MODULE) != 0
                            ? frameBuffer[i].m_bstrModule
                            : null;
                        frameBuffer[i] = default;

                        string? norm = WildcardPattern.NormalizeFrameModuleName(rawMod);
                        if (!string.IsNullOrWhiteSpace(norm))
                        {
                            currentThreadModules.Add(norm!);
                            if (topCurrentThreadUserModule == null && WildcardPattern.IsUserCodeModule(rawMod ?? norm))
                            {
                                topCurrentThreadUserModule = norm;
                            }

                            if (!_loadedModulesThisSession.Contains(norm!))
                            {
                                missingModules.Add(norm!);
                            }
                        }
                    }
                }
            }
            else
            {
                int remaining = maxNonCurrentThreadFrames;
                uint chunkSize = 4;
                while (remaining > 0)
                {
                    uint requestCount = (uint)Math.Min(remaining, (int)Math.Min(chunkSize, (uint)frameBuffer.Length));
                    uint fetched = 0;
                    if (!ErrorHandler.Succeeded(enumFrames.Next(requestCount, frameBuffer, ref fetched)) || fetched == 0)
                    {
                        break;
                    }

                    remaining -= (int)fetched;
                    bool hitUserCode = false;
                    for (uint i = 0; i < fetched; i++)
                    {
                        string? rawMod = (frameBuffer[i].m_dwValidFields & enum_FRAMEINFO_FLAGS.FIF_MODULE) != 0
                            ? frameBuffer[i].m_bstrModule
                            : null;
                        frameBuffer[i] = default;

                        if (hitUserCode)
                        {
                            continue;
                        }

                        framesInspected++;
                        string? norm = WildcardPattern.NormalizeFrameModuleName(rawMod);
                        if (!string.IsNullOrWhiteSpace(norm))
                        {
                            if (!_loadedModulesThisSession.Contains(norm!))
                            {
                                missingModules.Add(norm!);
                            }

                            if (WildcardPattern.IsUserCodeModule(rawMod ?? norm))
                            {
                                hitUserCode = true;
                            }
                        }
                    }

                    if (hitUserCode || fetched < requestCount)
                    {
                        break;
                    }

                    chunkSize = (uint)remaining;
                }
            }
        }
        catch
        {
            // Ignore errors reading individual thread stack frames
        }
    }

    private void CollectMissingModulesViaDte(
        EnvDTE.Debugger debugger,
        EnvDTE.Thread? currentThread,
        bool inspectBackgroundThreads,
        int maxNonCurrentThreadFrames,
        HashSet<string> missingModules,
        HashSet<string> currentThreadModules,
        out string? topCurrentThreadUserModule,
        out int threadsInspected,
        out int framesInspected)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        topCurrentThreadUserModule = null;
        threadsInspected = 0;
        framesInspected = 0;

        var threadsToInspect = new List<EnvDTE.Thread>();
        if (currentThread != null)
        {
            threadsToInspect.Add(currentThread);
        }

        if (inspectBackgroundThreads && debugger.CurrentProgram?.Threads != null)
        {
            foreach (EnvDTE.Thread th in debugger.CurrentProgram.Threads)
            {
                if (th != currentThread)
                {
                    threadsToInspect.Add(th);
                }
            }
        }

        foreach (EnvDTE.Thread th in threadsToInspect)
        {
            try
            {
                var frames = th.StackFrames;
                if (frames == null)
                {
                    continue;
                }

                threadsInspected++;
                int frameIndex = 0;
                foreach (StackFrame frame in frames)
                {
                    frameIndex++;
                    if (th != currentThread && frameIndex > maxNonCurrentThreadFrames)
                    {
                        break;
                    }

                    framesInspected++;
                    string? rawMod = null;
                    try
                    {
                        rawMod = frame.Module;
                    }
                    catch
                    {
                    }

                    string? norm = WildcardPattern.NormalizeFrameModuleName(rawMod);
                    if (!string.IsNullOrWhiteSpace(norm))
                    {
                        if (th == currentThread)
                        {
                            currentThreadModules.Add(norm!);
                            if (topCurrentThreadUserModule == null && WildcardPattern.IsUserCodeModule(rawMod ?? norm))
                            {
                                topCurrentThreadUserModule = norm;
                            }
                        }

                        if (!_loadedModulesThisSession.Contains(norm!))
                        {
                            missingModules.Add(norm!);
                        }

                        // For non-current threads, stop once the first user-code module is encountered
                        // so that the thread's location is resolved without walking deeper OS/runtime frames.
                        if (th != currentThread && WildcardPattern.IsUserCodeModule(rawMod ?? norm))
                        {
                            break;
                        }
                    }
                }
            }
            catch
            {
                // Ignore errors reading individual thread stack frames
            }
        }
    }

    /// <summary>
    /// When execution breaks inside an un-symbolized module, Visual Studio initially sets
    /// <see cref="EnvDTE.Debugger.CurrentStackFrame"/> and the active editor document to the nearest
    /// caller that already had symbols loaded (or the "Symbols not loaded" tab).
    /// Once on-demand symbol loading completes for the breaking thread, this method updates the active
    /// stack frame context and opens the corresponding source file if the top source-resolvable frame
    /// belongs to one of the newly symbolized modules.
    /// </summary>
    private void RefreshBreakingFrameContext(
        EnvDTE.Debugger debugger,
        EnvDTE.Thread currentThread,
        HashSet<string> newlyLoadedModules)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var frames = currentThread.StackFrames;
            if (frames == null)
            {
                return;
            }

            foreach (StackFrame frame in frames)
            {
                string? rawMod = null;
                string? fileName = null;
                uint lineNumber = 0;

                try
                {
                    rawMod = frame.Module;
                    if (frame is StackFrame2 frame2)
                    {
                        fileName = frame2.FileName;
                        lineNumber = frame2.LineNumber;
                    }
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(fileName) || !File.Exists(fileName))
                {
                    continue;
                }

                string? modFileName = WildcardPattern.NormalizeFrameModuleName(rawMod);
                if (!string.IsNullOrWhiteSpace(modFileName) && newlyLoadedModules.Contains(modFileName!))
                {
                    try
                    {
                        debugger.CurrentStackFrame = frame;
                    }
                    catch
                    {
                    }

                    try
                    {
                        var window = _dte.ItemOperations?.OpenFile(fileName!, EnvDTE.Constants.vsViewKindTextView);
                        window?.Activate();

                        if (lineNumber > 0 && _dte.ActiveDocument?.Selection is TextSelection selection)
                        {
                            selection.GotoLine((int)lineNumber, Select: false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Diagnostic($"Could not open source file '{fileName}' for breaking frame: {ex.Message}");
                    }
                }

                // Only evaluate the top-most stack frame that resolves to an existing source file.
                break;
            }
        }
        catch (Exception ex)
        {
            _logger.Diagnostic($"Could not refresh breaking frame context: {ex.Message}");
        }
    }

    private static bool HasSymbolsLoaded(EnvDTE90.Module module)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return !string.IsNullOrWhiteSpace(module.SymbolFile);
        }
        catch
        {
            return false;
        }
    }

    private static string? GetModulePathSafe(EnvDTE90.Module module)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return module.Path;
        }
        catch
        {
            return null;
        }
    }

    private static int GetThreadIdSafe(EnvDTE.Thread? thread)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (thread == null)
        {
            return 0;
        }

        try
        {
            return thread.ID;
        }
        catch
        {
            return 0;
        }
    }

    private void OnEnterDesignMode(dbgEventReason reason)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _lastInspectedThreadId = 0;
        _activeAd7Program = null;
        _activeAd7Process = null;
        _lastAd7EventThread = null;
        _loadedModulesThisSession.Clear();
        _symbolizedModulesThisSession.Clear();

        try
        {
            _settings.Reconcile(null, "session ended");
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to release symbol settings lease on session end.", ex);
        }
    }

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
            _launchCommandEvents.BeforeExecute -= OnBeforeLaunch;
            _debuggerEvents.OnEnterBreakMode -= OnEnterBreakMode;
            _debuggerEvents.OnEnterDesignMode -= OnEnterDesignMode;
            _debuggerEvents.OnContextChanged -= OnContextChanged;

            if (_advised && _vsDebugger != null)
            {
                _vsDebugger.UnadviseDebugEventCallback(this);
                _advised = false;
            }
        }
        catch
        {
            // Ignore shutdown errors
        }

        _activeAd7Program = null;
        _activeAd7Process = null;
        _lastAd7EventThread = null;
        _loadedModulesThisSession.Clear();
        _symbolizedModulesThisSession.Clear();
    }
}
