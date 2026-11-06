using System;
using System.Diagnostics;
using Microsoft.VisualStudio.Debugger;
using Microsoft.VisualStudio.Debugger.Breakpoints;
using Microsoft.VisualStudio.Debugger.ComponentInterfaces;
using Microsoft.VisualStudio.Debugger.Symbols;
using VSOnDemandSymbols.Core.Abstractions;
using VSOnDemandSymbols.Core.Modules;
using VSOnDemandSymbols.Core.Policy;

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
            if (sessionTracker == null || !sessionTracker.IsQualifyingProcess(moduleInstance.Process, moduleInstance))
            {
                return;
            }

            bool logDiag = _bridge.Logger.IsEnabled(LogLevel.Diagnostic);
            Stopwatch? sw = logDiag ? Stopwatch.StartNew() : null;

            sessionTracker.EnsureProcessSessionStarted(moduleInstance.Process, moduleInstance);

            var snapshot = _bridge.CurrentSnapshot;
            string fileName = WildcardPattern.ExtractFileNameSafe(moduleInstance.Name);
            if (string.IsNullOrEmpty(fileName) || !fileName.Contains("."))
            {
                string fromFull = WildcardPattern.ExtractFileNameSafe(moduleInstance.FullName);
                if (!string.IsNullOrEmpty(fromFull) && fromFull.Contains("."))
                {
                    fileName = fromFull;
                }
                else if (moduleInstance.LoadOrder == 0)
                {
                    fileName = sessionTracker.GetProcessExecutableName(moduleInstance.Process, moduleInstance);
                }
            }

            ModuleKey? key = snapshot.TryResolveLoadedModule(fileName);

            _bridge.ModuleRegistry.RegisterModuleLoad(moduleInstance, key, fileName);

            bool shouldLoad = snapshot.ShouldLoadAtModuleLoad(fileName, key, out string reasonDescription);
            if (moduleInstance.Module != null)
            {
                _bridge.ModuleRegistry.RecordStartupSymbolLoad(moduleInstance.UniqueId);
                if (shouldLoad)
                {
                    _bridge.Logger.Info($"Symbols loaded: {fileName} — {reasonDescription}");
                }
            }
            else if (shouldLoad)
            {
                // D-17/D-18: The native engine loads IncludeList symbols *after* this notification returns,
                // so Module == null here is expected. The outcome is reported from OnModuleSymbolsLoaded.
                _bridge.Logger.Verbose($"Module mapped, awaiting IncludeList symbol load: {fileName} ({reasonDescription})");
            }

            if (sw != null)
            {
                sw.Stop();
                _bridge.Logger.Diagnostic(
                    $"OnModuleInstanceLoad: '{fileName}' (key={key?.ToString() ?? "none"}, wanted={shouldLoad}, " +
                    $"hasSymbols={moduleInstance.Module != null}, elapsed={sw.Elapsed.TotalMilliseconds:F3} ms)");
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
            if (sessionTracker == null || !sessionTracker.IsQualifyingProcess(moduleInstance.Process, moduleInstance))
            {
                return;
            }

            bool initiatedByExtension = _bridge.ConsumeExtensionInitiatedLoad(moduleInstance.UniqueId);
            if (_bridge.ModuleRegistry.TryGetByUniqueId(moduleInstance.UniqueId, out var info) && info != null)
            {
                if (info.HadSymbolsAtModuleMap || info.LoadedAtStartup)
                {
                    return;
                }

                // D-18: Loads driven by the IncludeList (wanted set) are not manual loads (ML-1).
                bool wanted = _bridge.CurrentSnapshot.ShouldLoadAtModuleLoad(info.FileName, info.Key, out string reason);
                if (wanted)
                {
                    _bridge.ModuleRegistry.RecordStartupSymbolLoad(moduleInstance.UniqueId);
                    _bridge.Logger.Info($"Symbols loaded: {info.FileName} — {reason}");
                }
                else if (initiatedByExtension)
                {
                    _bridge.ModuleRegistry.RecordOnDemandSymbolLoad(moduleInstance.UniqueId);
                    _bridge.Logger.Info($"Symbols loaded on demand: {info.FileName}");
                }
                else
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
        try
        {
            _bridge.Logger.Info($"Breakpoint bound: {DescribeBreakpoint(pendingBreakpoint)} ({boundBreakpoints?.Length ?? 0} location(s)).");
        }
        catch (Exception ex)
        {
            _bridge.Logger.Error("Unhandled error in ConcordSymbolMonitor.OnBreakpointBound.", ex);
        }
    }

    public void OnBreakpointUnbound(
        DkmPendingBreakpoint pendingBreakpoint,
        DkmBoundBreakpoint[] boundBreakpoints,
        DkmBreakpointUnboundReason reason)
    {
        try
        {
            _bridge.Logger.Info($"Breakpoint unbound: {DescribeBreakpoint(pendingBreakpoint)} (reason={reason}).");
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
            _bridge.Logger.Info($"Breakpoint message [{level}]: {DescribeBreakpoint(pendingBreakpoint)} — {message}");
            _bridge.SessionTracker?.OnConcordBreakpointUnbound(pendingBreakpoint);
        }
        catch (Exception ex)
        {
            _bridge.Logger.Error("Unhandled error in ConcordSymbolMonitor.OnBreakpointMessage.", ex);
        }
    }

    private static string DescribeBreakpoint(DkmPendingBreakpoint? bp)
    {
        try
        {
            if (bp is DkmPendingFileLineBreakpoint fileLine)
            {
                var pos = fileLine.GetCurrentSourcePosition();
                return $"{WildcardPattern.ExtractFileNameSafe(pos.DocumentName)}({pos.TextSpan.StartLine})";
            }

            if (bp is DkmPendingFunctionBreakpoint func)
            {
                return func.FunctionName ?? "<function>";
            }
        }
        catch
        {
            // Fall through
        }

        return bp?.GetType().Name ?? "<null>";
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

