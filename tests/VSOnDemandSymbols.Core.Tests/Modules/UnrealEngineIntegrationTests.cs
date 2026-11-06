using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VSOnDemandSymbols.Core.Abstractions;
using VSOnDemandSymbols.Core.Modules;
using VSOnDemandSymbols.Core.Policy;
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

    [Fact]
    [Trait("Category", "Unreal")]
    public void LyraDebugGameEditor_SolutionDetectionAndModuleResolution_RoundTrips()
    {
        string? ueRoot = Environment.GetEnvironmentVariable("ODS_UE_ROOT");
        if (string.IsNullOrWhiteSpace(ueRoot) || !Directory.Exists(ueRoot))
        {
            return;
        }

        string lyraRoot = Path.Combine(ueRoot, "Samples", "Games", "Lyra");
        string lyraSlnx = Path.Combine(lyraRoot, "Lyra.slnx");
        if (!File.Exists(lyraSlnx))
        {
            Console.WriteLine($"[SKIP] Lyra.slnx not found at '{lyraSlnx}'.");
            return;
        }

        var fs = PhysicalFileSystem.Instance;
        var detector = new UnrealSolutionDetector(fs);

        // 1. Verify Lyra.slnx detection
        var solInfo = detector.Detect(
            lyraSlnx,
            new[]
            {
                Path.Combine(lyraRoot, "Intermediate", "ProjectFiles", "Lyra.vcxproj"),
                Path.Combine(lyraRoot, "Intermediate", "ProjectFiles", "UE5.vcxproj")
            });

        Assert.True(solInfo.IsUnreal);
        Assert.Equal(lyraRoot, solInfo.ProjectRoot, ignoreCase: true);
        Assert.Equal(ueRoot, solInfo.EngineRoot, ignoreCase: true);
        Assert.Equal("Lyra", solInfo.ProjectName);

        // 2. Verify DebugGame Editor executable qualification and AlwaysLoad match
        string debugGameEditorExe = Path.Combine(ueRoot, "Engine", "Binaries", "Win64", "UnrealEditor-Win64-DebugGame.exe");
        Assert.True(UnrealSolutionDetector.IsQualifyingUnrealProcess(debugGameEditorExe, null, solInfo.ProjectName));

        var policy = new LoadPolicy(WildcardPattern.ParseMultiple("UnrealEditor.exe;UnrealEditor-Win64-*.exe"));
        Assert.True(policy.IsAlwaysLoad("UnrealEditor-Win64-DebugGame.exe"));

        // 3. Verify resolver forward + reverse mapping on all Lyra game & plugin DLLs (including -Win64-DebugGame.dll)
        var resolver = new UnrealResolver(
            fs,
            NullLogger.Instance,
            engineRoot: solInfo.EngineRoot,
            projectRoot: solInfo.ProjectRoot);

        var lyraDlls = new List<string>();
        string lyraBinaries = Path.Combine(lyraRoot, "Binaries", "Win64");
        if (Directory.Exists(lyraBinaries))
        {
            lyraDlls.AddRange(Directory.EnumerateFiles(lyraBinaries, "UnrealEditor-*.dll", SearchOption.TopDirectoryOnly));
        }

        string lyraPlugins = Path.Combine(lyraRoot, "Plugins");
        if (Directory.Exists(lyraPlugins))
        {
            lyraDlls.AddRange(Directory.EnumerateFiles(lyraPlugins, "UnrealEditor-*.dll", SearchOption.AllDirectories));
        }

        Assert.NotEmpty(lyraDlls);

        foreach (string dllPath in lyraDlls)
        {
            string dllFileName = Path.GetFileName(dllPath);
            ModuleKey? key = resolver.TryParseLoadedModule(dllPath);
            Assert.True(key.HasValue, $"Failed to reverse-map Lyra DLL '{dllFileName}'");

            var candidates = resolver.GetCandidateFileNames(key!.Value);
            Assert.Contains(dllFileName, candidates, StringComparer.OrdinalIgnoreCase);
        }

        // 4. Verify source resolution across all Lyra Source & Plugin .cpp files
        var sourceCppFiles = new List<string>();
        sourceCppFiles.AddRange(Directory.EnumerateFiles(Path.Combine(lyraRoot, "Source"), "*.cpp", SearchOption.AllDirectories));
        if (Directory.Exists(lyraPlugins))
        {
            foreach (string pluginSourceDir in Directory.EnumerateDirectories(lyraPlugins, "Source", SearchOption.AllDirectories))
            {
                sourceCppFiles.AddRange(Directory.EnumerateFiles(pluginSourceDir, "*.cpp", SearchOption.AllDirectories));
            }
        }

        Assert.NotEmpty(sourceCppFiles);
        foreach (string cppFile in sourceCppFiles)
        {
            var resolvedKeys = resolver.Resolve(cppFile);
            Assert.True(resolvedKeys.Count > 0, $"Failed to resolve Lyra source file '{cppFile}' to an Unreal module.");
        }

        Console.WriteLine($"Successfully verified Lyra.slnx: {lyraDlls.Count} DLLs and {sourceCppFiles.Count} .cpp files resolved cleanly.");
    }
}

