using System;
using System.Collections.Generic;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using VSOnDemandSymbols.Core.Abstractions;
using VSOnDemandSymbols.Core.Modules;
using VSOnDemandSymbols.Core.Policy;

namespace VSOnDemandSymbols.Debugger;

/// <summary>
/// Monitors debugger launch (F5) and break events to maintain <c>IncludeList</c>
/// and trigger on-demand symbol loading (SPEC.md §4.1).
/// </summary>
public sealed class DebuggerSessionMonitor : IDisposable
{
    private static readonly Guid Std97CmdSetGuid = new("5efc7975-14bc-11cf-9b2b-00aa00573819");
    private const int CmdIdStart = 295; // F5 Launch

    private readonly DTE2 _dte;
    private readonly SymbolSettingsManager _settings;
    private readonly UnrealModuleResolver _resolver;
    private readonly ILogger _logger;

    private readonly CommandEvents _launchCommandEvents;
    private readonly DebuggerEvents _debuggerEvents;
    private bool _disposed;

    /// <summary>
    /// Gets or sets whether session monitoring and symbol management are currently active.
    /// Automatically activated when an Unreal solution is open and deactivated when closed.
    /// </summary>
    public bool IsEnabled { get; set; }

    public DebuggerSessionMonitor(
        DTE2 dte,
        SymbolSettingsManager settings,
        UnrealModuleResolver resolver,
        ILogger logger)
    {
        _dte = dte ?? throw new ArgumentNullException(nameof(dte));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ThreadHelper.ThrowIfNotOnUIThread();

        _launchCommandEvents = _dte.Events.get_CommandEvents(Std97CmdSetGuid.ToString("B"), CmdIdStart);
        _launchCommandEvents.BeforeExecute += OnBeforeLaunch;

        _debuggerEvents = _dte.Events.DebuggerEvents;
        _debuggerEvents.OnEnterBreakMode += OnEnterBreakMode;
    }

    /// <summary>
    /// Resolves enabled breakpoints to candidate binary names and populates Visual Studio's <c>IncludeList</c>.
    /// </summary>
    public void UpdateIncludeListFromBreakpoints(string reason = "before launch")
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!IsEnabled)
        {
            return;
        }

        try
        {
            var wantedBinaries = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "UnrealEditor*.exe"
            };

            int breakpointCount = 0;
            if (_dte.Debugger?.Breakpoints != null)
            {
                foreach (Breakpoint bp in _dte.Debugger.Breakpoints)
                {
                    if (!bp.Enabled || string.IsNullOrWhiteSpace(bp.File))
                    {
                        continue;
                    }

                    string? moduleName = _resolver.ResolveModuleFromSource(bp.File);
                    if (!string.IsNullOrWhiteSpace(moduleName))
                    {
                        breakpointCount++;
                        foreach (string binary in _resolver.GetCandidateBinaries(moduleName!))
                        {
                            wantedBinaries.Add(binary);
                        }
                    }
                }
            }

            _settings.ApplyManualMode(wantedBinaries, reason: $"{reason} (active breakpoints: {breakpointCount})");
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to update IncludeList from active breakpoints.", ex);
        }
    }

    private void OnBeforeLaunch(string guid, int id, object customIn, object customOut, ref bool cancelDefault)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!IsEnabled)
        {
            return;
        }

        UpdateIncludeListFromBreakpoints("before launch");
    }

    private void OnEnterBreakMode(dbgEventReason reason, ref dbgExecutionAction executionAction)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!IsEnabled)
        {
            return;
        }

        try
        {
            var currentThread = _dte.Debugger?.CurrentThread;
            if (currentThread?.StackFrames == null)
            {
                return;
            }

            var missingModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (StackFrame frame in currentThread.StackFrames)
            {
                string modName = frame.Module;
                if (!string.IsNullOrWhiteSpace(modName) && modName.IndexOf(".dll", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string clean = WildcardPattern.ExtractFileNameSafe(modName);
                    if (!string.IsNullOrWhiteSpace(clean))
                    {
                        missingModules.Add(clean);
                    }
                }
            }

            if (missingModules.Count == 0 || _dte.Debugger?.DebuggedProcesses == null)
            {
                return;
            }

            foreach (Process proc in _dte.Debugger.DebuggedProcesses)
            {
                if (proc is EnvDTE90.Process3 proc3 && proc3.Modules != null)
                {
                    foreach (EnvDTE90.Module mod in proc3.Modules)
                    {
                        string modFileName = WildcardPattern.ExtractFileNameSafe(mod.Name);
                        if (missingModules.Contains(modFileName))
                        {
                            _logger.Info($"Loading symbols on demand: {modFileName} — CallStack");
                            mod.LoadSymbols(string.Empty);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Never disrupt the debug session (Golden Rule #2)
            _logger.Error("Error handling break mode symbol loads.", ex);
        }
    }

    public void Dispose()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _launchCommandEvents.BeforeExecute -= OnBeforeLaunch;
            _debuggerEvents.OnEnterBreakMode -= OnEnterBreakMode;
        }
        catch
        {
            // Ignore shutdown errors
        }
    }
}
