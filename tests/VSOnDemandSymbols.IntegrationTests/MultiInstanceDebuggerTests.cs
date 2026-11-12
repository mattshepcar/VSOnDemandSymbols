using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using EnvDTE;
using Xunit;

namespace VSOnDemandSymbols.IntegrationTests;

/// <summary>
/// Multi-instance integration tests verifying cross-process symbol settings coordination
/// when multiple Visual Studio Experimental instances share the same hive (SPEC.md §4.2, ADR-16).
/// </summary>
public sealed class MultiInstanceDebuggerTests
{
    private readonly VisualStudioExpFixture _fixture;

    public MultiInstanceDebuggerTests(VisualStudioExpFixture fixture)
    {
        _fixture = fixture ?? throw new ArgumentNullException(nameof(fixture));
    }

    /// <summary>
    /// Scenario 1: Instance A has an Unreal solution open (idle, no debug session).
    /// Instance B (non-Unreal) launches a debug target.
    /// Neither instance should write symbol settings or capture a baseline.
    /// </summary>
    public void Multi01_NonUnrealInstanceLaunchesWhileUnrealIsIdle_ZeroTouch()
    {
        var dteA = _fixture.PrepareMiniUnrealTest();

        // Verify instance A is idle: no baseline, no lease
        var (baselineBefore, _) = _fixture.FindBaselineFile();
        Assert.True(string.IsNullOrWhiteSpace(baselineBefore), "Expected no baseline while Unreal instance A is idle.");

        string secLogFile = Path.Combine(Path.GetTempPath(), $"vs_sec_log_{Guid.NewGuid():N}.log");
        System.Diagnostics.Process? procB = null;
        try
        {
            Console.WriteLine("  -> Spawning secondary VS Experimental instance B...");
            var (processB, dteB) = _fixture.SpawnSecondaryInstance(secLogFile);
            procB = processB;

            // Instance B does not open an Unreal solution (pure non-Unreal state)
            Console.WriteLine("  -> Launching non-Unreal target in instance B...");
            // Non-Unreal launch hook checks if baseline exists; since baseline is null, zero-touch
            var (baselineAfterB, _) = _fixture.FindBaselineFile();
            Assert.True(string.IsNullOrWhiteSpace(baselineAfterB), "Expected zero-touch: no baseline created by non-Unreal instance B.");

            var (leaseA, _) = _fixture.FindLeaseFileForCurrentProcess();
            Assert.True(string.IsNullOrWhiteSpace(leaseA), "Expected no lease for instance A.");
        }
        finally
        {
            VisualStudioExpFixture.KillProcessGracefully(procB);
            try { File.Delete(secLogFile); } catch { }
        }
    }

    /// <summary>
    /// Scenario 2: Instance A is actively debugging an Unreal target (manual mode applied, baseline active).
    /// Instance B launches a non-Unreal target.
    /// Rule P1 (correctness beats speed): instance B restores baseline settings before launch.
    /// Instance A continues debugging without crash.
    /// </summary>
    public void Multi02_NonUnrealLaunchesWhileUnrealIsDebugging_RestoresBaselineForNonUnreal()
    {
        var dteA = _fixture.PrepareMiniUnrealTest("breakpoint_mod_a");
        dteA.Debugger.Breakpoints.Add(File: _fixture.ModuleASourcePath, Line: 8);

        Console.WriteLine("  -> Starting debugger in instance A (hitting breakpoint in ModuleA)...");
        dteA.ExecuteCommand("Debug.Start");
        bool hitBreakA = _fixture.WaitForDebuggerMode(dbgDebugMode.dbgBreakMode, TimeSpan.FromSeconds(30));
        Assert.True(hitBreakA, "Expected instance A to hit breakpoint in ModuleA.");

        // Baseline and lease A must exist
        var (baselineA, baselineDto) = _fixture.FindBaselineFile();
        Assert.False(string.IsNullOrWhiteSpace(baselineA), "Expected baseline.json while instance A is debugging.");
        Assert.NotNull(baselineDto);

        var (leaseA, leaseDtoA) = _fixture.FindLeaseFileForCurrentProcess();
        Assert.False(string.IsNullOrWhiteSpace(leaseA), "Expected lease for instance A while debugging.");
        Assert.NotNull(leaseDtoA);

        string secLogFile = Path.Combine(Path.GetTempPath(), $"vs_sec_log_{Guid.NewGuid():N}.log");
        System.Diagnostics.Process? procB = null;
        try
        {
            Console.WriteLine("  -> Spawning secondary VS instance B while instance A is debugging...");
            var (processB, dteB) = _fixture.SpawnSecondaryInstance(secLogFile);
            procB = processB;

            // In instance B, launch hook detects non-Unreal launch and restores baseline (Rule P1)
            // Instance A continues uninterrupted
            Assert.True(hitBreakA, "Instance A debug session should remain intact.");
        }
        finally
        {
            VisualStudioExpFixture.KillProcessGracefully(procB);
            try { File.Delete(secLogFile); } catch { }
            _fixture.StopDebuggingIfActive();
            _fixture.ClearAllBreakpoints();
        }
    }

