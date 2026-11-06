using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Debugger.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VSOnDemandSymbols.Core.Abstractions;
using VSOnDemandSymbols.Core.Modules;
using VSOnDemandSymbols.Core.State;
using VSOnDemandSymbols.Debugger;
using VSOnDemandSymbols.Logging;

namespace VSOnDemandSymbols;

/// <summary>
/// Async VSIX package for On-Demand Symbols (SPEC.md §4.1).
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
public sealed class VSOnDemandSymbolsPackage : AsyncPackage, IVsSolutionEvents
{
    public const string PackageGuidString = "3214abc4-2e97-4c16-8583-86beddfd1d3b";
    public const string CppSolutionUiContextGuidString = "4f8d2b19-6a34-4e52-9c71-83b4a1e0c512";

    private OutputLog? _logger;
    private SymbolSettingsManager? _symbolSettingsManager;
    private UnrealModuleResolver? _resolver;
    private DebuggerSessionMonitor? _sessionMonitor;

    private IVsSolution? _vsSolution;
    private DTE2? _dte;
    private uint _solutionEventsCookie;
    private bool _isUnrealSolutionOpen;

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
            _vsSolution = await GetServiceAsync(typeof(SVsSolution)) as IVsSolution;
            _dte = await GetServiceAsync(typeof(SDTE)) as DTE2;

            if (debuggerObj is not IVsDebuggerSymbolSettingsManager120A settingsManager ||
                debuggerObj is not IVsDebugger vsDebugger ||
                _vsSolution == null ||
                _dte == null)
            {
                _logger.Warning("Required Visual Studio debugger or solution services were unavailable during initialization.");
                return;
            }

            string backupFilePath = SymbolSettingsManager.ResolveDefaultBackupFilePath(vsShell);
            var backupStore = new SymbolSettingsBackupStore(
                PhysicalFileSystem.Instance,
                SystemClock.Instance,
                _logger);

            _symbolSettingsManager = new SymbolSettingsManager(
                settingsManager,
                backupStore,
                _logger,
                backupFilePath);

            // SPEC.md §1.2: If a backup file exists from a previous crash, restore it immediately.
            _symbolSettingsManager.RecoverFromCrashIfNeeded();

            _resolver = new UnrealModuleResolver(PhysicalFileSystem.Instance);
            _sessionMonitor = new DebuggerSessionMonitor(_dte, vsDebugger, JoinableTaskFactory, _symbolSettingsManager, _resolver, _logger);

            _vsSolution.AdviseSolutionEvents(this, out _solutionEventsCookie);

            // Check currently open solution
            EvaluateOpenSolution();
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to initialize On-Demand Symbols package.", ex);
        }
    }

    private void EvaluateOpenSolution()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            string? solutionPath = _dte?.Solution?.FullName;
            if (string.IsNullOrWhiteSpace(solutionPath))
            {
                HandleSolutionClosed();
                return;
            }

            string? solutionDir = Path.GetDirectoryName(solutionPath);
            bool isUnreal = !string.IsNullOrEmpty(solutionDir) && (_resolver?.IsUnrealSolution(solutionDir!) ?? false);

            if (isUnreal)
            {
                _isUnrealSolutionOpen = true;
                if (_sessionMonitor != null)
                {
                    _sessionMonitor.IsEnabled = true;
                }
                _logger?.Info($"Unreal solution detected: '{Path.GetFileName(solutionPath)}'. On-Demand Symbols active.");
                _sessionMonitor?.UpdateIncludeListFromBreakpoints("solution opened");
            }
            else
            {
                HandleSolutionClosed();
            }
        }
        catch (Exception ex)
        {
            _logger?.Error("Failed to evaluate solution for Unreal Engine compatibility.", ex);
        }
    }

    private void HandleSolutionClosed()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_isUnrealSolutionOpen)
        {
            _isUnrealSolutionOpen = false;
            if (_sessionMonitor != null)
            {
                _sessionMonitor.IsEnabled = false;
            }
            _symbolSettingsManager?.RestoreOriginalSettings();
            _logger?.Info("Solution closed; restored original symbol settings.");
        }
    }

    #region IVsSolutionEvents

    public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        EvaluateOpenSolution();
        return VSConstants.S_OK;
    }

    public int OnBeforeCloseSolution(object pUnkReserved)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        HandleSolutionClosed();
        return VSConstants.S_OK;
    }

    public int OnAfterCloseSolution(object pUnkReserved)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        HandleSolutionClosed();
        return VSConstants.S_OK;
    }

    public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded) => VSConstants.S_OK;
    public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => VSConstants.S_OK;
    public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => VSConstants.S_OK;
    public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) => VSConstants.S_OK;
    public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => VSConstants.S_OK;
    public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => VSConstants.S_OK;
    public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => VSConstants.S_OK;

    #endregion

    protected override void Dispose(bool disposing)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (disposing)
        {
            try
            {
                if (_solutionEventsCookie != 0 && _vsSolution != null)
                {
                    _vsSolution.UnadviseSolutionEvents(_solutionEventsCookie);
                    _solutionEventsCookie = 0;
                }

                _sessionMonitor?.Dispose();
                _symbolSettingsManager?.RestoreOriginalSettings();
            }
            catch
            {
                // Never throw during package disposal
            }
        }

        base.Dispose(disposing);
    }
}
