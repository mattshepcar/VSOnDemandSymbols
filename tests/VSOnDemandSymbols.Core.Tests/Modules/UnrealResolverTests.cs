using System;
using System.IO;
using VSOnDemandSymbols.Core.Abstractions;
using VSOnDemandSymbols.Core.Modules;
using Xunit;

namespace VSOnDemandSymbols.Core.Tests.Modules;

public class UnrealResolverTests
{
    private readonly string _fixtureProjectRoot;
    private readonly UnrealResolver _resolver;

    public UnrealResolverTests()
    {
        // Find fixture directory relative to test assembly or repo root
        string baseDir = AppContext.BaseDirectory;
        string fixturePath = Path.Combine(baseDir, "Fixtures", "FakeUnrealProject");
        if (!Directory.Exists(fixturePath))
        {
            // Try relative to workspace
            fixturePath = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "Fixtures", "FakeUnrealProject"));
        }

        _fixtureProjectRoot = fixturePath;
        _resolver = new UnrealResolver(
            PhysicalFileSystem.Instance,
            NullLogger.Instance,
            projectRoot: _fixtureProjectRoot);
    }

    [Fact]
    public void Resolve_SourceFileInGameModule_WalksUpToBuildCs()
    {
        string actorCpp = Path.Combine(_fixtureProjectRoot, "Source", "MyGame", "Private", "MyActor.cpp");
        var keys = _resolver.Resolve(actorCpp);

        Assert.Single(keys);
        Assert.Equal(ModuleKind.Unreal, keys[0].Kind);
        Assert.Equal("MyGame", keys[0].Name);
    }

    [Fact]
    public void Resolve_NestedSourceFile_WalksUpMultipleLevels()
    {
        string subsystemCpp = Path.Combine(_fixtureProjectRoot, "Source", "MyGame", "Private", "Subsystem", "MySubsystem.cpp");
        var keys = _resolver.Resolve(subsystemCpp);

        Assert.Single(keys);
        Assert.Equal("MyGame", keys[0].Name);
    }

    [Fact]
    public void Resolve_PluginSourceFile_WalksUpToPluginBuildCs()
    {
        string pluginActorCpp = Path.Combine(_fixtureProjectRoot, "Plugins", "MyPlugin", "Source", "MyPlugin", "Private", "MyPluginActor.cpp");
        var keys = _resolver.Resolve(pluginActorCpp);

        Assert.Single(keys);
        Assert.Equal("MyPlugin", keys[0].Name);
    }

    [Fact]
    public void Resolve_IntermediateIncFile_ExtractsModuleNameFromPath()
    {
        string genCpp = Path.Combine(_fixtureProjectRoot, "Intermediate", "Build", "Win64", "UnrealEditor", "Inc", "MyGame", "UHT", "MyActor.gen.cpp");
        var keys = _resolver.Resolve(genCpp);

        Assert.Single(keys);
        Assert.Equal("MyGame", keys[0].Name);
    }

    [Fact]
    public void Resolve_IntermediateIncWithSubFolder_ExtractsModuleNameFromPath()
    {
        string genCpp = Path.Combine(_fixtureProjectRoot, "Intermediate", "Build", "Win64", "UnrealEditor", "Inc", "SubFolder", "MyGame", "UHT", "MyActor.gen.cpp");
        var keys = _resolver.Resolve(genCpp);

        Assert.Single(keys);
        Assert.Equal("MyGame", keys[0].Name);
    }

    [Fact]
    public void Resolve_IntermediateUnityWithPartition_ExtractsModuleNameFromPath()
    {
        string unityCpp = Path.Combine(_fixtureProjectRoot, "Intermediate", "Build", "Win64", "UnrealEditor", "Development", "MyGame", "Module.MyGame.2.cpp");
        var keys = _resolver.Resolve(unityCpp);

        Assert.Single(keys);
        Assert.Equal("MyGame", keys[0].Name);
    }

    [Fact]
    public void Resolve_IntermediateUnityLegacyPartition_ExtractsModuleNameFromPath()
    {
        string legacyUnityCpp = Path.Combine(_fixtureProjectRoot, "Intermediate", "Build", "Win64", "UnrealEditor", "Development", "MyGame", "Module.MyGame.1_of_4.cpp");
        var keys = _resolver.Resolve(legacyUnityCpp);

        Assert.Single(keys);
        Assert.Equal("MyGame", keys[0].Name);
    }

    [Fact]
    public void GetCandidateFileNames_UsesModulesManifest()
    {
        var key = new ModuleKey(ModuleKind.Unreal, "MyGame");
        var candidates = _resolver.GetCandidateFileNames(key);

        Assert.Contains("UnrealEditor-MyGame.dll", candidates);
    }

    [Fact]
    public void GetCandidateFileNames_FallbackWhenNoManifest()
    {
        var key = new ModuleKey(ModuleKind.Unreal, "NonExistentModule");
        var candidates = _resolver.GetCandidateFileNames(key);

        Assert.Contains("UnrealEditor-NonExistentModule.dll", candidates);
        Assert.Contains("UnrealEditor-NonExistentModule-Win64-DebugGame.dll", candidates);
    }

    [Fact]
    public void TryParseLoadedModule_ParsesUnrealDll()
    {
        string dllPath = Path.Combine(_fixtureProjectRoot, "Binaries", "Win64", "UnrealEditor-MyGame.dll");
        var key = _resolver.TryParseLoadedModule(dllPath);

        Assert.NotNull(key);
        Assert.Equal(ModuleKind.Unreal, key.Value.Kind);
        Assert.Equal("MyGame", key.Value.Name);
    }
}

