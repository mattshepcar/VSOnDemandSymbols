using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using EnvDTE;
using Xunit;

namespace VSOnDemandSymbols.IntegrationTests;

/// <summary>
/// End-to-end automated integration tests running against a live Visual Studio Experimental instance
/// (<c>devenv.exe /rootsuffix Exp</c>) with the <c>MiniUnreal</c> C++ test fixture.
/// </summary>
public sealed class EndToEndDebuggerTests
{
    private readonly VisualStudioExpFixture _fixture;

    public EndToEndDebuggerTests(VisualStudioExpFixture fixture)
    {
        _fixture = fixture ?? throw new ArgumentNullException(nameof(fixture));
    }

    public void Test01_SolutionOpen_DetectsUnrealSolutionAndDoesNotTouchSettings()
    {
        var dte = _fixture.Dte ?? throw new InvalidOperationException("DTE not attached.");

        Console.WriteLine("  -> Opening MiniUnreal.sln...");
        _fixture.ClearOnDemandSymbolsOutputPane();
        dte.Solution.Open(_fixture.MiniUnrealSolutionPath);

        bool detected = _fixture.WaitUntilOutputContains(
            "Unreal solution detected: 'MiniUnreal.sln'. On-Demand Symbols active.",
            TimeSpan.FromSeconds(30),
            out string logOutput);

        Assert.True(detected, $"Expected Unreal solution detection log in Output pane, but got:\n{logOutput}");

        // SPEC.md §4.1 / ADR-15: Opening an Unreal solution only arms the session monitor;
        // it must NEVER write symbol settings or create a baseline/lease file until launch.
        var (baselinePath, _) = _fixture.FindBaselineFile();
        Assert.True(string.IsNullOrWhiteSpace(baselinePath), $"Expected NO baseline.json on solution open (just-in-time launch hook rule R1), but found: {baselinePath}");

        var (leasePath, _) = _fixture.FindLeaseFileForCurrentProcess();
        Assert.True(string.IsNullOrWhiteSpace(leasePath), $"Expected NO lease file on solution open, but found: {leasePath}");
    }

    public void Test02_PreLaunchBreakpoint_LoadsSymbolsOnlyForBreakpointModule()
    {
        var dte = _fixture.PrepareMiniUnrealTest("breakpoint_mod_a");

        // Set a breakpoint in ModuleA.cpp at line 8 (RunModuleAWork)
        dte.Debugger.Breakpoints.Add(File: _fixture.ModuleASourcePath, Line: 8);

        Console.WriteLine("  -> Starting debugger (Debug.Start) with breakpoint in ModuleA.cpp...");
        dte.ExecuteCommand("Debug.Start");

        bool hitBreak = _fixture.WaitForDebuggerMode(dbgDebugMode.dbgBreakMode, TimeSpan.FromSeconds(30));
        Assert.True(hitBreak, "Expected debugger to hit breakpoint in ModuleA.cpp and enter BreakMode.");

        // Verify baseline and lease are created upon debug launch
        var (baselinePath, baselineDto) = _fixture.FindBaselineFile();
        Assert.False(string.IsNullOrWhiteSpace(baselinePath), "Expected baseline.json to be created upon debug launch.");
        Assert.NotNull(baselineDto);

        var (leasePath, leaseDto) = _fixture.FindLeaseFileForCurrentProcess();
        Assert.False(string.IsNullOrWhiteSpace(leasePath), "Expected lease file to be created for current devenv PID upon debug launch.");
        Assert.NotNull(leaseDto);
        Assert.Equal(_fixture.DevenvProcess!.Id, leaseDto!.ProcessId);

        var modA = _fixture.FindDebuggedModule("UnrealEditor-ModuleA.dll");
        var modB = _fixture.FindDebuggedModule("UnrealEditor-ModuleB.dll");
        Assert.NotNull(modA);
        Assert.NotNull(modB);

        string? modASymbols = modA!.SymbolFile;
        string? modBSymbols = modB!.SymbolFile;
        Console.WriteLine($"  -> ModuleA SymbolFile: '{modASymbols}'");
        Console.WriteLine($"  -> ModuleB SymbolFile: '{modBSymbols}'");

        Assert.False(string.IsNullOrWhiteSpace(modASymbols), "Expected UnrealEditor-ModuleA.dll (has breakpoint) to have symbols loaded at launch.");
        Assert.True(string.IsNullOrWhiteSpace(modBSymbols), "Expected UnrealEditor-ModuleB.dll (no breakpoint, not on stack) to NOT have symbols loaded.");

        _fixture.StopDebuggingIfActive();
        _fixture.ClearAllBreakpoints();

        // Verify lease and baseline are released when session ends
        var (postLeasePath, _) = _fixture.FindLeaseFileForCurrentProcess();
        Assert.True(string.IsNullOrWhiteSpace(postLeasePath), "Expected lease file to be removed when debug session ends.");
        var (postBaselinePath, _) = _fixture.FindBaselineFile();
        Assert.True(string.IsNullOrWhiteSpace(postBaselinePath), "Expected baseline.json to be deleted and settings restored when last lease ends.");
    }

