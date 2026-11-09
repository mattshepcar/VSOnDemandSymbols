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

    private int _rescanPending;
    private bool _advised;
    private bool _disposed;

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
        ILogger logger)
    {
        _dte = dte ?? throw new ArgumentNullException(nameof(dte));
        _vsDebugger = vsDebugger ?? throw new ArgumentNullException(nameof(vsDebugger));
        _joinableTaskFactory = joinableTaskFactory ?? throw new ArgumentNullException(nameof(joinableTaskFactory));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

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

        UpdateIncludeListFromBreakpoints("before launch");
    }

    private void OnEnterBreakMode(dbgEventReason reason, ref dbgExecutionAction executionAction)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
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

            // Only inspect background threads on non-step breaks (breakpoint hits, exceptions, manual breaks).
            // Single-stepping (F10/F11) restricts inspection to CurrentThread to keep stepping fast and responsive.
            if (reason != dbgEventReason.dbgEventReasonStep && debugger.CurrentProgram?.Threads != null)
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

        _loadedModulesThisSession.Clear();
    }
}
