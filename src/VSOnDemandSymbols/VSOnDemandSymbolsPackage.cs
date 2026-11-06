using System;
using System.ComponentModel.Design;
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
[ProvideMenuResource("Menus.ctmenu", 1)]
[Guid(PackageGuidString)]
public sealed class VSOnDemandSymbolsPackage : AsyncPackage, IVsSolutionEvents
{
    public const string PackageGuidString = "3214abc4-2e97-4c16-8583-86beddfd1d3b";
    public const string CppSolutionUiContextGuidString = "4f8d2b19-6a34-4e52-9c71-83b4a1e0c512";
    public static readonly Guid CommandSetGuid = new("a8435d64-e4c1-4b10-86c4-118809ff4ef0");
    public const int CmdIdToggleEnabled = 0x0100;

    private OutputLog? _logger;
    private SymbolSettingsManager? _symbolSettingsManager;
    private UnrealModuleResolver? _resolver;
    private DebuggerSessionMonitor? _sessionMonitor;

    private IVsSolution? _vsSolution;
    private DTE2? _dte;
    private uint _solutionEventsCookie;
    private bool _isUnrealSolutionOpen;
    private bool _isEnabled = true;

    public bool IsEnabled => _isEnabled;

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
            _sessionMonitor = new DebuggerSessionMonitor(_dte, _symbolSettingsManager, _resolver, _logger);

            // Register Debug > On-Demand Symbols > Enabled command
            if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
            {
                var menuCommandId = new CommandID(CommandSetGuid, CmdIdToggleEnabled);
                var menuItem = new OleMenuCommand(ExecuteToggleCommand, menuCommandId);
                menuItem.BeforeQueryStatus += OnBeforeQueryStatusToggleCommand;
                commandService.AddCommand(menuItem);
            }

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
                if (_isEnabled)
                {
                    _logger?.Info($"Unreal solution detected: '{Path.GetFileName(solutionPath)}'. On-Demand Symbols active.");
                    _sessionMonitor?.UpdateIncludeListFromBreakpoints("solution opened");
                }
                else
                {
                    _logger?.Info($"Unreal solution detected: '{Path.GetFileName(solutionPath)}', but On-Demand Symbols is currently disabled.");
                }
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
            _symbolSettingsManager?.RestoreOriginalSettings();
            _logger?.Info("Solution closed; restored original symbol settings.");
        }
    }

    private void OnBeforeQueryStatusToggleCommand(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (sender is OleMenuCommand command)
        {
            command.Checked = _isEnabled;
            command.Enabled = true;
        }
    }

    private void ExecuteToggleCommand(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        _isEnabled = !_isEnabled;
        if (_sessionMonitor != null)
        {
            _sessionMonitor.IsEnabled = _isEnabled;
        }

        if (!_isEnabled)
        {
            _symbolSettingsManager?.RestoreOriginalSettings();
            _logger?.Info("On-Demand Symbols disabled by user; restored original symbol settings.");
        }
        else
        {
            if (_isUnrealSolutionOpen)
            {
                _logger?.Info("On-Demand Symbols enabled by user; applying settings for Unreal solution.");
                _sessionMonitor?.UpdateIncludeListFromBreakpoints("user enabled");
            }
            else
            {
                _logger?.Info("On-Demand Symbols enabled by user.");
            }
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
