using System;
using System.Diagnostics;
using Microsoft.VisualStudio.Debugger;
using Microsoft.VisualStudio.Debugger.Breakpoints;
using Microsoft.VisualStudio.Debugger.ComponentInterfaces;
using Microsoft.VisualStudio.Debugger.Symbols;
using VSOnDemandSymbols.Core.Abstractions;
using VSOnDemandSymbols.Core.Modules;

namespace VSOnDemandSymbols.Debugger;

/// <summary>
/// IDE-side Concord component (<c>ComponentLevel = 210000</c>) declared in <c>VSOnDemandSymbols.vsdconfigxml</c>.
/// Runs in-process inside <c>devenv.exe</c> on the Concord engine worker thread (ARCHITECTURE.md §3, VS_DEBUGGER_INTEGRATION.md §1.2).
/// </summary>
public sealed class ConcordSymbolMonitor :
    IDkmModuleInstanceLoadNotification,
    IDkmModuleInstanceUnloadNotification,
    IDkmModuleSymbolsLoadedNotification,
    IDkmBreakpointManagerNotification,
    IDkmProcessExitNotification
{
    private readonly ConcordSymbolBridge _bridge;

    public ConcordSymbolMonitor()
        : this(ConcordSymbolBridge.Instance)
    {
    }

    internal ConcordSymbolMonitor(ConcordSymbolBridge bridge)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
    }

    /// <summary>
    /// Fires synchronously while the target process is suspended at the native DLL load event,
    /// before <c>DllMain</c> or <c>StartupModule()</c> executes (SPEC §4, §7.4, NF-1).
    /// </summary>
    public void OnModuleInstanceLoad(
        DkmModuleInstance moduleInstance,
        DkmWorkList workList,
        DkmEventDescriptorS eventDescriptor)
    {
        try
        {
            var sessionTracker = _bridge.SessionTracker;
            if (sessionTracker == null || !sessionTracker.IsQualifyingProcess(moduleInstance.Process))
            {
                return;
            }

            bool logDiag = _bridge.Logger.IsEnabled(LogLevel.Diagnostic);
            Stopwatch? sw = logDiag ? Stopwatch.StartNew() : null;

            sessionTracker.EnsureProcessSessionStarted(moduleInstance.Process);

            var snapshot = _bridge.CurrentSnapshot;
            string fileName = moduleInstance.Name ?? string.Empty;
            ModuleKey? key = snapshot.TryResolveLoadedModule(fileName);

            _bridge.ModuleRegistry.RegisterModuleLoad(moduleInstance, key);

            bool shouldLoad = snapshot.ShouldLoadAtModuleLoad(fileName, key, out string reasonDescription);
            if (shouldLoad)
            {
                if (moduleInstance.SymbolFileId == null)
                {
                    _bridge.MarkExtensionInitiatedLoad(moduleInstance.UniqueId);
                    _bridge.Logger.Info($"Loading symbols: {fileName} — {reasonDescription}");

                    moduleInstance.TryLoadSymbols();

                    if (moduleInstance.SymbolFileId != null)
                    {
                        _bridge.ModuleRegistry.RecordStartupSymbolLoad(moduleInstance.UniqueId);
                    }
                    else
                    {
                        _bridge.RemoveExtensionInitiatedLoad(moduleInstance.UniqueId);
                        string searchDetails = ConcordSymbolBridge.GetSymbolSearchDetails(moduleInstance);
                        _bridge.Logger.Info($"Symbols not found for {fileName} (searched: {searchDetails})");
                    }
                }
                else
                {
                    _bridge.ModuleRegistry.RecordStartupSymbolLoad(moduleInstance.UniqueId);
                }
            }

            if (sw != null)
            {
                sw.Stop();
                _bridge.Logger.Diagnostic(
                    $"OnModuleInstanceLoad: '{fileName}' (key={key?.ToString() ?? "none"}, wanted={shouldLoad}, " +
                    $"hasSymbols={moduleInstance.SymbolFileId != null}, elapsed={sw.Elapsed.TotalMilliseconds:F3} ms)");
            }
        }
        catch (Exception ex)
        {
            _bridge.Logger.Error("Unhandled error in ConcordSymbolMonitor.OnModuleInstanceLoad.", ex);
        }
    }

    public void OnModuleInstanceUnload(
        DkmModuleInstance moduleInstance,
        DkmWorkList workList,
        DkmEventDescriptor eventDescriptor)
    {
        try
        {
            _bridge.ModuleRegistry.RegisterModuleUnload(moduleInstance);
            _bridge.RemoveExtensionInitiatedLoad(moduleInstance.UniqueId);
        }
        catch (Exception ex)
        {
            _bridge.Logger.Error("Unhandled error in ConcordSymbolMonitor.OnModuleInstanceUnload.", ex);
        }
    }

    /// <summary>
    /// Detects user-initiated symbol loads (e.g. from the Modules window) and records <c>Manual</c> MRU hits (SPEC §5.5, ML-1).
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
            var sessionTracker = _bridge.SessionTracker;
            if (sessionTracker == null || !sessionTracker.IsQualifyingProcess(moduleInstance.Process))
            {
                return;
            }

            bool initiatedByExtension = _bridge.ConsumeExtensionInitiatedLoad(moduleInstance.UniqueId);
            if (_bridge.ModuleRegistry.TryGetByUniqueId(moduleInstance.UniqueId, out var info) && info != null)
            {
                if (!initiatedByExtension && !info.HadSymbolsAtModuleMap && !info.LoadedAtStartup)
                {
                    _bridge.ModuleRegistry.RecordOnDemandSymbolLoad(moduleInstance.UniqueId);
                    sessionTracker.OnManualSymbolLoadDetected(moduleInstance, info.Key);
                }
            }
        }
        catch (Exception ex)
        {
            _bridge.Logger.Error("Unhandled error in ConcordSymbolMonitor.OnModuleSymbolsLoaded.", ex);
        }
    }

    public void OnBreakpointBound(DkmPendingBreakpoint pendingBreakpoint, DkmBoundBreakpoint[] boundBreakpoints)
    {
    }

    public void OnBreakpointUnbound(
        DkmPendingBreakpoint pendingBreakpoint,
        DkmBoundBreakpoint[] boundBreakpoints,
        DkmBreakpointUnboundReason reason)
    {
        try
        {
            _bridge.SessionTracker?.OnConcordBreakpointUnbound(pendingBreakpoint);
        }
        catch (Exception ex)
        {
            _bridge.Logger.Error("Unhandled error in ConcordSymbolMonitor.OnBreakpointUnbound.", ex);
        }
    }

    public void OnBreakpointMessage(
        DkmPendingBreakpoint pendingBreakpoint,
        DkmBreakpointMessageLevel level,
        string message)
    {
        try
        {
            _bridge.SessionTracker?.OnConcordBreakpointUnbound(pendingBreakpoint);
        }
        catch (Exception ex)
        {
            _bridge.Logger.Error("Unhandled error in ConcordSymbolMonitor.OnBreakpointMessage.", ex);
        }
    }

    public void OnProcessExit(DkmProcess process, int exitCode, DkmEventDescriptor eventDescriptor)
    {
        try
        {
            _bridge.ModuleRegistry.RemoveProcess(process.UniqueId);
            _bridge.SessionTracker?.OnProcessExited(process.UniqueId);
        }
        catch (Exception ex)
        {
            _bridge.Logger.Error("Unhandled error in ConcordSymbolMonitor.OnProcessExit.", ex);
        }
    }
}

