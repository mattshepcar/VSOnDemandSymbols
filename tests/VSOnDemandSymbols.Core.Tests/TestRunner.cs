using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Xunit;

namespace VSOnDemandSymbols.Core.Tests;

public static class Program
{
    public static int Main(string[] args)
    {
        Console.WriteLine("Running tests via in-process runner...");
        var sw = Stopwatch.StartNew();
        int passed = 0;
        int failed = 0;
        int total = 0;

        var asm = typeof(Program).Assembly;
        var testClasses = asm.GetTypes().Where(t => t.IsClass && !t.IsAbstract).OrderBy(t => t.FullName).ToList();

        foreach (var cls in testClasses)
        {
            var methods = cls.GetMethods(BindingFlags.Public | BindingFlags.Instance);
            foreach (var m in methods)
            {
                var fact = m.GetCustomAttribute<FactAttribute>();
                var theory = m.GetCustomAttribute<TheoryAttribute>();

                if (fact != null && theory == null)
                {
                    total++;
                    if (RunTestMethod(cls, m, null))
                    {
                        passed++;
                    }
                    else
                    {
                        failed++;
                    }
                }
                else if (theory != null)
                {
                    var inlineDataAttrs = m.GetCustomAttributes<InlineDataAttribute>().ToList();
                    if (inlineDataAttrs.Count > 0)
                    {
                        foreach (var data in inlineDataAttrs)
                        {
                            total++;
                            if (RunTestMethod(cls, m, data.GetData(m).First()))
                            {
                                passed++;
                            }
                            else
                            {
                                failed++;
                            }
                        }
                    }
                }
            }
        }

        sw.Stop();
        Console.WriteLine($"\nTest results: {passed} passed, {failed} failed, {total} total in {sw.ElapsedMilliseconds} ms.");
        return failed == 0 ? 0 : 1;
    }

    private static bool RunTestMethod(Type cls, MethodInfo m, object?[]? parameters)
    {
        string paramStr = parameters != null ? $"({string.Join(", ", parameters.Select(p => p?.ToString() ?? "null"))})" : "";
        string testName = $"{cls.Name}.{m.Name}{paramStr}";

        object? instance = null;
        try
        {
            instance = Activator.CreateInstance(cls);
            var result = m.Invoke(instance, parameters);
            if (result is System.Threading.Tasks.Task t)
            {
                t.GetAwaiter().GetResult();
            }
            Console.WriteLine($"[PASS] {testName}");
            return true;
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie ? tie.InnerException ?? tie : ex;
            Console.WriteLine($"[FAIL] {testName}: {inner.Message}");
            Console.WriteLine(inner.StackTrace);
            return false;
        }
        finally
        {
            (instance as IDisposable)?.Dispose();
        }
    }
}
