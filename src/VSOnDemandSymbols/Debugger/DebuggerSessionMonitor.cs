using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Debugger.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using VSOnDemandSymbols.Core.Abstractions;
using VSOnDemandSymbols.Core.Modules;
using VSOnDemandSymbols.Core.Policy;
using VSOnDemandSymbols.Statistics;

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
    private readonly StartupStatisticsManager? _statsManager;

    private readonly CommandEvents _launchCommandEvents;
    private readonly DebuggerEvents _debuggerEvents;
    private readonly HashSet<string> _loadedModulesThisSession = new(StringComparer.OrdinalIgnoreCase);

    private int _rescanPending;
    private bool _advised;
    private bool _disposed;

    private System.Diagnostics.Stopwatch? _launchStopwatch;
    private int _launchBreakpointModuleCount;
    private int _launchPending;
    private CancellationTokenSource? _launchWatcherCts;

    /// <summary>
    /// Gets or sets whether session monitoring and symbol management are currently active.
    /// Automatically activated when an Unreal solution is open and deactivated when closed.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// Gets or sets the delegate providing the current <see cref="BreakSymbolLoadMode"/>.
    /// Defaults to reading the <c>ODS_BREAK_SYMBOL_LOAD_MODE</c> environment variable or <see cref="BreakSymbolLoadMode.LocalOnly"/>.
    /// </summary>
    public Func<BreakSymbolLoadMode> BreakSymbolLoadModeProvider { get; set; } = () => BreakSymbolLoadModeExtensions.FromEnvironment();

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
        ILogger logger,
        StartupStatisticsManager? statsManager = null)
    {
        _dte = dte ?? throw new ArgumentNullException(nameof(dte));
        _vsDebugger = vsDebugger ?? throw new ArgumentNullException(nameof(vsDebugger));
        _joinableTaskFactory = joinableTaskFactory ?? throw new ArgumentNullException(nameof(joinableTaskFactory));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _statsManager = statsManager;

        ThreadHelper.ThrowIfNotOnUIThread();

        // Intercept F5 / Debug.Start to populate IncludeList before launch.
        // Uses Standard Command Set 97 (guidVSStd97 from stdidcmd.h) and cmdidStart (295).
        _launchCommandEvents = _dte.Events.get_CommandEvents(
            VSConstants.CMDSETID.StandardCommandSet97_string,
            (int)VSConstants.VSStd97CmdID.Start);
        _launchCommandEvents.BeforeExecute += OnBeforeLaunch;

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
            var wantedBinaries = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "UnrealEditor*.exe"
            };

            var candidatePatterns = new List<string>();
            int breakpointCount = 0;
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
                            candidatePatterns.Add(binary);
                        }
                    }
                }
            }

            _settings.ApplyManualMode(wantedBinaries, reason: $"{reason} (breakpoints: {breakpointCount})");
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

        var candidatePatterns = UpdateIncludeListFromBreakpoints("breakpoint bind error");
        if (candidatePatterns.Count == 0 || _dte.Debugger?.DebuggedProcesses == null)
        {
            return;
        }

        try
        {
            foreach (Process proc in _dte.Debugger.DebuggedProcesses)
            {
                if (proc is EnvDTE90.Process3 proc3 && proc3.Modules != null)
                {
                    foreach (EnvDTE90.Module mod in proc3.Modules)
                    {
                        string modFileName = WildcardPattern.ExtractFileNameSafe(mod.Name);
                        if (string.IsNullOrWhiteSpace(modFileName) || _loadedModulesThisSession.Contains(modFileName))
                        {
                            continue;
                        }

                        bool matches = false;
                        for (int i = 0; i < candidatePatterns.Count; i++)
                        {
                            if (WildcardPattern.MatchesFileName(candidatePatterns[i], modFileName))
                            {
                                matches = true;
                                break;
                            }
                        }

                        if (matches)
                        {
                            try
                            {
                                _logger.Info($"Loading symbols on demand: {modFileName} — Breakpoint");
                                mod.LoadSymbols(string.Empty);
                                _loadedModulesThisSession.Add(modFileName);
                            }
                            catch (Exception ex)
                            {
                                _logger.Error($"Failed to load symbols for {modFileName}.", ex);
                            }
                        }
                    }
                }
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

        var candidatePatterns = UpdateIncludeListFromBreakpoints("before launch");

        if (_statsManager != null)
        {
            _launchBreakpointModuleCount = candidatePatterns.Count;
            _launchStopwatch = System.Diagnostics.Stopwatch.StartNew();
            Interlocked.Exchange(ref _launchPending, 1);

            _launchWatcherCts?.Cancel();
            _launchWatcherCts?.Dispose();
            _launchWatcherCts = new CancellationTokenSource();

            var token = _launchWatcherCts.Token;
            _ = _joinableTaskFactory.RunAsync(() => WatchForEditorStartupAsync(token));
        }
    }

    private async Task WatchForEditorStartupAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Give the debugger time to start and attach to the process
            await Task.Delay(2000, cancellationToken);

            int targetPid = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            while (!cancellationToken.IsCancellationRequested &&
                   Volatile.Read(ref _launchPending) == 1 &&
                   sw.Elapsed.TotalSeconds < 60.0)
            {
                if (targetPid == 0)
                {
                    await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                    if (!IsEnabled || _dte.Debugger?.CurrentMode == dbgDebugMode.dbgDesignMode)
                    {
                        break;
                    }

                    if (_dte.Debugger?.DebuggedProcesses != null)
                    {
                        foreach (Process proc in _dte.Debugger.DebuggedProcesses)
                        {
                            string procName = WildcardPattern.ExtractFileNameSafe(proc.Name);
                            if (procName.StartsWith("UnrealEditor", StringComparison.OrdinalIgnoreCase))
                            {
                                targetPid = proc.ProcessID;
                                break;
                            }
                        }
                    }
                }

                if (targetPid != 0)
                {
                    bool isReady = false;
                    bool hasExited = false;
                    int moduleCount = 0;

                    await TaskScheduler.Default;

                    try
                    {
                        using var sysProc = System.Diagnostics.Process.GetProcessById(targetPid);
                        hasExited = sysProc.HasExited;
                        if (!hasExited)
                        {
                            // Unreal Editor has completed boot once its Slate main window is created
                            if (sysProc.MainWindowHandle != IntPtr.Zero || sw.Elapsed.TotalSeconds >= 45.0)
                            {
                                isReady = true;
                                try
                                {
                                    moduleCount = sysProc.Modules.Count;
                                }
                                catch
                                {
                                    // Process.Modules may throw on access restrictions
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Process may have exited or cannot be inspected
                    }

                    if (hasExited)
                    {
                        break;
                    }

                    if (isReady)
                    {
                        CompleteLaunch(moduleCount);
                        break;
                    }
                }

                await Task.Delay(1000, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when debug session ends or breakpoints trigger early
        }
        catch (Exception ex)
        {
            _logger.Diagnostic($"Error in editor startup watcher: {ex.Message}");
        }
    }

    private void CompleteLaunch(int moduleCount)
    {
        if (Interlocked.CompareExchange(ref _launchPending, 0, 1) == 1)
        {
            var duration = _launchStopwatch?.Elapsed ?? TimeSpan.Zero;
            _statsManager?.RecordLaunch(moduleCount, _launchBreakpointModuleCount + 1, duration);

            try
            {
                _launchWatcherCts?.Cancel();
            }
            catch
            {
            }
        }
    }

    private static int GetModuleCountSafe(int processId)
    {
        if (processId <= 0)
        {
            return 0;
        }

        try
        {
            using var sysProc = System.Diagnostics.Process.GetProcessById(processId);
            return sysProc.Modules.Count;
        }
        catch
        {
            return 0;
        }
    }

    private void OnEnterBreakMode(dbgEventReason reason, ref dbgExecutionAction executionAction)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (Volatile.Read(ref _launchPending) == 1)
        {
            int moduleCount = 0;
            try
            {
                int pid = _dte.Debugger?.CurrentProcess?.ProcessID ?? 0;
                moduleCount = GetModuleCountSafe(pid);
            }
            catch
            {
            }
            CompleteLaunch(moduleCount);
        }

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

            var threadsToInspect = new List<EnvDTE.Thread>();
            var currentThread = debugger.CurrentThread;
            if (currentThread != null)
            {
                threadsToInspect.Add(currentThread);
            }

            if (debugger.CurrentProgram?.Threads != null)
            {
                foreach (EnvDTE.Thread th in debugger.CurrentProgram.Threads)
                {
                    if (th != currentThread)
                    {
                        threadsToInspect.Add(th);
                    }
                }
            }

            LoadSymbolsForThreads(threadsToInspect, "BreakMode");
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
                LoadSymbolsForThreads(new[] { targetThread }, "ContextChanged");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Error handling context changed symbol loads.", ex);
        }
    }

    private void LoadSymbolsForThreads(IEnumerable<EnvDTE.Thread> threads, string reason)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var debugger = _dte.Debugger;
        if (!IsEnabled || BreakSymbolLoadMode == BreakSymbolLoadMode.Off || debugger?.DebuggedProcesses == null)
        {
            return;
        }

        try
        {
            var currentThread = debugger.CurrentThread;
            var missingModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            const int maxNonCurrentThreadFrames = 10;

            foreach (EnvDTE.Thread th in threads)
            {
                try
                {
                    var frames = th.StackFrames;
                    if (frames == null)
                    {
                        continue;
                    }

                    int frameIndex = 0;
                    foreach (StackFrame frame in frames)
                    {
                        frameIndex++;
                        if (th != currentThread && frameIndex > maxNonCurrentThreadFrames)
                        {
                            break;
                        }

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

            if (missingModules.Count == 0)
            {
                return;
            }

            var targetModules = new List<EnvDTE90.Module>();
            var candidateBinaries = new List<string>();

            foreach (Process proc in debugger.DebuggedProcesses)
            {
                if (proc is EnvDTE90.Process3 proc3 && proc3.Modules != null)
                {
                    foreach (EnvDTE90.Module mod in proc3.Modules)
                    {
                        string modFileName = WildcardPattern.ExtractFileNameSafe(mod.Name);
                        if (missingModules.Contains(modFileName) && !_loadedModulesThisSession.Contains(modFileName))
                        {
                            targetModules.Add(mod);
                            candidateBinaries.Add(modFileName);
                        }
                    }
                }
            }

            if (targetModules.Count == 0)
            {
                return;
            }

            bool maskServers = (BreakSymbolLoadMode == BreakSymbolLoadMode.LocalOnly);
            string modeDesc = maskServers ? " (local-only)" : string.Empty;

            // Load symbols using configured server policy (masked or unmasked)
            _settings.ExecuteWithCandidateModules(candidateBinaries, maskSymbolServers: maskServers, () =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                foreach (var mod in targetModules)
                {
                    string modFileName = WildcardPattern.ExtractFileNameSafe(mod.Name);
                    if (_loadedModulesThisSession.Contains(modFileName))
                    {
                        continue;
                    }

                    try
                    {
                        _logger.Info($"Loading symbols on demand{modeDesc}: {modFileName} — {reason}");
                        mod.LoadSymbols(string.Empty);
                    }
                    catch (Exception ex)
                    {
                        // Expected when symbols are not available locally (e.g. OS DLLs not in local cache)
                        if (WildcardPattern.IsUserCodeModule(modFileName))
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
        }
        catch (Exception ex)
        {
            _logger.Error($"Error loading symbols for threads ({reason}).", ex);
        }
    }

    private void OnEnterDesignMode(dbgEventReason reason)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _loadedModulesThisSession.Clear();

        if (Volatile.Read(ref _launchPending) == 1)
        {
            if (_launchStopwatch != null && _launchStopwatch.Elapsed.TotalSeconds >= 5.0)
            {
                CompleteLaunch(0);
            }
            else
            {
                Interlocked.Exchange(ref _launchPending, 0);
            }
        }

        try
        {
            _launchWatcherCts?.Cancel();
        }
        catch
        {
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
            _launchWatcherCts?.Cancel();
            _launchWatcherCts?.Dispose();
            _launchWatcherCts = null;
        }
        catch
        {
        }

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

        _loadedModulesThisSession.Clear();
    }
}
