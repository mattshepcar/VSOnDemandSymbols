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
/// Passive AD7 observer registered via <see cref="IVsDebugger.AdviseDebugEventCallback"/> (BP-2, D-19).
/// When the debugger reports a breakpoint that could not bind (<see cref="IDebugBreakpointErrorEvent2"/>),
/// a single coalesced rescan of the DTE breakpoints is scheduled so their modules can be loaded on demand.
/// Unlike a Concord <c>IDkmBreakpointManagerNotification</c> implementation, this never sits in the
/// breakpoint notification path, so the IDE's breakpoint glyphs update normally.
/// </summary>
public sealed class Ad7BreakpointEventListener : IDebugEventCallback2, IDisposable
{
    private static readonly Guid BreakpointErrorEventGuid = typeof(IDebugBreakpointErrorEvent2).GUID;

    private readonly IVsDebugger _debugger;
    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly Action _onBreakpointError;
    private readonly ILogger _logger;
    private int _rescanPending;
    private bool _advised;

    public Ad7BreakpointEventListener(
        IVsDebugger debugger,
        JoinableTaskFactory joinableTaskFactory,
        Action onBreakpointError,
        ILogger logger)
    {
        _debugger = debugger ?? throw new ArgumentNullException(nameof(debugger));
        _joinableTaskFactory = joinableTaskFactory ?? throw new ArgumentNullException(nameof(joinableTaskFactory));
        _onBreakpointError = onBreakpointError ?? throw new ArgumentNullException(nameof(onBreakpointError));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Advise()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _advised = ErrorHandler.Succeeded(_debugger.AdviseDebugEventCallback(this));
        if (!_advised)
        {
            _logger.Warning("Could not subscribe to debugger events; new breakpoints during a session will not trigger symbol loads.");
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
            if (riidEvent == BreakpointErrorEventGuid &&
                Interlocked.CompareExchange(ref _rescanPending, 1, 0) == 0)
            {
                // Coalesce bursts of error events (one per unbound breakpoint) into a single rescan.
                _ = _joinableTaskFactory.RunAsync(RescanAsync);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Error handling debugger event.", ex);
        }

        return VSConstants.S_OK;
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
