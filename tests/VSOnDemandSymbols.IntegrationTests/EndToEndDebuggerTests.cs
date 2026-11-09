using System;
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

    public void Test01_SolutionOpen_DetectsUnrealSolutionAndCreatesBackup()
    {
        var dte = _fixture.Dte ?? throw new InvalidOperationException("DTE not attached.");

        Console.WriteLine("  -> Opening MiniUnreal.sln...");
        dte.Solution.Open(_fixture.MiniUnrealSolutionPath);

        bool detected = _fixture.WaitUntilOutputContains(
            "Unreal solution detected: 'MiniUnreal.sln'. On-Demand Symbols active.",
            TimeSpan.FromSeconds(30),
            out string logOutput);

        Assert.True(detected, $"Expected Unreal solution detection log in Output pane, but got:\n{logOutput}");

        var (backupPath, backupDto) = _fixture.FindBackupFileForCurrentProcess();
        Assert.False(string.IsNullOrWhiteSpace(backupPath), "Expected symbol-settings-backup.json to be created for current devenv PID.");
        Assert.NotNull(backupDto);
        Assert.Equal(_fixture.DevenvProcess!.Id, backupDto!.ProcessId);
    }

    public void Test02_PreLaunchBreakpoint_LoadsSymbolsOnlyForBreakpointModule()
    {
        var dte = _fixture.Dte ?? throw new InvalidOperationException("DTE not attached.");
        _fixture.StopDebuggingIfActive();
        _fixture.ClearAllBreakpoints();
        _fixture.ClearOnDemandSymbolsOutputPane();

        _fixture.SetMiniUnrealTestMode("breakpoint_mod_a");

        // Set a breakpoint in ModuleA.cpp at line 8 (RunModuleAWork)
        dte.Debugger.Breakpoints.Add(File: _fixture.ModuleASourcePath, Line: 8);

        Console.WriteLine("  -> Starting debugger (Debug.Start) with breakpoint in ModuleA.cpp...");
        dte.ExecuteCommand("Debug.Start");

        bool hitBreak = _fixture.WaitForDebuggerMode(dbgDebugMode.dbgBreakMode, TimeSpan.FromSeconds(30));
        Assert.True(hitBreak, "Expected debugger to hit breakpoint in ModuleA.cpp and enter BreakMode.");

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
    }

    public void Test03_CrashInUnsymbolizedModule_LoadsSymbolsAndSetsContextAndOpensSourceFile()
    {
        var dte = _fixture.Dte ?? throw new InvalidOperationException("DTE not attached.");
        _fixture.StopDebuggingIfActive();
        _fixture.ClearAllBreakpoints();
        _fixture.ClearOnDemandSymbolsOutputPane();

        // Close all open documents so no source file is pre-opened before the crash
        try
        {
            dte.Documents.CloseAll(vsSaveChanges.vsSaveChangesNo);
        }
        catch
        {
        }

        // Configure target to crash inside ModuleB::TriggerCrashInModuleB (via ModuleA::DispatchToModuleB)
        // with zero breakpoints set, so neither ModuleA nor ModuleB has symbols loaded when the crash happens.
        _fixture.SetMiniUnrealTestMode("crash_mod_b");

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

        // Allow VS UI a brief moment to settle frame context & active document
        System.Threading.Thread.Sleep(500);

        var currentFrame = dte.Debugger.CurrentStackFrame;
        Assert.NotNull(currentFrame);

        string frameFunc = currentFrame.FunctionName ?? string.Empty;
        string frameModule = Path.GetFileName(currentFrame.Module ?? string.Empty);
        string? activeDocFullName = null;
        int activeSelectionLine = 0;

        try
        {
            activeDocFullName = dte.ActiveDocument?.FullName;
            if (dte.ActiveDocument?.Selection is TextSelection sel)
            {
                activeSelectionLine = sel.ActivePoint.Line;
            }
        }
        catch
        {
        }

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
        var dte = _fixture.Dte ?? throw new InvalidOperationException("DTE not attached.");
        _fixture.StopDebuggingIfActive();
        _fixture.ClearAllBreakpoints();
        _fixture.ClearOnDemandSymbolsOutputPane();

        _fixture.SetMiniUnrealTestMode("wait_for_bp_mod_b");

        Console.WriteLine("  -> Starting debugger (Debug.Start) in wait_for_bp_mod_b loop with zero breakpoints...");
        dte.ExecuteCommand("Debug.Start");

        bool running = _fixture.WaitForDebuggerMode(dbgDebugMode.dbgRunMode, TimeSpan.FromSeconds(15));
        Assert.True(running, "Expected target to be running in dbgRunMode.");

        // Give the process 500ms to enter the loop with ModuleB unsymbolized
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

    public void Test05_SolutionCloseAndCrashRecovery_RestoresOriginalSymbolSettings()
    {
        var dte = _fixture.Dte ?? throw new InvalidOperationException("DTE not attached.");
        _fixture.StopDebuggingIfActive();
        _fixture.ClearAllBreakpoints();

        var (backupPathBeforeClose, _) = _fixture.FindBackupFileForCurrentProcess();
        Assert.False(string.IsNullOrWhiteSpace(backupPathBeforeClose), "Expected backup file to exist while solution is open.");

        Console.WriteLine("  -> Closing MiniUnreal.sln to verify clean settings restore...");
        dte.Solution.Close(SaveFirst: false);

        bool restoredOnClose = _fixture.WaitUntilOutputContains(
            "Solution closed; restored original symbol settings.",
            TimeSpan.FromSeconds(15),
            out string closeLog);

        Assert.True(restoredOnClose, $"Expected solution close restore message in Output pane, but got:\n{closeLog}");
        Assert.False(File.Exists(backupPathBeforeClose!), "Expected backup JSON file to be deleted after solution close.");

        // Now test crash recovery: reopen solution so backup file is written, kill devenv.exe, and relaunch
        Console.WriteLine("  -> Reopening MiniUnreal.sln and simulating devenv.exe crash (Kill)...");
        dte.Solution.Open(_fixture.MiniUnrealSolutionPath);
        bool reopened = _fixture.WaitUntilOutputContains(
            "Unreal solution detected: 'MiniUnreal.sln'. On-Demand Symbols active.",
            TimeSpan.FromSeconds(20),
            out _);
        Assert.True(reopened, "Expected solution to reopen and activate On-Demand Symbols.");

        var (crashBackupPath, _) = _fixture.FindBackupFileForCurrentProcess();
        Assert.False(string.IsNullOrWhiteSpace(crashBackupPath), "Expected backup file to exist before simulated crash.");

        // Force-kill devenv.exe so backup file is orphaned on disk
        _fixture.KillCurrentInstanceIfRunning();
        Assert.True(File.Exists(crashBackupPath!), "Expected orphaned backup file to remain on disk after devenv.exe crash.");

        // Launch a new Experimental instance and open the solution so the package loads and runs RecoverFromCrashIfNeeded()
        _fixture.StartAndAttach();
        _fixture.Dte!.Solution.Open(_fixture.MiniUnrealSolutionPath);

        bool recovered = _fixture.WaitUntilOutputContains(
            "Restored symbol settings after a previous unexpected exit",
            TimeSpan.FromSeconds(30),
            out string recoveryLog);

        Assert.True(recovered, $"Expected crash recovery warning in Output pane after restart, but got:\n{recoveryLog}");
    }
}

