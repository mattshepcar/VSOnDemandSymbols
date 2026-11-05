// Phase 0 Spike Harness for On-Demand Symbols (S1–S5, S7)
// Throwaway experimental verification code — lives in spikes/, NOT compiled into production src/.
// To run interactively in the Experimental Instance (/rootsuffix Exp):
//   1. Temporarily include <Compile Include="spikes\Phase0SpikeRunner.cs" /> in the VSIX csproj
//   2. Call `await Phase0SpikeRunner.InitializeAsync(this);` from VSOnDemandSymbolsPackage.InitializeAsync
//   3. Press F5 to launch the Experimental Instance and check the "On-Demand Symbols Spike" Output pane.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Debugger.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace VSOnDemandSymbols.Spikes;

/// <summary>
/// Minimal COM import for VS 17.0+ <c>IVsDebuggerSymbolSettings170</c>
/// (defined in <c>Microsoft.VisualStudio.Debugger.Interop.17.0.dll</c>).
/// Controls the "Automatically include additional symbols when needed" setting (<c>AutomaticSymbolLoad</c>).
/// </summary>
[ComImport]
[Guid("aa6086eb-b8f7-4b4a-be6b-37e4dfe1a933")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVsDebuggerSymbolSettings170
{
    void set_AutomaticSymbolLoad([In] bool value);
    bool get_AutomaticSymbolLoad();
}

internal sealed class Phase0SpikeRunner : IDebugEventCallback2, IVsDebuggerEvents, IDisposable
{
    public static readonly Guid OutputPaneGuid = new("d2b57819-4a12-4c8d-9e3f-7152e89c01a1");

    // Command set GUIDs for S4 (breakpoint commands) and S5 (launch commands)
    public static readonly Guid Std97CmdSetGuid = VSConstants.CMDSETID.StandardCommandSet97_string != null
        ? new Guid(VSConstants.CMDSETID.StandardCommandSet97_string)
        : new Guid("5efc7975-14bc-11cf-9b2b-00aa00573819");

    // guidVSDebugCommand from VsDbgCmd.h / vsdebugguids.h
    public static readonly Guid VsDebugCmdSetGuid = new("C9DD4A59-47FB-11D2-83E7-00C04F9902C1");

    private readonly AsyncPackage _package;
    private readonly IVsOutputWindowPane _pane;
    private readonly IVsDebugger _debugger;
    private readonly IVsDebuggerSymbolSettingsManager120A _settingsManager;
    private readonly DTE2 _dte;

    private uint _debuggerEventsCookie;
    private readonly List<CommandEvents> _commandEventSubscriptions = new();
    private readonly Dictionary<string, IDebugModule2> _loadedModules = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _extensionInitiatedLoads = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _lastBreakpointSnapshot = new(StringComparer.OrdinalIgnoreCase);

    private Phase0SpikeRunner(
        AsyncPackage package,
        IVsOutputWindowPane pane,
        IVsDebugger debugger,
        IVsDebuggerSymbolSettingsManager120A settingsManager,
        DTE2 dte)
    {
        _package = package;
        _pane = pane;
        _debugger = debugger;
        _settingsManager = settingsManager;
        _dte = dte;
    }

    public static async Task<Phase0SpikeRunner?> InitializeAsync(AsyncPackage package, CancellationToken cancellationToken = default)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        var outputWindow = await package.GetServiceAsync(typeof(SVsOutputWindow)) as IVsOutputWindow;
        var debuggerObj = await package.GetServiceAsync(typeof(SVsShellDebugger));
        var dte = await package.GetServiceAsync(typeof(SDTE)) as DTE2;

        if (outputWindow == null || debuggerObj is not IVsDebugger debugger ||
            debuggerObj is not IVsDebuggerSymbolSettingsManager120A settingsManager || dte == null)
        {
            return null;
        }

        var paneGuid = OutputPaneGuid;
        outputWindow.CreatePane(ref paneGuid, "On-Demand Symbols Spike", fInitVisible: 1, fClearWithSolution: 0);
        outputWindow.GetPane(ref paneGuid, out var pane);
        pane.Activate();

        var runner = new Phase0SpikeRunner(package, pane, debugger, settingsManager, dte);
        runner.Start();
        return runner;
    }

    private void Start()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Log("=== Phase 0 Spike Runner Initialized ===");

        // Run S1 round-trip verification immediately (design mode, restores original settings immediately)
        RunSpikeS1_SymbolSettingsRoundTrip();

        // Subscribe to AD7 debug events (S2, S3, S4-A, S7)
        ErrorHandler.ThrowOnFailure(_debugger.AdviseDebugEventCallback(this));
        ErrorHandler.ThrowOnFailure(_debugger.AdviseDebuggerEvents(this, out _debuggerEventsCookie));

        // Subscribe to command events for S4-B (breakpoints) and S5 (before-launch)
        SubscribeCommandEvents();

        // Take initial breakpoint snapshot
        DiffBreakpoints("Initial snapshot");
    }

    /// <summary>
    /// S1: Verify reading, cloning, modifying, saving, and restoring <see cref="IVsDebuggerSymbolSettings120A"/>
    /// and <see cref="IVsDebuggerSymbolSettings170"/> without altering any user settings permanently.
    /// </summary>
    public void RunSpikeS1_SymbolSettingsRoundTrip()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var original = _settingsManager.GetCurrentSymbolSettings();
            var backup = original.Clone();
            bool? originalAutoLoad = (original as IVsDebuggerSymbolSettings170)?.get_AutomaticSymbolLoad();

            Log($"[S1] Original settings: IsManualLoad={original.IsManualLoad}, IsLoadAdjacent={original.IsLoadAdjacent}, " +
                $"AutomaticSymbolLoad={originalAutoLoad}, IncludeList.Count={original.IncludeList.Count}, " +
                $"ExcludeList.Count={original.ExcludeList.Count}, UseMSSymbolServers={original.UseMSSymbolServers}");

            // Modify a working clone and save with loadSymbolsNow = false
            var working = original.Clone();
            working.IsManualLoad = true;
            working.IsLoadAdjacent = false;
            if (working is IVsDebuggerSymbolSettings170 working170)
            {
                working170.set_AutomaticSymbolLoad(false);
            }

            const string spikeSentinelEntry = "UnrealEditor-SpikeTest-*.dll";
            working.IncludeList.Add(spikeSentinelEntry, IsEnabled: true);

            _settingsManager.SaveSymbolSettings(working, loadSymbolsNow: false);

            // Read back to verify round-trip and wildcard Find
            var readBack = _settingsManager.GetCurrentSymbolSettings();
            bool? readBackAutoLoad = (readBack as IVsDebuggerSymbolSettings170)?.get_AutomaticSymbolLoad();
            int exactIndex = readBack.IncludeList.Find(0, spikeSentinelEntry, useWildcard: false);
            int wildcardMatchIndex = readBack.IncludeList.Find(0, "UnrealEditor-SpikeTest-Core.dll", useWildcard: true);

            Log($"[S1] Read-back after SaveSymbolSettings: IsManualLoad={readBack.IsManualLoad}, " +
                $"IsLoadAdjacent={readBack.IsLoadAdjacent}, AutomaticSymbolLoad={readBackAutoLoad}, " +
                $"IncludeList.Count={readBack.IncludeList.Count}, ExactFind={exactIndex}, WildcardFind={wildcardMatchIndex}");

            // Immediately restore the user's exact backup
            if (backup is IVsDebuggerSymbolSettings170 backup170 && originalAutoLoad.HasValue)
            {
                backup170.set_AutomaticSymbolLoad(originalAutoLoad.Value);
            }
            _settingsManager.SaveSymbolSettings(backup, loadSymbolsNow: false);
            Log("[S1] PASS: Restored original user symbol settings cleanly.");
        }
        catch (Exception ex)
        {
            Log($"[S1] ERROR: {ex}");
        }
    }

    /// <summary>
    /// S2: Programmatically load symbols for a loaded module via <see cref="IDebugModule3.LoadSymbols"/>.
    /// </summary>
    public void TryLoadSymbolsForModule(IDebugModule2 module, string moduleName, string reason)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (module is not IDebugModule3 module3)
            {
                Log($"[S2] Module '{moduleName}' does not implement IDebugModule3.");
                return;
            }

            _extensionInitiatedLoads.Add(moduleName);
            var sw = Stopwatch.StartNew();
            int hr = module3.LoadSymbols();
            sw.Stop();

            var info = new MODULE_INFO[1];
            module.GetInfo(enum_MODULE_INFO_FIELDS.MIF_FLAGS, info);
            bool hasSymbols = (info[0].m_dwModuleFlags & enum_MODULE_FLAGS.MODULE_FLAG_SYMBOLS) != 0;

            Log($"[S2] IDebugModule3.LoadSymbols() for '{moduleName}' ({reason}): hr=0x{hr:X8}, " +
                $"hasSymbols={hasSymbols}, elapsed={sw.Elapsed.TotalMilliseconds:F2} ms");
        }
        catch (Exception ex)
        {
            Log($"[S2] ERROR loading symbols for '{moduleName}': {ex.Message}");
        }
    }

    private void SubscribeCommandEvents()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        // StandardCommandSet97 launch commands (S5)
        int[] stdLaunchCmdIds =
        {
            295, // cmdidStart (Debug.Start / F5)
            296, // cmdidRestart (Debug.Restart)
            248, // cmdidStepInto (Debug.StepInto / F11)
            249, // cmdidStepOver (Debug.StepOver / F10)
            251, // cmdidRunToCursor (Debug.RunToCursor / Ctrl+F10)
            213, // cmdidDebugProcesses (Debug.AttachtoProcess)
        };

        foreach (int id in stdLaunchCmdIds)
        {
            SubscribeCommand(Std97CmdSetGuid, id, beforeLaunch: true, afterBreakpoint: false);
        }

        // guidVSDebugCommand launch commands (S5)
        int[] dbgLaunchCmdIds =
        {
            0xF010, // cmdidReattach (Debug.ReattachtoProcess / Shift+Alt+P)
            0x0156, // cmdidSingleProcStepInto
            0x0157, // cmdidSingleProcStepOver
        };

        foreach (int id in dbgLaunchCmdIds)
        {
            SubscribeCommand(VsDebugCmdSetGuid, id, beforeLaunch: true, afterBreakpoint: false);
        }

        // StandardCommandSet97 breakpoint commands (S4-B)
        int[] stdBpCmdIds =
        {
            255, // cmdidToggleBreakpoint (F9)
            256, // cmdidClearBreakpoints (Ctrl+Shift+F9)
            375, // cmdidInsertBreakpoint
            376, // cmdidEnableBreakpoint (Ctrl+F9)
        };

        foreach (int id in stdBpCmdIds)
        {
            SubscribeCommand(Std97CmdSetGuid, id, beforeLaunch: false, afterBreakpoint: true);
        }

        // guidVSDebugCommand breakpoint commands (S4-B)
        int[] dbgBpCmdIds =
        {
            0x0122, // cmdidDisableAllBreakpoints
            0x0123, // cmdidEnableAllBreakpoints
            0x0124, // cmdidToggleAllBreakpoints
            0x0137, // cmdidNewFunctionBreakpoint
            0x0041, // cmdidInsertTracepoint
            0x1003, // cmdidBreakpointsWindowDelete
            0x1014, // cmdidBreakpointsWindowDeleteAllMatching
            0x1015, // cmdidBreakpointsWindowToggleAllMatching
        };

        foreach (int id in dbgBpCmdIds)
        {
            SubscribeCommand(VsDebugCmdSetGuid, id, beforeLaunch: false, afterBreakpoint: true);
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
            Log($"[S5] BeforeExecute launch command: guid={guid}, cmdId=0x{id:X} ({id}), Mode={_dte.Debugger.CurrentMode}");
            DiffBreakpoints($"BeforeExecute cmdId={id}");
        }
        catch (Exception ex)
        {
            Log($"[S5] ERROR in OnLaunchCommandBeforeExecute: {ex.Message}");
        }
    }

    private void OnBreakpointCommandAfterExecute(string guid, int id, object customIn, object customOut)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            DiffBreakpoints($"AfterExecute BP command guid={guid} id=0x{id:X}");
        }
        catch (Exception ex)
        {
            Log($"[S4] ERROR in OnBreakpointCommandAfterExecute: {ex.Message}");
        }
    }

    /// <summary>
    /// S4-B: Snapshot and diff <c>DTE.Debugger.Breakpoints</c> to detect add/remove/enable/disable.
    /// </summary>
    private void DiffBreakpoints(string triggerSource)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Breakpoints? bps = _dte.Debugger?.Breakpoints;
        if (bps != null)
        {
            foreach (Breakpoint bp in bps)
            {
                string key = $"{bp.LocationType}|{bp.File}|{bp.FileLine}|{bp.FunctionName}|Enabled={bp.Enabled}";
                current.Add(key);
            }
        }

        foreach (string added in current)
        {
            if (!_lastBreakpointSnapshot.Contains(added))
            {
                Log($"[S4-B] Breakpoint state added/changed ({triggerSource}): {added}");
            }
        }

        foreach (string removed in _lastBreakpointSnapshot)
        {
            if (!current.Contains(removed))
            {
                Log($"[S4-B] Breakpoint state removed/changed ({triggerSource}): {removed}");
            }
        }

        _lastBreakpointSnapshot = current;
    }

    public int Event(
        IDebugEngine2? pEngine,
        IDebugProcess2? pProcess,
        IDebugProgram2? pProgram,
        IDebugThread2? pThread,
        IDebugEvent2 pEvent,
        ref Guid riidEvent,
        uint dwAttrib)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            // Keep NF-1 timing measurement on module load
            if (pEvent is IDebugModuleLoadEvent2 moduleLoadEvent)
            {
                var sw = Stopwatch.StartNew();
                HandleModuleLoadEvent(moduleLoadEvent, dwAttrib);
                sw.Stop();
                if (sw.Elapsed.TotalMilliseconds > 1.0)
                {
                    Log($"[S3/NF-1] Module load handler took {sw.Elapsed.TotalMilliseconds:F3} ms (dwAttrib=0x{dwAttrib:X})");
                }
            }
            else if (pEvent is IDebugSymbolSearchEvent2 symbolSearchEvent)
            {
                HandleSymbolSearchEvent(symbolSearchEvent);
            }
            else if (pEvent is IDebugBreakpointErrorEvent2 bpErrorEvent)
            {
                HandleBreakpointErrorEvent(bpErrorEvent);
            }
        }
        catch (Exception ex)
        {
            Log($"[Spike AD7 Event Error] {ex.Message}");
        }

        return VSConstants.S_OK;
    }

    /// <summary>
    /// S3: Inspect <see cref="IDebugModuleLoadEvent2"/> and event attributes (<c>EVENT_SYNCHRONOUS</c> / <c>EVENT_STOPPING</c>).
    /// </summary>
    private void HandleModuleLoadEvent(IDebugModuleLoadEvent2 moduleLoadEvent, uint dwAttrib)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string debugMessage = string.Empty;
        int pbLoad = 0;
        if (ErrorHandler.Failed(moduleLoadEvent.GetModule(out IDebugModule2 module, ref debugMessage, ref pbLoad)) || module == null)
        {
            return;
        }

        var info = new MODULE_INFO[1];
        if (ErrorHandler.Failed(module.GetInfo(
            enum_MODULE_INFO_FIELDS.MIF_NAME | enum_MODULE_INFO_FIELDS.MIF_URL | enum_MODULE_INFO_FIELDS.MIF_FLAGS,
            info)))
        {
            return;
        }

        string moduleName = info[0].m_bstrName ?? string.Empty;
        bool hasSymbols = (info[0].m_dwModuleFlags & enum_MODULE_FLAGS.MODULE_FLAG_SYMBOLS) != 0;

        if (pbLoad != 0)
        {
            _loadedModules[moduleName] = module;
            Log($"[S3] Module Load: {moduleName} (hasSymbols={hasSymbols}, dwAttrib=0x{dwAttrib:X}, msg='{debugMessage}')");
        }
        else
        {
            _loadedModules.Remove(moduleName);
        }
    }

    /// <summary>
    /// S7: Detect user-initiated symbol loads via <see cref="IDebugSymbolSearchEvent2"/> (ML-1).
    /// </summary>
    private void HandleSymbolSearchEvent(IDebugSymbolSearchEvent2 symbolSearchEvent)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string debugMessage = string.Empty;
        var flags = new enum_MODULE_INFO_FLAGS[1];
        if (ErrorHandler.Failed(symbolSearchEvent.GetSymbolSearchInfo(out IDebugModule3 module3, ref debugMessage, flags)) ||
            module3 == null)
        {
            return;
        }

        bool symbolsLoaded = (flags[0] & enum_MODULE_INFO_FLAGS.MIF_SYMBOLS_LOADED) != 0;
        var info = new MODULE_INFO[1];
        module3.GetInfo(enum_MODULE_INFO_FIELDS.MIF_NAME, info);
        string moduleName = info[0].m_bstrName ?? string.Empty;

        bool wasInitiatedByExtension = _extensionInitiatedLoads.Remove(moduleName);
        if (symbolsLoaded && !wasInitiatedByExtension)
        {
            Log($"[S7/ML-1] External/User-initiated symbol load detected for '{moduleName}': {debugMessage}");
        }
    }

    /// <summary>
    /// S4-A: Extract file and line from <see cref="IDebugBreakpointErrorEvent2"/> when a pending breakpoint
    /// cannot bind because its module's symbols are not yet loaded.
    /// </summary>
    private void HandleBreakpointErrorEvent(IDebugBreakpointErrorEvent2 bpErrorEvent)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (ErrorHandler.Failed(bpErrorEvent.GetErrorBreakpoint(out IDebugErrorBreakpoint2 errorBp)) || errorBp == null)
        {
            return;
        }

        if (ErrorHandler.Failed(errorBp.GetPendingBreakpoint(out IDebugPendingBreakpoint2 pendingBp)) || pendingBp == null)
        {
            return;
        }

        if (ErrorHandler.Failed(pendingBp.GetBreakpointRequest(out IDebugBreakpointRequest2 bpRequest)) || bpRequest == null)
        {
            return;
        }

        var reqInfo = new BP_REQUEST_INFO[1];
        if (ErrorHandler.Failed(bpRequest.GetRequestInfo(enum_BPREQI_FIELDS.BPREQI_BPLOCATION, reqInfo)))
        {
            return;
        }

        BP_LOCATION loc = reqInfo[0].bpLocation;
        if (loc.bpLocationType == (uint)enum_BP_LOCATION_TYPE.BPLT_CODE_FILE_LINE && loc.unionmember2 != IntPtr.Zero)
        {
            try
            {
                if (Marshal.GetObjectForIUnknown(loc.unionmember2) is IDebugDocumentPosition2 docPos)
                {
                    docPos.GetFileName(out string fileName);
                    var startPos = new TEXT_POSITION[1];
                    var endPos = new TEXT_POSITION[1];
                    docPos.GetRange(startPos, endPos);
                    uint lineOneBased = startPos[0].dwLine + 1;

                    Log($"[S4-A] Pending breakpoint unbound at '{fileName}' line {lineOneBased}");
                }
            }
            finally
            {
                Marshal.Release(loc.unionmember2);
            }
        }
        else if (loc.bpLocationType == (uint)enum_BP_LOCATION_TYPE.BPLT_CODE_FUNC_OFFSET && loc.unionmember2 != IntPtr.Zero)
        {
            try
            {
                if (Marshal.GetObjectForIUnknown(loc.unionmember2) is IDebugFunctionPosition2 funcPos)
                {
                    funcPos.GetFunctionName(out string funcName);
                    Log($"[S4-A] Pending function breakpoint unbound: '{funcName}'");
                }
            }
            finally
            {
                Marshal.Release(loc.unionmember2);
            }
        }
    }

    public int OnModeChange(DBGMODE dbgmodeNew)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Log($"[S5] IVsDebuggerEvents.OnModeChange: {dbgmodeNew}");
        return VSConstants.S_OK;
    }

    private void Log(string message)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _pane.OutputStringThreadSafe($"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
    }

    public void Dispose()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _debugger.UnadviseDebugEventCallback(this);
        if (_debuggerEventsCookie != 0)
        {
            _debugger.UnadviseDebuggerEvents(_debuggerEventsCookie);
            _debuggerEventsCookie = 0;
        }
        _commandEventSubscriptions.Clear();
    }
}
