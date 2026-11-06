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
    private static readonly Guid Std97CmdSetGuid = new("5efc7975-14bc-11cf-9b2b-00aa00573819");
    private static readonly Guid BreakpointErrorEventGuid = typeof(IDebugBreakpointErrorEvent2).GUID;
    private const int CmdIdStart = 295; // F5 Launch

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

        _launchCommandEvents = _dte.Events.get_CommandEvents(Std97CmdSetGuid.ToString("B"), CmdIdStart);
        _launchCommandEvents.BeforeExecute += OnBeforeLaunch;

        _debuggerEvents = _dte.Events.DebuggerEvents;
        _debuggerEvents.OnEnterBreakMode += OnEnterBreakMode;
        _debuggerEvents.OnEnterDesignMode += OnEnterDesignMode;

        _advised = ErrorHandler.Succeeded(_vsDebugger.AdviseDebugEventCallback(this));
        if (!_advised)
        {
            _logger.Warning("Could not subscribe to debugger events; new breakpoints during a session will not trigger symbol loads.");
        }
    }

    /// <summary>
    /// Resolves enabled breakpoints to candidate binary names and populates Visual Studio's <c>IncludeList</c>.
    /// Returns the list of candidate binary wildcard patterns for modules with active breakpoints.
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
                    if (!bp.Enabled || string.IsNullOrWhiteSpace(bp.File))
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

            _settings.ApplyManualMode(wantedBinaries, reason: $"{reason} (active breakpoints: {breakpointCount})");
            return candidatePatterns;
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to update IncludeList from active breakpoints.", ex);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Handles AD7 debug events asynchronously to observe breakpoint binding errors mid-session (D-19, D-24).
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

            if ((riidEvent == BreakpointErrorEventGuid || pEvent is IDebugBreakpointErrorEvent2) &&
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

        UpdateIncludeListFromBreakpoints("before launch");
    }

    private void OnEnterBreakMode(dbgEventReason reason, ref dbgExecutionAction executionAction)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!IsEnabled)
        {
            return;
        }

        try
        {
            var currentThread = _dte.Debugger?.CurrentThread;
            if (currentThread?.StackFrames == null)
            {
                return;
            }

            var missingModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (StackFrame frame in currentThread.StackFrames)
            {
                string modName = frame.Module;
                if (!string.IsNullOrWhiteSpace(modName) && modName.IndexOf(".dll", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string clean = WildcardPattern.ExtractFileNameSafe(modName);
                    if (!string.IsNullOrWhiteSpace(clean) && !_loadedModulesThisSession.Contains(clean))
                    {
                        missingModules.Add(clean);
                    }
                }
            }

            if (missingModules.Count == 0 || _dte.Debugger?.DebuggedProcesses == null)
            {
                return;
            }

            foreach (Process proc in _dte.Debugger.DebuggedProcesses)
            {
                if (proc is EnvDTE90.Process3 proc3 && proc3.Modules != null)
                {
                    foreach (EnvDTE90.Module mod in proc3.Modules)
                    {
                        string modFileName = WildcardPattern.ExtractFileNameSafe(mod.Name);
                        if (missingModules.Contains(modFileName))
                        {
                            try
                            {
                                _logger.Info($"Loading symbols on demand: {modFileName} — CallStack");
                                mod.LoadSymbols(string.Empty);
                                _loadedModulesThisSession.Add(modFileName);
                            }
                            catch (Exception ex)
                            {
                                _logger.Error($"Failed to load call stack symbols for {modFileName}.", ex);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Never disrupt the debug session (Golden Rule #2)
            _logger.Error("Error handling break mode symbol loads.", ex);
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
