// Phase 0 Spike Harness for On-Demand Symbols (Option 2: Concord Hybrid + Manual-Load Settings)
// Throwaway experimental verification code — lives in spikes/, NOT compiled into production src/.
// Verifies:
//   - S1: IVsDebuggerSymbolSettingsManager120A + IVsDebuggerSymbolSettings170 manual-load toggle (without touching IncludeList)
//   - S2 & S3: Concord IDkmModuleInstanceLoadNotification + DkmModuleInstance.TryLoadSymbols() synchronous & on-demand symbol loading
//   - S4: Concord IDkmBreakpointManagerNotification (DkmPendingFileLineBreakpoint / DkmPendingFunctionBreakpoint) + CommandEvents diff
//   - S5: CommandEvents.BeforeExecute pre-launch hook
//   - S7: Concord IDkmModuleSymbolsLoadedNotification for detecting manual/external symbol loads (ML-1)

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Debugger;
using Microsoft.VisualStudio.Debugger.Breakpoints;
using Microsoft.VisualStudio.Debugger.ComponentInterfaces;
using Microsoft.VisualStudio.Debugger.Interop;
using Microsoft.VisualStudio.Debugger.Symbols;
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

/// <summary>
/// IDE-side Concord component (<c>ComponentLevel = 210000</c>) declared in
/// <c>VSOnDemandSymbols.Spike.vsdconfigxml</c>.
/// Runs in-process inside <c>devenv.exe</c> on the Concord engine worker thread.
/// </summary>
public sealed class ConcordSpikeMonitor :
    IDkmModuleInstanceLoadNotification,
    IDkmModuleInstanceUnloadNotification,
    IDkmModuleSymbolsLoadedNotification,
    IDkmBreakpointManagerNotification,
    IDkmProcessExitNotification
{
    internal static readonly ConcurrentDictionary<string, DkmModuleInstance> LoadedModules =
        new(StringComparer.OrdinalIgnoreCase);

    internal static readonly ConcurrentDictionary<string, byte> WantedModuleNames =
        new(StringComparer.OrdinalIgnoreCase);

    internal static readonly ConcurrentDictionary<Guid, byte> ExtensionInitiatedSymbolLoads = new();

    internal static Action<string>? LogSink;

    /// <summary>
    /// S3 / NF-1: Fires synchronously while the target process is suspended at the native DLL load event,
    /// before <c>DllMain</c> or <c>StartupModule()</c> executes.
    /// </summary>
    public void OnModuleInstanceLoad(
        DkmModuleInstance moduleInstance,
        DkmWorkList workList,
        DkmEventDescriptorS eventDescriptor)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            string fileName = moduleInstance.Name ?? string.Empty;
            LoadedModules[fileName] = moduleInstance;

            bool isWanted = WantedModuleNames.ContainsKey(fileName);
            bool hasSymbols = moduleInstance.SymbolFileId != null;

            if (isWanted && !hasSymbols)
            {
                ExtensionInitiatedSymbolLoads[moduleInstance.UniqueId] = 1;
                moduleInstance.TryLoadSymbols();
                hasSymbols = moduleInstance.SymbolFileId != null;
            }

            sw.Stop();
            LogSink?.Invoke(
                $"[Concord S3] OnModuleInstanceLoad: '{fileName}' (wanted={isWanted}, hasSymbols={hasSymbols}, elapsed={sw.Elapsed.TotalMilliseconds:F3} ms)");
        }
        catch (Exception ex)
        {
            LogSink?.Invoke($"[Concord S3 ERROR] {ex.Message}");
        }
    }

    public void OnModuleInstanceUnload(
        DkmModuleInstance moduleInstance,
        DkmWorkList workList,
        DkmEventDescriptor eventDescriptor)
    {
        try
        {
            string fileName = moduleInstance.Name ?? string.Empty;
            LoadedModules.TryRemove(fileName, out _);
            ExtensionInitiatedSymbolLoads.TryRemove(moduleInstance.UniqueId, out _);
        }
        catch (Exception ex)
        {
            LogSink?.Invoke($"[Concord Unload ERROR] {ex.Message}");
        }
    }

    /// <summary>
    /// S7 (ML-1): Fires whenever symbols are loaded for a module instance (either at load time,
    /// via <see cref="DkmModuleInstance.TryLoadSymbols()"/>, or manually by the user in the Modules window).
    /// </summary>
    public void OnModuleSymbolsLoaded(
        DkmModuleInstance moduleInstance,
        DkmModule module,
        bool isReload,
        DkmWorkList workList,
        DkmEventDescriptor eventDescriptor)
    {
        try
        {
            string fileName = moduleInstance.Name ?? string.Empty;
            bool initiatedByExtension = ExtensionInitiatedSymbolLoads.TryRemove(moduleInstance.UniqueId, out _)
                || WantedModuleNames.ContainsKey(fileName);

            if (!initiatedByExtension)
            {
                LogSink?.Invoke($"[Concord S7/ML-1] External/User-initiated symbol load for '{fileName}' (isReload={isReload})");
            }
            else
            {
                LogSink?.Invoke($"[Concord S2] Symbols loaded for '{fileName}' (isReload={isReload})");
            }
        }
        catch (Exception ex)
        {
            LogSink?.Invoke($"[Concord S7 ERROR] {ex.Message}");
        }
    }

    /// <summary>
    /// S4-A (BP-2): Strongly typed Concord breakpoint notification when a breakpoint binds,
    /// fails to bind, or emits a warning (e.g. when added mid-session in a module without symbols).
    /// </summary>
    public void OnBreakpointBound(DkmPendingBreakpoint pendingBreakpoint, DkmBoundBreakpoint[] boundBreakpoints)
    {
    }

    public void OnBreakpointUnbound(
        DkmPendingBreakpoint pendingBreakpoint,
        DkmBoundBreakpoint[] boundBreakpoints,
        DkmBreakpointUnboundReason reason)
    {
        InspectPendingBreakpoint(pendingBreakpoint, $"Unbound ({reason})");
    }

    public void OnBreakpointMessage(
        DkmPendingBreakpoint pendingBreakpoint,
        DkmBreakpointMessageLevel level,
        string message)
    {
        InspectPendingBreakpoint(pendingBreakpoint, $"Message ({level}): {message}");
    }

    private static void InspectPendingBreakpoint(DkmPendingBreakpoint pendingBreakpoint, string context)
    {
        try
        {
            if (pendingBreakpoint is DkmPendingFileLineBreakpoint fileLineBp)
            {
                DkmSourcePosition? pos = fileLineBp.GetCurrentSourcePosition();
                if (pos != null)
                {
                    LogSink?.Invoke(
                        $"[Concord S4-A] Pending file/line breakpoint ({context}): '{pos.DocumentName}' line {pos.TextSpan.StartLine}");
                }
            }
            else if (pendingBreakpoint is DkmPendingFunctionBreakpoint funcBp)
            {
                LogSink?.Invoke(
                    $"[Concord S4-A] Pending function breakpoint ({context}): module='{funcBp.ModuleName}', func='{funcBp.FunctionName}'");
            }
        }
        catch (Exception ex)
        {
            LogSink?.Invoke($"[Concord S4-A ERROR] {ex.Message}");
        }
    }

    public void OnProcessExit(DkmProcess process, int exitCode, DkmEventDescriptor eventDescriptor)
    {
        try
        {
            LoadedModules.Clear();
            ExtensionInitiatedSymbolLoads.Clear();
            LogSink?.Invoke($"[Concord] Process exited (exitCode={exitCode}). Cleared module registry.");
        }
        catch (Exception ex)
        {
            LogSink?.Invoke($"[Concord ProcessExit ERROR] {ex.Message}");
        }
    }
}

