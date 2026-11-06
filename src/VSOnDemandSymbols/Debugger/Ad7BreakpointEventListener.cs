using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Debugger.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using VSOnDemandSymbols.Core.Abstractions;

namespace VSOnDemandSymbols.Debugger;

/// <summary>
/// Passive AD7 observer registered via <see cref="IVsDebugger.AdviseDebugEventCallback"/> (BP-2, CS-1–CS-6, D-19).
/// <list type="bullet">
/// <item><description>On <see cref="IDebugBreakpointErrorEvent2"/>: schedules a single coalesced rescan of DTE breakpoints so their modules load on demand (BP-2).</description></item>
/// <item><description>On stopping events (<see cref="IDebugBreakEvent2"/>, <see cref="IDebugExceptionEvent2"/>, <see cref="IDebugBreakpointEvent2"/>, <see cref="IDebugStepCompleteEvent2"/>): schedules asynchronous call-stack evaluation according to <c>LoadPolicy.CallStackMode</c> (CS-1–CS-6).</description></item>
/// </list>
/// Unlike a Concord <c>IDkmBreakpointManagerNotification</c> implementation, this never sits in the
/// breakpoint notification path, so the IDE's breakpoint glyphs update normally.
/// </summary>
public sealed class Ad7BreakpointEventListener : IDebugEventCallback2, IDisposable
{
    public delegate void BreakEventHandler(
        IDebugProgram2? program,
        IDebugThread2? thread,
        bool isExceptionEvent,
        bool isHardBreakCandidate);

    private static readonly Guid BreakpointErrorEventGuid = typeof(IDebugBreakpointErrorEvent2).GUID;
    private static readonly Guid BreakEventGuid = typeof(IDebugBreakEvent2).GUID;
    private static readonly Guid ExceptionEventGuid = typeof(IDebugExceptionEvent2).GUID;
    private static readonly Guid BreakpointEventGuid = typeof(IDebugBreakpointEvent2).GUID;
    private static readonly Guid StepCompleteEventGuid = typeof(IDebugStepCompleteEvent2).GUID;

    private readonly IVsDebugger _debugger;
    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly Action _onBreakpointError;
    private readonly BreakEventHandler? _onBreakEvent;
    private readonly ILogger _logger;

    private readonly object _breakStateLock = new();
    private IDebugProgram2? _pendingProgram;
    private IDebugThread2? _pendingThread;
    private bool _pendingIsException;
    private bool _pendingIsHardBreakCandidate;
    private bool _pendingIsUserOrNormalBreak;

    private int _rescanPending;
    private int _breakEvalPending;
    private bool _advised;

    public Ad7BreakpointEventListener(
        IVsDebugger debugger,
        JoinableTaskFactory joinableTaskFactory,
        Action onBreakpointError,
        ILogger logger,
        BreakEventHandler? onBreakEvent = null)
    {
        _debugger = debugger ?? throw new ArgumentNullException(nameof(debugger));
        _joinableTaskFactory = joinableTaskFactory ?? throw new ArgumentNullException(nameof(joinableTaskFactory));
        _onBreakpointError = onBreakpointError ?? throw new ArgumentNullException(nameof(onBreakpointError));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _onBreakEvent = onBreakEvent;
    }

    public void Advise()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _advised = ErrorHandler.Succeeded(_debugger.AdviseDebugEventCallback(this));
        if (!_advised)
        {
            _logger.Warning("Could not subscribe to debugger events; live breakpoints and call-stack triggers will be unavailable.");
        }
    }

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
            if (riidEvent == BreakpointErrorEventGuid)
            {
                if (Interlocked.CompareExchange(ref _rescanPending, 1, 0) == 0)
                {
                    // Coalesce bursts of error events (one per unbound breakpoint) into a single rescan.
                    _ = _joinableTaskFactory.RunAsync(RescanAsync);
                }

                return VSConstants.S_OK;
            }

            if (_onBreakEvent != null && IsStoppingBreakEvent(riidEvent))
            {
                lock (_breakStateLock)
                {
                    if (pProgram != null)
                    {
                        _pendingProgram = pProgram;
                    }

                    if (pThread != null)
                    {
                        _pendingThread = pThread;
                    }

                    if (riidEvent == ExceptionEventGuid)
                    {
                        _pendingIsException = true;
                    }
                    else if (riidEvent == BreakpointEventGuid ||
                             riidEvent == StepCompleteEventGuid)
                    {
                        _pendingIsUserOrNormalBreak = true;
                    }
                    else if (riidEvent == BreakEventGuid)
                    {
                        _pendingIsHardBreakCandidate = true;
                    }
                }

                // CS-6: Schedule call-stack evaluation asynchronously so break mode is entered without waiting.
                if (Interlocked.CompareExchange(ref _breakEvalPending, 1, 0) == 0)
                {
                    _ = _joinableTaskFactory.RunAsync(EvaluateBreakCallStackAsync);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Error handling debugger event.", ex);
        }

        return VSConstants.S_OK;
    }

    private static bool IsStoppingBreakEvent(Guid riidEvent)
    {
        return riidEvent == BreakEventGuid ||
               riidEvent == ExceptionEventGuid ||
               riidEvent == BreakpointEventGuid ||
               riidEvent == StepCompleteEventGuid;
    }

    private async Task RescanAsync()
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: true);
        Interlocked.Exchange(ref _rescanPending, 0);

        try
        {
            _onBreakpointError();
        }
        catch (Exception ex)
        {
            _logger.Error("Error rescanning breakpoints after a breakpoint bind error.", ex);
        }
    }

    private async Task EvaluateBreakCallStackAsync()
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: true);
        Interlocked.Exchange(ref _breakEvalPending, 0);

        IDebugProgram2? program;
        IDebugThread2? thread;
        bool isException;
        bool isHardBreakCandidate;

        lock (_breakStateLock)
        {
            program = _pendingProgram;
            thread = _pendingThread;
            isException = _pendingIsException;
            isHardBreakCandidate = _pendingIsHardBreakCandidate && !_pendingIsUserOrNormalBreak;

            _pendingProgram = null;
            _pendingThread = null;
            _pendingIsException = false;
            _pendingIsHardBreakCandidate = false;
            _pendingIsUserOrNormalBreak = false;
        }

        try
        {
            _onBreakEvent?.Invoke(program, thread, isException, isHardBreakCandidate);
        }
        catch (Exception ex)
        {
            _logger.Error("Error evaluating call stack on debugger break.", ex);
        }
    }

    public void Dispose()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_advised)
        {
            _debugger.UnadviseDebugEventCallback(this);
            _advised = false;
        }
    }
}
