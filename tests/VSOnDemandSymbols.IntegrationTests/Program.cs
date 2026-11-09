using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace VSOnDemandSymbols.IntegrationTests;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        ParseArgs(args, out string? filter, out string? ueRootOverride, out bool includeLyra, out bool lyraOnly);
        Console.WriteLine("==================================================================");
        Console.WriteLine(" VSOnDemandSymbols — Automated Experimental Instance Test Runner");
        Console.WriteLine("==================================================================");

        OleMessageFilter.Register();
        var sw = Stopwatch.StartNew();
        int passed = 0;
        int failed = 0;

        try
        {
            using var fixture = new VisualStudioExpFixture(ueRootOverride);
            var ueConfig = fixture.UnrealConfig;
            Console.WriteLine($"[Config] UnrealEngineRoot : {ueConfig.UnrealEngineRoot} (via {ueConfig.ConfigurationSource})");
            Console.WriteLine($"[Config] Lyra Available   : {(ueConfig.IsLyraAvailable ? $"Yes ({ueConfig.PreferredEditorCmdExePath})" : $"No ({ueConfig.UnavailableReason})")}");

            var testMethods = new List<(string Name, Action Run)>();

            if (!lyraOnly)
            {
                var suite = new EndToEndDebuggerTests(fixture);
                testMethods.Add((nameof(EndToEndDebuggerTests.Test01_SolutionOpen_DetectsUnrealSolutionAndCreatesBackup),
                    suite.Test01_SolutionOpen_DetectsUnrealSolutionAndCreatesBackup));
                testMethods.Add((nameof(EndToEndDebuggerTests.Test02_PreLaunchBreakpoint_LoadsSymbolsOnlyForBreakpointModule),
                    suite.Test02_PreLaunchBreakpoint_LoadsSymbolsOnlyForBreakpointModule));
                testMethods.Add((nameof(EndToEndDebuggerTests.Test03_CrashInUnsymbolizedModule_LoadsSymbolsAndSetsContextAndOpensSourceFile),
                    suite.Test03_CrashInUnsymbolizedModule_LoadsSymbolsAndSetsContextAndOpensSourceFile));
                testMethods.Add((nameof(EndToEndDebuggerTests.Test04_AddBreakpointWhileRunning_LoadsSymbolsOnDemandAndBindsBreakpoint),
                    suite.Test04_AddBreakpointWhileRunning_LoadsSymbolsOnDemandAndBindsBreakpoint));
                testMethods.Add((nameof(EndToEndDebuggerTests.Test05_SolutionCloseAndCrashRecovery_RestoresOriginalSymbolSettings),
                    suite.Test05_SolutionCloseAndCrashRecovery_RestoresOriginalSymbolSettings));
            }

            if (includeLyra || lyraOnly ||
                (!string.IsNullOrWhiteSpace(filter) && filter!.IndexOf("Lyra", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                var lyraSuite = new LyraProfilingTests(fixture);
                testMethods.Add((nameof(LyraProfilingTests.Lyra01_ColdBoot_StartupAndBreakModeProfile),
                    lyraSuite.Lyra01_ColdBoot_StartupAndBreakModeProfile));
                testMethods.Add((nameof(LyraProfilingTests.Lyra02_InSessionWarm_SecondBreakAndMidSessionBreakpointBind),
                    lyraSuite.Lyra02_InSessionWarm_SecondBreakAndMidSessionBreakpointBind));
                testMethods.Add((nameof(LyraProfilingTests.Lyra03_WarmBoot_CachedRelaunchAndComparisonReport),
                    lyraSuite.Lyra03_WarmBoot_CachedRelaunchAndComparisonReport));
                testMethods.Add((nameof(LyraProfilingTests.Lyra04_BaselineDisabled_FullSymbolLoadAndSavingsReport),
                    lyraSuite.Lyra04_BaselineDisabled_FullSymbolLoadAndSavingsReport));
            }

            foreach (var (name, run) in testMethods)
            {
                if (!string.IsNullOrWhiteSpace(filter) &&
                    name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                Console.WriteLine($"\n[RUN ] {name}");
                var testSw = Stopwatch.StartNew();
                try
                {
                    run();
                    testSw.Stop();
                    Console.WriteLine($"[PASS] {name} ({testSw.ElapsedMilliseconds} ms)");
                    passed++;
                }
                catch (Exception ex)
                {
                    testSw.Stop();
                    Console.WriteLine($"[FAIL] {name} ({testSw.ElapsedMilliseconds} ms): {ex.Message}");
                    Console.WriteLine(ex.StackTrace);
                    failed++;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FATAL] Fixture setup failed: {ex}");
            failed++;
        }
        finally
        {
            OleMessageFilter.Revoke();
        }

        sw.Stop();
        Console.WriteLine("\n==================================================================");
        Console.WriteLine($" Summary: {passed} passed, {failed} failed ({sw.ElapsedMilliseconds} ms total)");
        Console.WriteLine("==================================================================");
        return failed == 0 ? 0 : 1;
    }

    private static void ParseArgs(
        string[] args,
        out string? filter,
        out string? ueRootOverride,
        out bool includeLyra,
        out bool lyraOnly)
    {
        filter = null;
        ueRootOverride = null;
        includeLyra = false;
        lyraOnly = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, "--ue-root", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                ueRootOverride = args[++i];
            }
            else if (arg.StartsWith("--ue-root=", StringComparison.OrdinalIgnoreCase))
            {
                ueRootOverride = arg.Substring("--ue-root=".Length);
            }
            else if (string.Equals(arg, "--profile-lyra", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(arg, "--lyra", StringComparison.OrdinalIgnoreCase))
            {
                lyraOnly = true;
            }
            else if (string.Equals(arg, "--all", StringComparison.OrdinalIgnoreCase))
            {
                includeLyra = true;
            }
            else if (!arg.StartsWith("-", StringComparison.Ordinal))
            {
                filter = arg;
            }
        }
    }
}