internal sealed class Phase0SpikeRunner : IDisposable
{
    public static readonly Guid OutputPaneGuid = new("d2b57819-4a12-4c8d-9e3f-7152e89c01a1");

    public static readonly Guid Std97CmdSetGuid = VSConstants.CMDSETID.StandardCommandSet97_string != null
        ? new Guid(VSConstants.CMDSETID.StandardCommandSet97_string)
        : new Guid("5efc7975-14bc-11cf-9b2b-00aa00573819");

    public static readonly Guid VsDebugCmdSetGuid = new("C9DD4A59-47FB-11D2-83E7-00C04F9902C1");

    private readonly AsyncPackage _package;
    private readonly IVsOutputWindowPane _pane;
    private readonly IVsDebuggerSymbolSettingsManager120A _settingsManager;
    private readonly DTE2 _dte;

    private readonly List<CommandEvents> _commandEventSubscriptions = new();
    private HashSet<string> _lastBreakpointSnapshot = new(StringComparer.OrdinalIgnoreCase);

    private Phase0SpikeRunner(
        AsyncPackage package,
        IVsOutputWindowPane pane,
        IVsDebuggerSymbolSettingsManager120A settingsManager,
        DTE2 dte)
    {
        _package = package;
        _pane = pane;
        _settingsManager = settingsManager;
        _dte = dte;
    }

