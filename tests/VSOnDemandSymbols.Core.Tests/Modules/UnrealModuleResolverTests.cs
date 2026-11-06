using System.IO;
using VSOnDemandSymbols.Core.Modules;
using Xunit;

namespace VSOnDemandSymbols.Core.Tests.Modules;

public sealed class UnrealModuleResolverTests
{
    private readonly InMemoryFileSystem _fileSystem;
    private readonly UnrealModuleResolver _resolver;

    public UnrealModuleResolverTests()
    {
        _fileSystem = new InMemoryFileSystem();
        _resolver = new UnrealModuleResolver(_fileSystem);

        // Setup test project directory structure
        _fileSystem.WriteAllText(@"C:\Dev\MyGame\MyGame.uproject", "{}");
        _fileSystem.WriteAllText(@"C:\Dev\MyGame\Source\MyGame\MyGame.Build.cs", "// build");
        _fileSystem.WriteAllText(@"C:\Dev\MyGame\Source\MyGame\Private\MyActor.cpp", "// cpp");
        _fileSystem.WriteAllText(@"C:\Dev\MyGame\Source\MyGame\Public\MyActor.h", "// h");
        _fileSystem.WriteAllText(@"C:\Dev\MyGame\Plugins\Combat\Source\Combat\Combat.Build.cs", "// build");
        _fileSystem.WriteAllText(@"C:\Dev\MyGame\Plugins\Combat\Source\Combat\Private\Abilities\Fireball.cpp", "// cpp");
    }

    [Fact]
    public void ResolveModuleFromSource_GameModuleSourceFile_ReturnsModuleName()
    {
        string? module = _resolver.ResolveModuleFromSource(@"C:\Dev\MyGame\Source\MyGame\Private\MyActor.cpp");
        Assert.Equal("MyGame", module);
    }

    [Fact]
    public void ResolveModuleFromSource_NestedPluginSourceFile_ReturnsPluginModuleName()
    {
        string? module = _resolver.ResolveModuleFromSource(@"C:\Dev\MyGame\Plugins\Combat\Source\Combat\Private\Abilities\Fireball.cpp");
        Assert.Equal("Combat", module);
    }

    [Fact]
    public void ResolveModuleFromSource_IntermediateUhtFile_ReturnsModuleName()
    {
        string? module = _resolver.ResolveModuleFromSource(@"C:\Dev\MyGame\Intermediate\Build\Win64\UnrealEditor\Inc\MyGame\UHT\MyActor.gen.cpp");
        Assert.Equal("MyGame", module);
    }

    [Fact]
    public void ResolveModuleFromSource_IntermediateUnityFile_ReturnsModuleName()
    {
        string? module = _resolver.ResolveModuleFromSource(@"C:\Dev\MyGame\Intermediate\Build\Win64\UnrealEditor\Development\MyGame\Module.MyGame.1_of_3.cpp");
        Assert.Equal("MyGame", module);
    }

    [Fact]
    public void GetCandidateBinaries_ReturnsExpectedPatterns()
    {
        var candidates = _resolver.GetCandidateBinaries("MyGame");
        Assert.Contains("UnrealEditor-MyGame.dll", candidates);
        Assert.Contains("UnrealEditor-MyGame-Win64-DebugGame.dll", candidates);
        Assert.Contains("UnrealEditor-MyGame*.dll", candidates);
    }
}