    public void Test03_CrashInUnsymbolizedModule_LoadsSymbolsAndSetsContextAndOpensSourceFile()
    {
        // Configure target to crash inside ModuleB::TriggerCrashInModuleB (via ModuleA::DispatchToModuleB)
        // with zero breakpoints set, so neither ModuleA nor ModuleB has symbols loaded when the crash happens.
        var dte = _fixture.PrepareMiniUnrealTest("crash_mod_b");

        // Close all open documents so no source file is pre-opened before the crash
        try
        {
            dte.Documents.CloseAll(vsSaveChanges.vsSaveChangesNo);
        }
        catch
        {
        }

        Console.WriteLine("  -> Starting debugger (Debug.Start) in crash_mod_b mode with zero breakpoints...");
        dte.ExecuteCommand("Debug.Start");

        bool hitBreak = _fixture.WaitForDebuggerMode(dbgDebugMode.dbgBreakMode, TimeSpan.FromSeconds(30));
        Assert.True(hitBreak, "Expected debugger to enter BreakMode on crash in ModuleB.");

        // Wait for OnEnterBreakMode on-demand symbol loading to complete
        bool loadedOnDemand = _fixture.WaitUntilOutputContains(
            "[Profile] BreakMode:",
            TimeSpan.FromSeconds(15),
            out string logOutput);

        Console.WriteLine($"  -> Output pane after crash break:\n{logOutput.Trim()}");
        Assert.True(loadedOnDemand, $"Expected OnEnterBreakMode to log on-demand symbol loading and [Profile] BreakMode telemetry, but got:\n{logOutput}");

        var profile = ProfileMetricsParser.Parse(logOutput);
        Assert.NotEmpty(profile.BreakModeEvents);
        var breakMetric = profile.BreakModeEvents[0];
        Assert.True(breakMetric.ThreadsInspected >= 1, "Expected at least 1 thread inspected in BreakMode profile metric.");
        Assert.True(breakMetric.ModulesScanned >= 2, "Expected at least 2 modules scanned in BreakMode profile metric.");
        Assert.True(breakMetric.ModulesLoaded >= 2, "Expected ModuleA and ModuleB loaded in BreakMode profile metric.");
        Console.WriteLine(
            $"  -> Parsed BreakMode Profile: total={breakMetric.TotalMs}ms, stackWalk={breakMetric.StackWalkMs}ms " +
            $"(threads={breakMetric.ThreadsInspected}, frames={breakMetric.FramesInspected}), " +
            $"moduleScan={breakMetric.ModuleScanMs}ms (modules={breakMetric.ModulesScanned}), " +
            $"symbolLoad={breakMetric.SymbolLoadMs}ms (loaded={breakMetric.ModulesLoaded}/{breakMetric.CandidateTargetModules}), " +
            $"contextRefresh={breakMetric.ContextRefreshMs}ms");

        var modB = _fixture.FindDebuggedModule("UnrealEditor-ModuleB.dll");
        var modA = _fixture.FindDebuggedModule("UnrealEditor-ModuleA.dll");
        Assert.NotNull(modB);
        Assert.NotNull(modA);
        Assert.False(string.IsNullOrWhiteSpace(modB!.SymbolFile), "Expected UnrealEditor-ModuleB.dll symbols to be loaded on demand on crash.");
        Assert.False(string.IsNullOrWhiteSpace(modA!.SymbolFile), "Expected UnrealEditor-ModuleA.dll symbols (caller on stack) to be loaded on demand on crash.");

        var (activeDocFullName, activeSelectionLine) = WaitForActiveDocumentAndLine(dte, TimeSpan.FromSeconds(5));

        var currentFrame = dte.Debugger.CurrentStackFrame;
        Assert.NotNull(currentFrame);

        string frameFunc = currentFrame.FunctionName ?? string.Empty;
        string frameModule = Path.GetFileName(currentFrame.Module ?? string.Empty);

        Console.WriteLine($"  -> CurrentStackFrame: Function='{frameFunc}', Module='{frameModule}'");
        Console.WriteLine($"  -> ActiveDocument: '{activeDocFullName}' (Line {activeSelectionLine})");

        Assert.Contains("TriggerCrashInModuleB", frameFunc, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("UnrealEditor-ModuleB.dll", frameModule, ignoreCase: true);

        Assert.False(
            string.IsNullOrWhiteSpace(activeDocFullName),
            "Expected the crashing source file (ModuleB.cpp) to be open as the ActiveDocument in the debugger, but ActiveDocument was null.");

        Assert.Equal(
            Path.GetFullPath(_fixture.ModuleBSourcePath),
            Path.GetFullPath(activeDocFullName!),
            ignoreCase: true);
        Assert.Equal(15, activeSelectionLine);

        _fixture.StopDebuggingIfActive();
    }

    public void Test04_AddBreakpointWhileRunning_LoadsSymbolsOnDemandAndBindsBreakpoint()
    {
        var dte = _fixture.PrepareMiniUnrealTest("wait_for_bp_mod_b");

        Console.WriteLine("  -> Starting debugger (Debug.Start) in wait_for_bp_mod_b loop with zero breakpoints...");
        dte.ExecuteCommand("Debug.Start");

        bool running = _fixture.WaitForDebuggerMode(dbgDebugMode.dbgRunMode, TimeSpan.FromSeconds(15));
        Assert.True(running, "Expected target to be running in dbgRunMode.");

        // Give the process a brief moment to enter the loop with ModuleB unsymbolized
        System.Threading.Thread.Sleep(500);

        Console.WriteLine("  -> Adding breakpoint in ModuleB.cpp (TickModuleB, line 21) while process is running...");
        dte.Debugger.Breakpoints.Add(File: _fixture.ModuleBSourcePath, Line: 21);

        bool loadedForBp = _fixture.WaitUntilOutputContains(
            "[Profile] BreakpointBind:",
            TimeSpan.FromSeconds(15),
            out string logOutput);

        Assert.True(loadedForBp, $"Expected mid-session breakpoint to load symbols for UnrealEditor-ModuleB.dll and emit [Profile] BreakpointBind, but got:\n{logOutput}");
        Assert.Contains("UnrealEditor-ModuleB.dll — Breakpoint", logOutput, StringComparison.OrdinalIgnoreCase);

        var profile = ProfileMetricsParser.Parse(logOutput);
        Assert.NotEmpty(profile.BreakpointBindEvents);
        var bpMetric = profile.BreakpointBindEvents[0];
        Assert.True(bpMetric.ModulesLoaded >= 1, "Expected at least 1 module loaded in BreakpointBind profile metric.");
        Console.WriteLine(
            $"  -> Parsed BreakpointBind Profile: total={bpMetric.TotalMs}ms, includeList={bpMetric.IncludeListMs}ms, " +
            $"moduleScan={bpMetric.ModuleScanMs}ms (modules={bpMetric.ModulesScanned}), " +
            $"symbolLoad={bpMetric.SymbolLoadMs}ms (loaded={bpMetric.ModulesLoaded})");

        bool hitBreak = _fixture.WaitForDebuggerMode(dbgDebugMode.dbgBreakMode, TimeSpan.FromSeconds(15));
        Assert.True(hitBreak, "Expected mid-session breakpoint in ModuleB.cpp to bind and hit while running.");

        _fixture.StopDebuggingIfActive();
        _fixture.ClearAllBreakpoints();
    }

    public void Test05_SessionLifecycleAndCrashRecovery_ManagesBaselineAndPrunesDeadLeases()
    {
        var dte = _fixture.PrepareMiniUnrealTest("wait_for_bp_mod_b");

        // 1. Launch debug target and verify baseline and lease exist during active debugging
        Console.WriteLine("  -> Starting debugger (Debug.Start) to create active lease...");
        dte.ExecuteCommand("Debug.Start");
        bool running = _fixture.WaitForDebuggerMode(dbgDebugMode.dbgRunMode, TimeSpan.FromSeconds(15));
        Assert.True(running, "Expected target to be running in dbgRunMode.");

        var (activeBaselinePath, _) = _fixture.FindBaselineFile();
        var (activeLeasePath, _) = _fixture.FindLeaseFileForCurrentProcess();
        Assert.False(string.IsNullOrWhiteSpace(activeBaselinePath), "Expected baseline.json during active debugging.");
        Assert.False(string.IsNullOrWhiteSpace(activeLeasePath), "Expected active lease during debugging.");

        // 2. Stop debugging and verify clean release of lease and baseline
        Console.WriteLine("  -> Stopping debugger to verify clean session end release...");
        _fixture.StopDebuggingIfActive();

        var (postStopLease, _) = _fixture.FindLeaseFileForCurrentProcess();
        var (postStopBaseline, _) = _fixture.FindBaselineFile();
        Assert.True(string.IsNullOrWhiteSpace(postStopLease), "Expected lease deleted after session end.");
        Assert.True(string.IsNullOrWhiteSpace(postStopBaseline), "Expected baseline deleted after last session end.");

        // 3. Test crash recovery: start debug session so lease and baseline are created, then force-kill devenv.exe
        Console.WriteLine("  -> Starting debugger again and simulating devenv.exe crash (Kill)...");
        dte.ExecuteCommand("Debug.Start");
        _fixture.WaitForDebuggerMode(dbgDebugMode.dbgRunMode, TimeSpan.FromSeconds(15));

        var (crashLeasePath, _) = _fixture.FindLeaseFileForCurrentProcess();
        var (crashBaselinePath, _) = _fixture.FindBaselineFile();
        Assert.False(string.IsNullOrWhiteSpace(crashLeasePath), "Expected lease before simulated crash.");
        Assert.False(string.IsNullOrWhiteSpace(crashBaselinePath), "Expected baseline before simulated crash.");

        // Force-kill devenv.exe. OS closes the file handle immediately, but file remains on disk until pruned.
        _fixture.KillCurrentInstanceIfRunning();
        Assert.True(File.Exists(crashLeasePath!), "Expected lease file on disk after devenv.exe kill.");
        Assert.True(File.Exists(crashBaselinePath!), "Expected baseline file on disk after devenv.exe kill.");

        // 4. Launch a new Experimental instance: startup crash recovery prunes dead lease and restores baseline
        Console.WriteLine("  -> Restarting devenv and opening solution to trigger startup dead-lease pruning...");
        _fixture.StartAndAttach();
        var restartedDte = _fixture.Dte ?? throw new InvalidOperationException("DTE not attached after restart.");
        restartedDte.Solution.Open(_fixture.MiniUnrealSolutionPath);

        bool detected = _fixture.WaitUntilOutputContains(
            "Unreal solution detected",
            TimeSpan.FromSeconds(30),
            out string _);
        Assert.True(detected, "Expected package to load and initialize after restart.");

        // Verify dead lease was pruned and baseline restored
        Assert.False(File.Exists(crashLeasePath!), "Expected dead lease file to be pruned by startup crash recovery.");
        Assert.False(File.Exists(crashBaselinePath!), "Expected baseline to be restored and deleted after dead lease pruned.");
    }

    private static (string? FullName, int Line) WaitForActiveDocumentAndLine(EnvDTE80.DTE2 dte, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        string? fullName = null;
        int line = 0;

        while (sw.Elapsed < timeout)
        {
            try
            {
                fullName = dte.ActiveDocument?.FullName;
                if (dte.ActiveDocument?.Selection is TextSelection sel)
                {
                    line = sel.ActivePoint.Line;
                }

                if (!string.IsNullOrWhiteSpace(fullName) && line > 0)
                {
                    return (fullName, line);
                }
            }
            catch
            {
            }

            System.Threading.Thread.Sleep(50);
        }

        return (fullName, line);
    }
}