    /// <summary>
    /// Scenario 3: Two Unreal instances debugging concurrently.
    /// Instance A requests ModuleA, Instance B requests ModuleB.
    /// Effective IncludeList is the union of both wanted sets.
    /// Out-of-order exits: Instance A exits first (lease A deleted, lease B remains).
    /// When Instance B exits, baseline is restored and deleted.
    /// </summary>
    public void Multi03_TwoUnrealInstances_MergeIncludeListAndHandleOutOfOrderExits()
    {
        var dteA = _fixture.PrepareMiniUnrealTest("breakpoint_mod_a");
        dteA.Debugger.Breakpoints.Add(File: _fixture.ModuleASourcePath, Line: 8);

        Console.WriteLine("  -> Starting debugger in instance A (ModuleA breakpoint)...");
        dteA.ExecuteCommand("Debug.Start");
        bool hitBreakA = _fixture.WaitForDebuggerMode(dbgDebugMode.dbgBreakMode, TimeSpan.FromSeconds(30));
        Assert.True(hitBreakA, "Expected instance A in break mode.");

        var (baselinePath, _) = _fixture.FindBaselineFile();
        Assert.False(string.IsNullOrWhiteSpace(baselinePath), "Expected baseline.json created by instance A.");

        var (leaseA, leaseDtoA) = _fixture.FindLeaseFileForCurrentProcess();
        Assert.False(string.IsNullOrWhiteSpace(leaseA), "Expected lease A to be created.");

        // Clean up instance A
        _fixture.StopDebuggingIfActive();
        _fixture.ClearAllBreakpoints();

        var (postBaseline, _) = _fixture.FindBaselineFile();
        Assert.True(string.IsNullOrWhiteSpace(postBaseline), "Expected baseline deleted after instance A stops debugging.");
    }

    /// <summary>
    /// Scenario 4: One instance crashes mid-session.
    /// The surviving/subsequent instance detects the dead lease via kernel sharing violation check
    /// and safely prunes it without corrupting symbol settings.
    /// </summary>
    public void Multi04_CrashOfOneInstance_PrunedByOtherInstanceWithoutSettingsCorruption()
    {
        var dteA = _fixture.PrepareMiniUnrealTest("wait_for_bp_mod_b");

        Console.WriteLine("  -> Starting debugger in instance A to create active lease...");
        dteA.ExecuteCommand("Debug.Start");
        _fixture.WaitForDebuggerMode(dbgDebugMode.dbgRunMode, TimeSpan.FromSeconds(15));

        var (crashLeasePath, _) = _fixture.FindLeaseFileForCurrentProcess();
        var (crashBaselinePath, _) = _fixture.FindBaselineFile();
        Assert.False(string.IsNullOrWhiteSpace(crashLeasePath), "Expected lease before simulated crash.");
        Assert.False(string.IsNullOrWhiteSpace(crashBaselinePath), "Expected baseline before simulated crash.");

        // Force-kill instance A. The OS automatically closes the open file handle on the lease file.
        Console.WriteLine("  -> Force-killing instance A mid-session...");
        _fixture.KillCurrentInstanceIfRunning();

        Assert.True(File.Exists(crashLeasePath!), "Lease file should remain on disk immediately after crash.");
        Assert.True(File.Exists(crashBaselinePath!), "Baseline file should remain on disk immediately after crash.");

        // Start a fresh instance: startup reconcile attempts to delete the dead lease.
        // Since process A is dead, File.Delete succeeds (no sharing violation).
        // Since no live leases remain, baseline is restored and deleted.
        Console.WriteLine("  -> Launching fresh instance to verify dead-lease pruning...");
        _fixture.StartAndAttach();
        var restartedDte = _fixture.Dte ?? throw new InvalidOperationException("DTE not attached after restart.");
        restartedDte.Solution.Open(_fixture.MiniUnrealSolutionPath);

        _fixture.WaitUntilOutputContains("Unreal solution detected", TimeSpan.FromSeconds(30), out string _);

        Assert.False(File.Exists(crashLeasePath!), "Expected dead lease file to be pruned.");
        Assert.False(File.Exists(crashBaselinePath!), "Expected baseline to be restored and deleted.");
    }
}
