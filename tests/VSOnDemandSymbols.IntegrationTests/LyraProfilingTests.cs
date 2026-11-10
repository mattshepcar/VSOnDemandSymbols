using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using EnvDTE;
using Xunit;

namespace VSOnDemandSymbols.IntegrationTests;

/// <summary>
/// Full Unreal Engine 5 + Lyra Starter Game profiling and integration suite.
/// Runs headless (<c>-nullrhi -unattended</c>) via a zero-write harness in <c>%TEMP%</c>
/// and explicitly compares Cold Boot vs. Warm Boot (OS file cache &amp; symbol cache warm)
/// as well as In-Session Warm Break and Mid-Session Breakpoint Binding across ~900+ DLLs.
/// </summary>
public sealed class LyraProfilingTests
{
    private readonly VisualStudioExpFixture _fixture;
    private string? _harnessSolutionPath;

    // Cold boot metrics
    private long _coldLaunchToBreakMs;
    private int _coldTotalModulesMapped;
    private int _coldSymbolizedModulesAfterBreak;
    private ParsedProfileReport? _coldProfileReport;

    // In-session warm break & mid-session breakpoint bind metrics
    private long _inSessionWarmContinueToBreakMs;
    private ParsedProfileReport? _inSessionWarmBreakReport;
    private BreakpointBindProfileMetric? _midSessionBpBindMetric;
    private long _midSessionLyraGamePdbLoadMs;

    // Warm boot metrics
    private long _warmLaunchToBreakMs;
    private int _warmTotalModulesMapped;
    private int _warmSymbolizedModulesAfterBreak;
    private ParsedProfileReport? _warmProfileReport;

    // Baseline (Extension Disabled / Default VS Symbol Loading) metrics
    private long _baselineLaunchToBreakMs;
    private int _baselineTotalModulesMapped;
    private int _baselineSymbolizedModulesAfterBreak;

    public LyraProfilingTests(VisualStudioExpFixture fixture)
    {
        _fixture = fixture ?? throw new ArgumentNullException(nameof(fixture));
    }

    public void Lyra01_ColdBoot_StartupAndBreakModeProfile()
    {
        EnsureLyraAvailable();
        var ue = _fixture.UnrealConfig;

        _harnessSolutionPath = LyraHarnessGenerator.Generate(ue);
        Console.WriteLine($"  -> Generated zero-write Lyra harness: {_harnessSolutionPath}");
        Console.WriteLine($"  -> Target Editor Executable: {ue.PreferredEditorCmdExePath}");

        (_coldLaunchToBreakMs, _coldTotalModulesMapped, _coldSymbolizedModulesAfterBreak, _coldProfileReport) =
            RunLyraBootPass(
                label: "COLD BOOT",
                resetDirtyBackupFirst: true,
                addSecondWarmBreak: true);

        var lyraEditorMod = _fixture.FindDebuggedModule(ue.ExpectedLyraEditorDllName);
        var lyraGameMod = _fixture.FindDebuggedModule(ue.ExpectedLyraGameDllName);
        var unrealEdMod = _fixture.FindDebuggedModule("UnrealEditor-UnrealEd.dll");

        Assert.NotNull(lyraEditorMod);
        Assert.NotNull(lyraGameMod);
        Assert.NotNull(unrealEdMod);

        Assert.False(string.IsNullOrWhiteSpace(lyraEditorMod!.SymbolFile),
            $"Expected {ue.ExpectedLyraEditorDllName} (has startup breakpoint) to have symbols loaded at launch.");
        Assert.False(string.IsNullOrWhiteSpace(unrealEdMod!.SymbolFile),
            "Expected UnrealEditor-UnrealEd.dll (caller on stack) to have symbols loaded on demand during BreakMode.");
        Assert.True(string.IsNullOrWhiteSpace(lyraGameMod!.SymbolFile),
            $"Expected {ue.ExpectedLyraGameDllName} (no breakpoint, not on breaking stack) to NOT have symbols loaded yet.");
    }

