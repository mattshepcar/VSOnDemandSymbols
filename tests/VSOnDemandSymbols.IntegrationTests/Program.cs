using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;

namespace VSOnDemandSymbols.IntegrationTests;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string? filter = args.Length > 0 ? args[0] : null;
        Console.WriteLine("==================================================================");
        Console.WriteLine(" VSOnDemandSymbols — Automated Experimental Instance Test Runner");
        Console.WriteLine("==================================================================");

        OleMessageFilter.Register();
        var sw = Stopwatch.StartNew();
        int passed = 0;
        int failed = 0;

        try
        {
            using var fixture = new VisualStudioExpFixture();
            var suite = new EndToEndDebuggerTests(fixture);

            var testMethods = new List<(string Name, Action Run)>
            {
                (nameof(EndToEndDebuggerTests.Test01_SolutionOpen_DetectsUnrealSolutionAndCreatesBackup),
                    suite.Test01_SolutionOpen_DetectsUnrealSolutionAndCreatesBackup),
                (nameof(EndToEndDebuggerTests.Test02_PreLaunchBreakpoint_LoadsSymbolsOnlyForBreakpointModule),
                    suite.Test02_PreLaunchBreakpoint_LoadsSymbolsOnlyForBreakpointModule),
                (nameof(EndToEndDebuggerTests.Test03_CrashInUnsymbolizedModule_LoadsSymbolsAndSetsContextAndOpensSourceFile),
                    suite.Test03_CrashInUnsymbolizedModule_LoadsSymbolsAndSetsContextAndOpensSourceFile),
                (nameof(EndToEndDebuggerTests.Test04_AddBreakpointWhileRunning_LoadsSymbolsOnDemandAndBindsBreakpoint),
                    suite.Test04_AddBreakpointWhileRunning_LoadsSymbolsOnDemandAndBindsBreakpoint),
                (nameof(EndToEndDebuggerTests.Test05_SolutionCloseAndCrashRecovery_RestoresOriginalSymbolSettings),
                    suite.Test05_SolutionCloseAndCrashRecovery_RestoresOriginalSymbolSettings),
            };

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
                    var inner = ex is TargetInvocationException tie ? tie.InnerException ?? tie : ex;
                    Console.WriteLine($"[FAIL] {name} ({testSw.ElapsedMilliseconds} ms): {inner.Message}");
                    Console.WriteLine(inner.StackTrace);
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
}

