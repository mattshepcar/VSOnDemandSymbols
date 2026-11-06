using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Debugger.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VSOnDemandSymbols.Core.Abstractions;
using VSOnDemandSymbols.Core.State;
using VSOnDemandSymbols.Debugger;
using VSOnDemandSymbols.Logging;

namespace VSOnDemandSymbols;

/// <summary>
/// Async VSIX package for On-Demand Symbols (SPEC §7.4, §10 NF-4, ARCHITECTURE.md §3).
/// Auto-loads in the background when a solution containing C++ projects is opened.
/// </summary>
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[ProvideAutoLoad(CppSolutionUiContextGuidString, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideUIContextRule(
    CppSolutionUiContextGuidString,
    name: "On-Demand Symbols C++ Solution Load",
    expression: "HasVCProjectCapability | HasVCProjectFlavor",
    termNames: new[] { "HasVCProjectCapability", "HasVCProjectFlavor" },
    termValues: new[]
    {
        "SolutionHasProjectCapability:VisualC",
        "SolutionHasProjectFlavor:{8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942}"
    })]
[Guid(PackageGuidString)]
public sealed class VSOnDemandSymbolsPackage : AsyncPackage
{
    public const string PackageGuidString = "3214abc4-2e97-4c16-8583-86beddfd1d3b";
    public const string CppSolutionUiContextGuidString = "4f8d2b19-6a34-4e52-9c71-83b4a1e0c512";

    private OutputLog? _logger;
    private SymbolSettingsController? _symbolSettingsController;
    private SessionTracker? _sessionTracker;

    protected override async Task InitializeAsync(
        CancellationToken cancellationToken,
        IProgress<ServiceProgressData> progress)
    {
        await base.InitializeAsync(cancellationToken, progress);

        _logger = await OutputLog.CreateAsync(this, LogLevel.Info, cancellationToken);

        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        try
        {
            var vsShell = await GetServiceAsync(typeof(SVsShell)) as IVsShell;
            var debuggerObj = await GetServiceAsync(typeof(SVsShellDebugger));
            var vsSolution = await GetServiceAsync(typeof(SVsSolution)) as IVsSolution;
            var dte = await GetServiceAsync(typeof(SDTE)) as DTE2;

            if (debuggerObj is not IVsDebuggerSymbolSettingsManager120A settingsManager ||
                vsSolution == null ||
                dte == null)
            {
                _logger.Warning("Required Visual Studio debugger or solution services were unavailable during initialization.");
                return;
            }

            string backupFilePath = SymbolSettingsController.ResolveDefaultBackupFilePath(vsShell);
            var backupStore = new SymbolSettingsBackupStore(
                PhysicalFileSystem.Instance,
                SystemClock.Instance,
                _logger);

            _symbolSettingsController = new SymbolSettingsController(
                settingsManager,
                backupStore,
                _logger,
                backupFilePath);

            // SPEC §7.4: If a backup file exists from a previous crash, restore it before doing anything else.
            _symbolSettingsController.RecoverFromCrashIfNeeded();

            var bridge = ConcordSymbolBridge.Instance;
            bridge.Logger = _logger;

            var symbolLoader = new SymbolLoader(bridge, JoinableTaskFactory, _logger)
            {
                Dte = dte,
                SymbolSettingsController = _symbolSettingsController
            };
            bridge.SymbolLoader = symbolLoader;

            _sessionTracker = new SessionTracker(
                this,
                dte,
                vsSolution,
                _symbolSettingsController,
                bridge,
                symbolLoader,
                PhysicalFileSystem.Instance,
                SystemClock.Instance,
                _logger);

            bridge.SessionTracker = _sessionTracker;
            _sessionTracker.Initialize();
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to initialize On-Demand Symbols package.", ex);
        }
    }

    protected override void Dispose(bool disposing)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (disposing)
        {
            try
            {
                _sessionTracker?.Dispose();
                _symbolSettingsController?.RestoreOriginalSettings();
            }
            catch
            {
                // Never throw during package disposal (NF-5)
            }
        }

        base.Dispose(disposing);
    }
}
