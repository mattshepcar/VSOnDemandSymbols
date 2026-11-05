using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VSOnDemandSymbols.Core.Modules;
using Xunit;

namespace VSOnDemandSymbols.Core.Tests.Modules;

public class UnrealEngineIntegrationTests
{
    [Fact]
    [Trait("Category", "Unreal")]
    public void ReverseMap_EveryRealEngineDll_MapsToKnownBuildCs()
    {
        string? ueRoot = Environment.GetEnvironmentVariable("ODS_UE_ROOT");
        if (string.IsNullOrWhiteSpace(ueRoot) || !Directory.Exists(ueRoot))
        {
            Console.WriteLine("[SKIP] UnrealEngineIntegrationTests: ODS_UE_ROOT is not set or directory does not exist.");
            return;
        }

        string engineBinaries = Path.Combine(ueRoot, "Engine", "Binaries", "Win64");
        string enginePlugins = Path.Combine(ueRoot, "Engine", "Plugins");

        var dllFiles = new List<string>();
        if (Directory.Exists(engineBinaries))
        {
            dllFiles.AddRange(Directory.EnumerateFiles(engineBinaries, "UnrealEditor-*.dll", SearchOption.TopDirectoryOnly));
        }
        if (Directory.Exists(enginePlugins))
        {
            dllFiles.AddRange(Directory.EnumerateFiles(enginePlugins, "UnrealEditor-*.dll", SearchOption.AllDirectories));
        }

        if (dllFiles.Count == 0)
        {
            Console.WriteLine($"[SKIP] No UnrealEditor-*.dll files found under {ueRoot}. Engine may not be built.");
            return;
        }

        // Build index of all *.Build.cs files in the engine
        var knownBuildCsModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var buildCs in Directory.EnumerateFiles(Path.Combine(ueRoot, "Engine"), "*.Build.cs", SearchOption.AllDirectories))
        {
            string fileName = Path.GetFileName(buildCs);
            string modName = fileName.Substring(0, fileName.Length - ".Build.cs".Length);
            knownBuildCsModules.Add(modName);
        }

        var parser = new UnrealDllNameParser();
        var unparsedDlls = new List<string>();
        var missingBuildCs = new List<string>();

        foreach (var dllPath in dllFiles)
        {
            string fileName = Path.GetFileName(dllPath);
            if (!parser.TryParse(dllPath, out var info))
            {
                unparsedDlls.Add(fileName);
                continue;
            }

            if (!knownBuildCsModules.Contains(info.ModuleName))
            {
                missingBuildCs.Add($"{fileName} -> {info.ModuleName}");
            }
        }

        Assert.True(unparsedDlls.Count == 0, $"Failed to parse {unparsedDlls.Count} DLLs: {string.Join(", ", unparsedDlls.Take(10))}");
        Assert.True(missingBuildCs.Count == 0, $"Found {missingBuildCs.Count} DLLs with no matching .Build.cs: {string.Join(", ", missingBuildCs.Take(10))}");

        Console.WriteLine($"Successfully verified 100% of {dllFiles.Count} UnrealEditor-*.dll files mapped to valid .Build.cs modules.");
    }
}