    public static async Task<Phase0SpikeRunner?> InitializeAsync(AsyncPackage package, CancellationToken cancellationToken = default)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        var outputWindow = await package.GetServiceAsync(typeof(SVsOutputWindow)) as IVsOutputWindow;
        var debuggerObj = await package.GetServiceAsync(typeof(SVsShellDebugger));
        var dte = await package.GetServiceAsync(typeof(SDTE)) as DTE2;

        if (outputWindow == null || debuggerObj is not IVsDebuggerSymbolSettingsManager120A settingsManager || dte == null)
        {
            return null;
        }

        var paneGuid = OutputPaneGuid;
        outputWindow.CreatePane(ref paneGuid, "On-Demand Symbols Spike", fInitVisible: 1, fClearWithSolution: 0);
        outputWindow.GetPane(ref paneGuid, out var pane);
        pane.Activate();

        var runner = new Phase0SpikeRunner(package, pane, settingsManager, dte);
        runner.Start();
        return runner;
    }

    private void Start()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        ConcordSpikeMonitor.LogSink = LogThreadSafe;
        LogThreadSafe("=== Phase 0 Spike Runner (Option 2: Concord + Manual-Load Settings) Initialized ===");

        // Run S1 round-trip verification immediately (design mode, restores original settings immediately)
        RunSpikeS1_SymbolSettingsRoundTrip();

        // Subscribe to command events for S4-B (breakpoints) and S5 (before-launch)
        SubscribeCommandEvents();

        // Take initial breakpoint snapshot
        DiffBreakpoints("Initial snapshot");
    }

    /// <summary>
    /// S1: Verify reading, cloning, setting manual-load mode (<c>IsManualLoad = true</c>,
    /// <c>IsLoadAdjacent = false</c>, <c>AutomaticSymbolLoad = false</c>) WITHOUT modifying
    /// <c>IncludeList</c>, and restoring the user's exact settings.
    /// </summary>
    public void RunSpikeS1_SymbolSettingsRoundTrip()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var original = _settingsManager.GetCurrentSymbolSettings();
            var backup = original.Clone();
            bool? originalAutoLoad = (original as IVsDebuggerSymbolSettings170)?.get_AutomaticSymbolLoad();

            LogThreadSafe(
                $"[S1] Original settings: IsManualLoad={original.IsManualLoad}, IsLoadAdjacent={original.IsLoadAdjacent}, " +
                $"AutomaticSymbolLoad={originalAutoLoad}, IncludeList.Count={original.IncludeList.Count}");

            // Apply manual-load mode without modifying IncludeList
            var working = original.Clone();
            working.IsManualLoad = true;
            working.IsLoadAdjacent = false;
            if (working is IVsDebuggerSymbolSettings170 working170)
            {
                working170.set_AutomaticSymbolLoad(false);
            }

            _settingsManager.SaveSymbolSettings(working, loadSymbolsNow: false);

            var readBack = _settingsManager.GetCurrentSymbolSettings();
            bool? readBackAutoLoad = (readBack as IVsDebuggerSymbolSettings170)?.get_AutomaticSymbolLoad();
            LogThreadSafe(
                $"[S1] Read-back after SaveSymbolSettings: IsManualLoad={readBack.IsManualLoad}, " +
                $"IsLoadAdjacent={readBack.IsLoadAdjacent}, AutomaticSymbolLoad={readBackAutoLoad}, " +
                $"IncludeList.Count={readBack.IncludeList.Count} (untouched)");

            // Restore original user settings
            if (backup is IVsDebuggerSymbolSettings170 backup170 && originalAutoLoad.HasValue)
            {
                backup170.set_AutomaticSymbolLoad(originalAutoLoad.Value);
            }
            _settingsManager.SaveSymbolSettings(backup, loadSymbolsNow: false);
            LogThreadSafe("[S1] PASS: Restored original user symbol settings cleanly.");
        }
        catch (Exception ex)
        {
            LogThreadSafe($"[S1] ERROR: {ex}");
        }
    }

    /// <summary>
    /// S2: On-demand symbol load via Concord <see cref="DkmModuleInstance.TryLoadSymbols()"/> (thread-safe).
    /// </summary>
    public void TryLoadSymbolsOnDemand(string moduleFileName, string reason)
    {
        if (!ConcordSpikeMonitor.LoadedModules.TryGetValue(moduleFileName, out DkmModuleInstance? moduleInstance))
        {
            ConcordSpikeMonitor.WantedModuleNames[moduleFileName] = 1;
            LogThreadSafe($"[S2] '{moduleFileName}' not loaded yet; added to Concord wanted set ({reason}).");
            return;
        }

        try
        {
            ConcordSpikeMonitor.ExtensionInitiatedSymbolLoads[moduleInstance.UniqueId] = 1;
            var sw = Stopwatch.StartNew();
            moduleInstance.TryLoadSymbols();
            sw.Stop();

            bool hasSymbols = moduleInstance.SymbolFileId != null;
            LogThreadSafe(
                $"[S2] DkmModuleInstance.TryLoadSymbols() for '{moduleFileName}' ({reason}): " +
                $"hasSymbols={hasSymbols}, elapsed={sw.Elapsed.TotalMilliseconds:F2} ms");
        }
        catch (Exception ex)
        {
            LogThreadSafe($"[S2] ERROR loading symbols for '{moduleFileName}': {ex.Message}");
        }
    }

    private void SubscribeCommandEvents()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        int[] stdLaunchCmdIds = { 295, 296, 248, 249, 251, 213 };
        foreach (int id in stdLaunchCmdIds)
        {
            SubscribeCommand(Std97CmdSetGuid, id, beforeLaunch: true, afterBreakpoint: false);
        }

        int[] dbgLaunchCmdIds = { 0xF010, 0x0156, 0x0157 };
        foreach (int id in dbgLaunchCmdIds)
        {
            SubscribeCommand(VsDebugCmdSetGuid, id, beforeLaunch: true, afterBreakpoint: false);
        }

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
            LogThreadSafe($"[S5] BeforeExecute launch command: guid={guid}, cmdId=0x{id:X} ({id}), Mode={_dte.Debugger.CurrentMode}");
            DiffBreakpoints($"BeforeExecute cmdId={id}");
        }
        catch (Exception ex)
        {
            LogThreadSafe($"[S5] ERROR in OnLaunchCommandBeforeExecute: {ex.Message}");
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
            LogThreadSafe($"[S4] ERROR in OnBreakpointCommandAfterExecute: {ex.Message}");
        }
    }

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
                LogThreadSafe($"[S4-B] Breakpoint state added/changed ({triggerSource}): {added}");
            }
        }

        foreach (string removed in _lastBreakpointSnapshot)
        {
            if (!current.Contains(removed))
            {
                LogThreadSafe($"[S4-B] Breakpoint state removed/changed ({triggerSource}): {removed}");
            }
        }

        _lastBreakpointSnapshot = current;
    }

    private void LogThreadSafe(string message)
    {
        string formatted = $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}";
        _ = _package.JoinableTaskFactory.RunAsync(async () =>
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            _pane.OutputStringThreadSafe(formatted);
        });
    }

    public void Dispose()
    {
        ConcordSpikeMonitor.LogSink = null;
        _commandEventSubscriptions.Clear();
    }
}