    public void Lyra02_InSessionWarm_SecondBreakAndMidSessionBreakpointBind()
    {
        EnsureLyraAvailable();
        var dte = _fixture.Dte ?? throw new InvalidOperationException("DTE not attached.");
        var ue = _fixture.UnrealConfig;

        if (dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
        {
            Lyra01_ColdBoot_StartupAndBreakModeProfile();
            dte = _fixture.Dte ?? throw new InvalidOperationException("DTE not attached.");
        }

        Assert.Equal(dbgDebugMode.dbgBreakMode, dte.Debugger.CurrentMode);

        // 1. Test In-Session Warm Break: continue from ULyraEditorEngine::Init (line 27) to ULyraEditorEngine::Start (line 32)
        // where all call-stack modules already have symbols loaded in _loadedModulesThisSession.
        _fixture.ClearOnDemandSymbolsOutputPane();
        Console.WriteLine("  -> [IN-SESSION WARM BREAK] Continuing from ULyraEditorEngine::Init (line 27) to ULyraEditorEngine::Start (line 32)...");
        var continueSw = Stopwatch.StartNew();
        dte.Debugger.Go(WaitForBreakOrEnd: true);
        continueSw.Stop();
        _inSessionWarmContinueToBreakMs = continueSw.ElapsedMilliseconds;

        Assert.Equal(dbgDebugMode.dbgBreakMode, dte.Debugger.CurrentMode);
        System.Threading.Thread.Sleep(300);
        string warmBreakLog = _fixture.GetOnDemandSymbolsOutputText();
        _inSessionWarmBreakReport = ProfileMetricsParser.Parse(warmBreakLog);

        long warmBreakOnDemandMs = _inSessionWarmBreakReport.BreakModeEvents.Count > 0
            ? _inSessionWarmBreakReport.BreakModeEvents.Sum(e => e.TotalMs)
            : 0L;
        Console.WriteLine($"  -> [IN-SESSION WARM BREAK] Continue to Break(Start) : {_inSessionWarmContinueToBreakMs} ms (additional symbol loads: {_inSessionWarmBreakReport.ModuleLoads.Count}, on-demand overhead: {warmBreakOnDemandMs} ms)");
        if (_inSessionWarmBreakReport.BreakModeEvents.Count > 0)
        {
            var wbm = _inSessionWarmBreakReport.BreakModeEvents[0];
            Console.WriteLine(
                $"  -> [IN-SESSION WARM BREAK] BreakMode Profile        : total={wbm.TotalMs}ms " +
                $"(stackWalk={wbm.StackWalkMs}ms [threads={wbm.ThreadsInspected}, frames={wbm.FramesInspected}], " +
                $"moduleScan={wbm.ModuleScanMs}ms [modules={wbm.ModulesScanned}], " +
                $"symbolLoad={wbm.SymbolLoadMs}ms [loaded={wbm.ModulesLoaded}/{wbm.CandidateTargetModules}], " +
                $"contextRefresh={wbm.ContextRefreshMs}ms)");
        }

        // 2. Test Mid-Session Breakpoint Binding on an unsymbolized 115 MB PDB module (UnrealEditor-LyraGame*.dll)
        _fixture.ClearOnDemandSymbolsOutputPane();
        var lyraGameBefore = _fixture.FindDebuggedModule(ue.ExpectedLyraGameDllName);
        Assert.NotNull(lyraGameBefore);
        Assert.True(string.IsNullOrWhiteSpace(lyraGameBefore!.SymbolFile),
            $"Expected {ue.ExpectedLyraGameDllName} to still be unsymbolized before mid-session breakpoint.");

        Console.WriteLine($"  -> [MID-SESSION BIND] Adding breakpoint in LyraGameplayTags.cpp (line 77) to trigger on-demand load of {ue.ExpectedLyraGameDllName}...");
        dte.Debugger.Breakpoints.Add(File: ue.LyraGameplayTagsSourcePath, Line: 77);

        bool boundLogged = _fixture.WaitUntilOutputContains(
            "[Profile] BreakpointBind:",
            TimeSpan.FromSeconds(30),
            out string bpBindLog);
        Assert.True(boundLogged, $"Expected [Profile] BreakpointBind telemetry after adding LyraGameplayTags.cpp breakpoint, but got:\n{bpBindLog}");

        var bpReport = ProfileMetricsParser.Parse(bpBindLog);
        Assert.NotEmpty(bpReport.BreakpointBindEvents);
        _midSessionBpBindMetric = bpReport.BreakpointBindEvents[0];
        _midSessionLyraGamePdbLoadMs = bpReport.ModuleLoads.FirstOrDefault()?.ElapsedMs ?? _midSessionBpBindMetric.SymbolLoadMs;

        var lyraGameAfter = _fixture.FindDebuggedModule(ue.ExpectedLyraGameDllName);
        Assert.NotNull(lyraGameAfter);
        Assert.False(string.IsNullOrWhiteSpace(lyraGameAfter!.SymbolFile),
            $"Expected {ue.ExpectedLyraGameDllName} symbols to be loaded on demand after adding mid-session breakpoint.");

        Console.WriteLine(
            $"  -> [MID-SESSION BIND] Profile: total={_midSessionBpBindMetric.TotalMs}ms " +
            $"(includeList={_midSessionBpBindMetric.IncludeListMs}ms, " +
            $"moduleScan={_midSessionBpBindMetric.ModuleScanMs}ms [modules={_midSessionBpBindMetric.ModulesScanned}], " +
            $"symbolLoad={_midSessionBpBindMetric.SymbolLoadMs}ms [loaded={_midSessionBpBindMetric.ModulesLoaded}])");

        _fixture.StopDebuggingIfActive();
        _fixture.ClearAllBreakpoints();
        dte.Solution.Close(SaveFirst: false);
    }

    public void Lyra03_WarmBoot_CachedRelaunchAndComparisonReport()
    {
        EnsureLyraAvailable();
        var dte = _fixture.Dte ?? throw new InvalidOperationException("DTE not attached.");

        (_warmLaunchToBreakMs, _warmTotalModulesMapped, _warmSymbolizedModulesAfterBreak, _warmProfileReport) =
            RunLyraBootPass(
                label: "WARM BOOT",
                resetDirtyBackupFirst: false,
                addSecondWarmBreak: false);

        _fixture.StopDebuggingIfActive();
        _fixture.ClearAllBreakpoints();
        dte.Solution.Close(SaveFirst: false);
    }

    private (long LaunchToBreakMs, int TotalModules, int SymbolizedModules, ParsedProfileReport ProfileReport) RunLyraBootPass(
        string label,
        bool resetDirtyBackupFirst,
        bool addSecondWarmBreak)
    {
        var dte = _fixture.Dte ?? throw new InvalidOperationException("DTE not attached.");
        var ue = _fixture.UnrealConfig;

        _fixture.StopDebuggingIfActive();
        _fixture.ClearAllBreakpoints();

        if (string.IsNullOrWhiteSpace(_harnessSolutionPath) || !File.Exists(_harnessSolutionPath))
        {
            _harnessSolutionPath = LyraHarnessGenerator.Generate(ue);
        }

        _fixture.ClearOnDemandSymbolsOutputPane();
        dte.Solution.Open(_harnessSolutionPath);

        bool detected = _fixture.WaitUntilOutputContains(
            "Unreal solution detected: 'LyraHarness.sln'. On-Demand Symbols active.",
            TimeSpan.FromSeconds(30),
            out string openLog);
        Assert.True(detected, $"Expected LyraHarness.sln to be detected for {label}, but got:\n{openLog}");

        if (resetDirtyBackupFirst)
        {
            _fixture.ResetDirtyExpBackupSettingsIfNeeded(_harnessSolutionPath!, "LyraHarness.sln");
            dte = _fixture.Dte ?? throw new InvalidOperationException("DTE not attached.");
        }

        _fixture.ClearOnDemandSymbolsOutputPane();

        // Startup breakpoint in ULyraEditorEngine::Init (line 27)
        dte.Debugger.Breakpoints.Add(File: ue.LyraEditorEngineSourcePath, Line: 27);
        if (addSecondWarmBreak)
        {
            // Second breakpoint in ULyraEditorEngine::Start (line 32) for in-session warm break test
            dte.Debugger.Breakpoints.Add(File: ue.LyraEditorEngineSourcePath, Line: 32);
        }

        Console.WriteLine($"  -> [{label}] Launching UnrealEditor with Lyra.uproject (-nullrhi -unattended)...");
        var launchSw = Stopwatch.StartNew();
        dte.ExecuteCommand("Debug.Start");

        bool hitBreak = _fixture.WaitForDebuggerMode(dbgDebugMode.dbgBreakMode, TimeSpan.FromSeconds(180));
        launchSw.Stop();
        long launchToBreakMs = launchSw.ElapsedMilliseconds;
        Assert.True(hitBreak, $"Expected {label} to hit breakpoint in ULyraEditorEngine::Init within 180s (elapsed: {launchToBreakMs} ms).\nLog:\n{_fixture.GetOnDemandSymbolsOutputText()}");

        bool breakProfileLogged = _fixture.WaitUntilOutputContains(
            "[Profile] BreakMode:",
            TimeSpan.FromSeconds(30),
            out string breakLog);
        Assert.True(breakProfileLogged, $"Expected [Profile] BreakMode telemetry on {label} break, but got:\n{breakLog}");

        var report = ProfileMetricsParser.Parse(breakLog);
        Assert.NotEmpty(report.BreakModeEvents);

        var (totalMods, symbolizedMods, _) = _fixture.GetDebuggedModulesSummary();
        var bm = report.BreakModeEvents[0];

        Console.WriteLine($"  -> [{label}] Launch to Breakpoint Hit : {launchToBreakMs} ms");
        Console.WriteLine($"  -> [{label}] Modules Mapped           : {totalMods} total, {symbolizedMods} symbolized ({totalMods - symbolizedMods} skipped)");
        Console.WriteLine(
            $"  -> [{label}] BreakMode Profile        : total={bm.TotalMs}ms " +
            $"(stackWalk={bm.StackWalkMs}ms [threads={bm.ThreadsInspected}, frames={bm.FramesInspected}], " +
            $"moduleScan={bm.ModuleScanMs}ms [modules={bm.ModulesScanned}], " +
            $"symbolLoad={bm.SymbolLoadMs}ms [loaded={bm.ModulesLoaded}/{bm.CandidateTargetModules}], " +
            $"contextRefresh={bm.ContextRefreshMs}ms)");

        return (launchToBreakMs, totalMods, symbolizedMods, report);
    }

    public void Lyra04_BaselineDisabled_FullSymbolLoadAndSavingsReport()
    {
        EnsureLyraAvailable();
        var dte = _fixture.Dte ?? throw new InvalidOperationException("DTE not attached.");
        var ue = _fixture.UnrealConfig;

        _fixture.StopDebuggingIfActive();
        _fixture.ClearAllBreakpoints();

        // Generate a harness WITHOUT a .uproject marker so On-Demand Symbols stays completely inactive
        // and Visual Studio uses its default symbol loading (loading all adjacent PDBs across ~1,062 modules).
        string baselineSolutionPath = LyraHarnessGenerator.Generate(
            ue,
            includeUprojectMarker: false,
            folderSuffix: "BaselineDisabled");

        _fixture.ClearOnDemandSymbolsOutputPane();
        dte.Solution.Open(baselineSolutionPath);
        System.Threading.Thread.Sleep(500);

        // Set the exact same startup breakpoint in ULyraEditorEngine::Init (line 27)
        dte.Debugger.Breakpoints.Add(File: ue.LyraEditorEngineSourcePath, Line: 27);

        Console.WriteLine("  -> [BASELINE — EXTENSION DISABLED] Launching UnrealEditor with default VS symbol loading (all adjacent PDBs)...");
        var launchSw = Stopwatch.StartNew();
        dte.ExecuteCommand("Debug.Start");

        bool hitBreak = _fixture.WaitForDebuggerMode(dbgDebugMode.dbgBreakMode, TimeSpan.FromSeconds(300));
        launchSw.Stop();
        _baselineLaunchToBreakMs = launchSw.ElapsedMilliseconds;
        Assert.True(hitBreak, $"Expected Baseline (extension disabled) to hit breakpoint in ULyraEditorEngine::Init within 300s (elapsed: {_baselineLaunchToBreakMs} ms).");

        var (totalMods, symbolizedMods, _) = _fixture.GetDebuggedModulesSummary();
        _baselineTotalModulesMapped = totalMods;
        _baselineSymbolizedModulesAfterBreak = symbolizedMods;

        Console.WriteLine($"  -> [BASELINE — EXTENSION DISABLED] Launch to Breakpoint Hit : {_baselineLaunchToBreakMs} ms");
        Console.WriteLine($"  -> [BASELINE — EXTENSION DISABLED] Modules Mapped           : {_baselineTotalModulesMapped} total, {_baselineSymbolizedModulesAfterBreak} symbolized ({_baselineTotalModulesMapped - _baselineSymbolizedModulesAfterBreak} without PDB)");

        _fixture.StopDebuggingIfActive();
        _fixture.ClearAllBreakpoints();
        dte.Solution.Close(SaveFirst: false);

        PrintComparisonReport();
    }

    private void PrintComparisonReport()
    {
        var coldBm = _coldProfileReport?.BreakModeEvents.FirstOrDefault();
        var warmBm = _warmProfileReport?.BreakModeEvents.FirstOrDefault();
        var coldInc = _coldProfileReport?.IncludeListUpdates.FirstOrDefault();
        var warmInc = _warmProfileReport?.IncludeListUpdates.FirstOrDefault();

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("================================================================================");
        sb.AppendLine(" LYRA STARTER GAME — COLD BOOT vs. WARM BOOT PROFILING REPORT");
        sb.AppendLine("================================================================================");
        sb.AppendLine($" Unreal Engine Root : {_fixture.UnrealConfig.UnrealEngineRoot} ({_fixture.UnrealConfig.ConfigurationSource})");
        sb.AppendLine($" Target Binary      : {Path.GetFileName(_fixture.UnrealConfig.PreferredEditorCmdExePath)}");
        sb.AppendLine($" Mapped Modules     : {_warmTotalModulesMapped} DLLs/EXE ({_warmSymbolizedModulesAfterBreak} symbolized, {_warmTotalModulesMapped - _warmSymbolizedModulesAfterBreak} PDBs skipped)");
        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine(string.Format(" {0,-38} | {1,12} | {2,12} | {3,10}", "Metric", "Cold Boot", "Warm Boot", "Delta"));
        sb.AppendLine("--------------------------------------------------------------------------------");
        AppendRow(sb, "F5 Launch -> Breakpoint Hit", _coldLaunchToBreakMs, _warmLaunchToBreakMs);
        if (coldInc != null && warmInc != null)
        {
            AppendRow(sb, "BeforeLaunch UpdateIncludeList", coldInc.TotalMs, warmInc.TotalMs);
        }

        if (coldBm != null && warmBm != null)
        {
            AppendRow(sb, "BreakMode On-Demand Total", coldBm.TotalMs, warmBm.TotalMs);
            AppendRow(sb, $"  - Stack Walk ({warmBm.ThreadsInspected} threads, {warmBm.FramesInspected} frames)", coldBm.StackWalkMs, warmBm.StackWalkMs);
            AppendRow(sb, $"  - Module Scan ({warmBm.ModulesScanned} modules)", coldBm.ModuleScanMs, warmBm.ModuleScanMs);
            AppendRow(sb, $"  - PDB Symbol Load ({warmBm.ModulesLoaded} modules)", coldBm.SymbolLoadMs, warmBm.SymbolLoadMs);
            AppendRow(sb, "  - Editor Context Refresh", coldBm.ContextRefreshMs, warmBm.ContextRefreshMs);
        }

        if (_coldProfileReport != null && _warmProfileReport != null)
        {
            var warmLoadsByMod = _warmProfileReport.ModuleLoads
                .GroupBy(m => m.ModuleFileName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().ElapsedMs, StringComparer.OrdinalIgnoreCase);

            if (_coldProfileReport.ModuleLoads.Count > 0)
            {
                sb.AppendLine("--------------------------------------------------------------------------------");
                sb.AppendLine(" Per-Module On-Demand PDB Load Breakdown:");
                foreach (var coldMod in _coldProfileReport.ModuleLoads)
                {
                    if (warmLoadsByMod.TryGetValue(coldMod.ModuleFileName, out long warmMs))
                    {
                        AppendRow(sb, $"  PDB: {coldMod.ModuleFileName}", coldMod.ElapsedMs, warmMs);
                    }
                    else
                    {
                        sb.AppendLine(string.Format(" {0,-38} | {1,9} ms | {2,12} | {3,10}", $"  PDB: {coldMod.ModuleFileName}", coldMod.ElapsedMs, "n/a", "n/a"));
                    }
                }
            }
        }

        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine(" Additional In-Session Operations (Warm Session):");
        long warmSecondBreakOnDemandMs = _inSessionWarmBreakReport?.BreakModeEvents.Sum(e => e.TotalMs) ?? 0L;
        sb.AppendLine($"  - In-Session 2nd Break (Already Cached) : {_inSessionWarmContinueToBreakMs} ms wall, {warmSecondBreakOnDemandMs} ms on-demand load (0 new user PDBs)");
        if (_midSessionBpBindMetric != null)
        {
            sb.AppendLine(
                $"  - Mid-Session Breakpoint Bind (LyraGame): {_midSessionBpBindMetric.TotalMs} ms total " +
                $"(scan {_midSessionBpBindMetric.ModulesScanned} modules={_midSessionBpBindMetric.ModuleScanMs} ms, " +
                $"PDB load={_midSessionLyraGamePdbLoadMs} ms)");
        }

        if (_baselineLaunchToBreakMs > 0)
        {
            long savedColdMs = _baselineLaunchToBreakMs - _coldLaunchToBreakMs;
            long savedWarmMs = _baselineLaunchToBreakMs - _warmLaunchToBreakMs;
            double speedupCold = _coldLaunchToBreakMs > 0 ? (double)_baselineLaunchToBreakMs / _coldLaunchToBreakMs : 0.0;
            double speedupWarm = _warmLaunchToBreakMs > 0 ? (double)_baselineLaunchToBreakMs / _warmLaunchToBreakMs : 0.0;
            double pctSavedWarm = (double)savedWarmMs / _baselineLaunchToBreakMs * 100.0;

            sb.AppendLine("--------------------------------------------------------------------------------");
            sb.AppendLine(" EXTENSION ENABLED vs. EXTENSION DISABLED (DEFAULT VS SYMBOL LOADING):");
            sb.AppendLine($"  - Extension Disabled (Default VS)       : {_baselineLaunchToBreakMs,6} ms ({_baselineSymbolizedModulesAfterBreak}/{_baselineTotalModulesMapped} PDBs loaded at boot)");
            if (_coldLaunchToBreakMs > 0)
            {
                double pctSavedCold = (double)savedColdMs / _baselineLaunchToBreakMs * 100.0;
                sb.AppendLine($"  - Extension Enabled  (Cold Boot)        : {_coldLaunchToBreakMs,6} ms ({_coldSymbolizedModulesAfterBreak}/{_coldTotalModulesMapped} PDBs loaded) -> Saved {savedColdMs} ms ({pctSavedCold:0.0}% reduction, {speedupCold:0.00}x faster)");
            }
            if (_warmLaunchToBreakMs > 0)
            {
                sb.AppendLine($"  - Extension Enabled  (Warm Boot)        : {_warmLaunchToBreakMs,6} ms ({_warmSymbolizedModulesAfterBreak}/{_warmTotalModulesMapped} PDBs loaded) -> Saved {savedWarmMs} ms ({pctSavedWarm:0.0}% reduction, {speedupWarm:0.00}x faster)");
            }
        }
        sb.AppendLine("================================================================================");

        Console.WriteLine(sb.ToString());
    }

    private static void AppendRow(StringBuilder sb, string label, long coldMs, long warmMs)
    {
        long diff = warmMs - coldMs;
        string deltaStr = coldMs > 0
            ? $"{diff:+#;-#;0} ms ({(double)diff / coldMs * 100.0:+0.0;-0.0;0.0}%)"
            : $"{diff:+#;-#;0} ms";
        sb.AppendLine(string.Format(" {0,-38} | {1,9} ms | {2,9} ms | {3,10}",
            label.Length > 38 ? label.Substring(0, 38) : label,
            coldMs,
            warmMs,
            deltaStr));
    }

    private void EnsureLyraAvailable()
    {
        if (!_fixture.UnrealConfig.IsLyraAvailable)
        {
            throw new InvalidOperationException(
                $"Cannot run Lyra profiling suite: {_fixture.UnrealConfig.UnavailableReason}");
        }
    }
}

