using System;
using System.IO;
using VSOnDemandSymbols.Core.Abstractions;
using VSOnDemandSymbols.Core.Modules;
using VSOnDemandSymbols.Core.Policy;
using Xunit;

namespace VSOnDemandSymbols.Core.Tests.Modules;

public class MsBuildResolverTests
{
    private static string LocateNativeSampleRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            string candidate = Path.Combine(dir, "tests", "Samples", "NativeSample");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException("Could not locate tests/Samples/NativeSample directory.");
    }

    [Fact]
    public void Resolve_NativeSample_DirectDllAndExeFiles_ResolveToRespectiveBinaries()
    {
        string sampleRoot = LocateNativeSampleRoot();
        string[] vcxprojs =
        {
            Path.Combine(sampleRoot, "StaticMath", "StaticMath.vcxproj"),
            Path.Combine(sampleRoot, "CorePlugin", "CorePlugin.vcxproj"),
            Path.Combine(sampleRoot, "SampleApp", "SampleApp.vcxproj")
        };

        var graph = MsBuildProjectGraph.FromProjectFiles(vcxprojPaths: vcxprojs, PhysicalFileSystem.Instance, "Debug", "x64");
        var resolver = new MsBuildResolver(graph);

        var pluginKeys = resolver.Resolve(Path.Combine(sampleRoot, "CorePlugin", "CorePlugin.cpp"));
        Assert.Single(pluginKeys);
        Assert.Equal(new ModuleKey(ModuleKind.MsBuild, "CorePlugin.dll"), pluginKeys[0]);

        var appKeys = resolver.Resolve(Path.Combine(sampleRoot, "SampleApp", "main.cpp"));
        Assert.Single(appKeys);
        Assert.Equal(new ModuleKey(ModuleKind.MsBuild, "SampleApp.exe"), appKeys[0]);
    }

    [Fact]
    public void Resolve_NativeSample_StaticLibraryFile_TransitivelyResolvesToAllLinkingBinaries_M21()
    {
        string sampleRoot = LocateNativeSampleRoot();
        string[] vcxprojs =
        {
            Path.Combine(sampleRoot, "StaticMath", "StaticMath.vcxproj"),
            Path.Combine(sampleRoot, "CorePlugin", "CorePlugin.vcxproj"),
            Path.Combine(sampleRoot, "SampleApp", "SampleApp.vcxproj")
        };

        var graph = MsBuildProjectGraph.FromProjectFiles(vcxprojPaths: vcxprojs, PhysicalFileSystem.Instance, "Debug", "x64");
        var resolver = new MsBuildResolver(graph);

        // StaticMath is linked by both CorePlugin.dll and SampleApp.exe
        var staticLibKeys = resolver.Resolve(Path.Combine(sampleRoot, "StaticMath", "StaticMath.cpp"));

        Assert.Equal(2, staticLibKeys.Count);
        Assert.Contains(new ModuleKey(ModuleKind.MsBuild, "CorePlugin.dll"), staticLibKeys);
        Assert.Contains(new ModuleKey(ModuleKind.MsBuild, "SampleApp.exe"), staticLibKeys);
    }

    [Fact]
    public void Resolve_UnlistedFileInSubfolder_FallsBackToNearestProjectDirectory()
    {
        string sampleRoot = LocateNativeSampleRoot();
        string[] vcxprojs =
        {
            Path.Combine(sampleRoot, "CorePlugin", "CorePlugin.vcxproj")
        };

        var graph = MsBuildProjectGraph.FromProjectFiles(vcxprojPaths: vcxprojs, PhysicalFileSystem.Instance, "Debug", "x64");
        var resolver = new MsBuildResolver(graph);

        string unlistedHeader = Path.Combine(sampleRoot, "CorePlugin", "Internal", "Helper.h");
        var resolved = resolver.Resolve(unlistedHeader);

        Assert.Single(resolved);
        Assert.Equal(new ModuleKey(ModuleKind.MsBuild, "CorePlugin.dll"), resolved[0]);
    }

    [Fact]
    public void Resolve_MakefileProject_IgnoredByMsBuildGraph()
    {
        var makefileProj = new MsBuildProjectInfo(
            @"C:\Proj\Intermediate\ProjectFiles\MyGame.vcxproj",
            MsBuildConfigurationType.Makefile,
            "UnrealEditor.exe",
            new[] { @"C:\Proj\Source\MyGame\MyActor.cpp" });

        var graph = new MsBuildProjectGraph(new[] { makefileProj });
        var resolver = new MsBuildResolver(graph);

        Assert.Empty(resolver.Resolve(@"C:\Proj\Source\MyGame\MyActor.cpp"));
    }

    [Fact]
    public void CompositeResolver_UnrealAndAllNativeScopes_ResolvesCorrectly()
    {
        var fs = new InMemoryFileSystem();
        fs.WriteAllText(@"C:\Game\MyGame.uproject", "{}");
        fs.WriteAllText(@"C:\Game\Source\MyGame\MyGame.Build.cs", "");
        fs.WriteAllText(@"C:\Game\Source\MyGame\Private\MyActor.cpp", "");

        var unrealResolver = new UnrealResolver(fs, NullLogger.Instance, projectRoot: @"C:\Game");

        var nativeProj = new MsBuildProjectInfo(
            @"C:\Native\ToolLib\ToolLib.vcxproj",
            MsBuildConfigurationType.DynamicLibrary,
            "ToolLib.dll",
            new[] { @"C:\Native\ToolLib\Tool.cpp" });
        var msBuildResolver = new MsBuildResolver(new MsBuildProjectGraph(new[] { nativeProj }));

        var composite = new CompositeResolver(unrealResolver, msBuildResolver)
        {
            IsUnrealSolution = true,
            Scope = SymbolScope.UnrealOnly
        };

        // In UnrealOnly scope, Unreal files resolve, non-Unreal MSBuild files do not
        Assert.Single(composite.Resolve(@"C:\Game\Source\MyGame\Private\MyActor.cpp"));
        Assert.Empty(composite.Resolve(@"C:\Native\ToolLib\Tool.cpp"));

        // In AllNative scope, both resolve
        composite.Scope = SymbolScope.AllNative;
        var nativeResolved = composite.Resolve(@"C:\Native\ToolLib\Tool.cpp");
        Assert.Single(nativeResolved);
        Assert.Equal(new ModuleKey(ModuleKind.MsBuild, "ToolLib.dll"), nativeResolved[0]);
    }
}
