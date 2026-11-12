using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Debugger.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VSOnDemandSymbols.Core.Abstractions;
using VSOnDemandSymbols.Core.State;

namespace VSOnDemandSymbols.Debugger;

/// <summary>
/// Public launch hook called synchronously inside <c>LaunchDebugTargets</c> for every launch path
/// (F5, F11, Run To Cursor, Restart, Start New Instance, UnrealVS) across all VS instances (SPEC.md §4.1, ADR-15).
/// Registered under <c>[$RootKey$\Debugger\LaunchHooks110]</c>.
/// </summary>
[Guid(HookGuidString)]
[ComVisible(true)]
public sealed class SymbolSettingsLaunchHook : IVsDebugLaunchHook
{
    public const string HookGuidString = "F31DC52A-963D-4C3E-8621-1FA12E52467B";
    public const string HookRegistrationName = "VSOnDemandSymbols_LaunchHook";

    private IVsDebugLaunchHook? _nextHook;

    public int SetNextHook(IVsDebugLaunchHook pNextHook)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _nextHook = pNextHook;
        return VSConstants.S_OK;
    }

    public int IsProcessRecycleRequired(VsDebugTargetProcessInfo[] pProcessInfo)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_nextHook != null)
        {
            return _nextHook.IsProcessRecycleRequired(pProcessInfo);
        }
        return VSConstants.S_FALSE;
    }

    public int OnLaunchDebugTargets(
        uint DebugTargetCount,
        VsDebugTargetInfo3[] pDebugTargets,
        VsDebugTargetProcessInfo[] pLaunchResults)
    {
        try
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ReconcileForLaunch();
        }
        catch (Exception ex)
        {
            // Golden Rule #2: Never break the debug session
            Trace.WriteLine($"[On-Demand Symbols] Error in launch hook: {ex}");
        }

        if (_nextHook != null)
        {
            return _nextHook.OnLaunchDebugTargets(DebugTargetCount, pDebugTargets, pLaunchResults);
        }

        return VSConstants.S_OK;
    }

    private void ReconcileForLaunch()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var monitor = VSOnDemandSymbolsPackage.CurrentSessionMonitor;
        var settingsManager = VSOnDemandSymbolsPackage.CurrentSettingsManager;
        var logger = VSOnDemandSymbolsPackage.Logger ?? NullLogger.Instance;

        if (monitor != null && monitor.IsEnabled && settingsManager != null)
        {
            // Unreal target launching in this instance: populate IncludeList just-in-time
            var wanted = monitor.ResolveWantedBinariesFromBreakpoints(out int bpCount);
            settingsManager.Reconcile(wanted, $"launch hook (breakpoints: {bpCount})");
            return;
        }

        // Non-Unreal launch target (or package unloaded).
        // Check if baseline exists in the shared hive directory; if so, restore it (Rule P1: correctness beats speed).
        if (settingsManager != null)
        {
            settingsManager.RestoreBaselineIfActive("non-Unreal launch hook");
        }
        else
        {
            // Package is not loaded in this instance (e.g. pure C# or web project).
            // Check if baseline.json exists on disk without loading full package services.
            TryRestoreBaselineWhenPackageUnloaded(logger);
        }
    }

    private static void TryRestoreBaselineWhenPackageUnloaded(ILogger logger)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var vsShell = Package.GetGlobalService(typeof(SVsShell)) as IVsShell
                       ?? ServiceProvider.GlobalProvider.GetService(typeof(SVsShell)) as IVsShell;
            string hiveDirectory = SymbolSettingsManager.ResolveDefaultHiveDirectory(vsShell);
            string baselinePath = Path.Combine(hiveDirectory, "baseline.json");

            if (!File.Exists(baselinePath))
            {
                return; // Zero-touch for normal non-Unreal launch
            }

            var vsDebugger = Package.GetGlobalService(typeof(SVsShellDebugger))
                          ?? ServiceProvider.GlobalProvider.GetService(typeof(SVsShellDebugger));
            if (vsDebugger is IVsDebuggerSymbolSettingsManager120A debuggerSettingsManager)
            {
                string hiveId = SymbolSettingsManager.ResolveDefaultHiveId(vsShell);
                var coordinator = new HiveCoordinator(PhysicalFileSystem.Instance, logger);
                using var manager = new SymbolSettingsManager(
                    debuggerSettingsManager,
                    coordinator,
                    logger,
                    hiveDirectory,
                    hiveId);

                manager.RestoreBaselineIfActive("non-Unreal launch hook (package unloaded)");
            }
        }
        catch (Exception ex)
        {
            logger.Warning($"Could not restore baseline on non-Unreal launch: {ex.Message}");
        }
    }
}
