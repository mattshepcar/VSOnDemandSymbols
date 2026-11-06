using System.IO;
using VSOnDemandSymbols.Core.Modules;
using VSOnDemandSymbols.Core.Policy;
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
    public void GetCandidateBinaries_ReturnsWildcardPattern()
    {
        var candidates = _resolver.GetCandidateBinaries("MyGame");
        Assert.Single(candidates);
        Assert.Equal("UnrealEditor-MyGame*.dll", candidates[0]);
    }

    [Theory]
    [InlineData("UnrealEditor-MyGame.dll")]
    [InlineData("UnrealEditor-MyGame-Win64-DebugGame.dll")]
    [InlineData("UnrealEditor-MyGame-Win64-Development.dll")]
    [InlineData("UnrealEditor-MyGame-Win64-Shipping.dll")]
    [InlineData("UnrealEditor-MyGame-Win64-Debug.dll")]
    [InlineData("UnrealEditor-MyGame-0001.dll")]
    [InlineData("UnrealEditor-MyGame-0001-Win64-DebugGame.dll")]
    public void GetCandidateBinaries_WildcardMatchesAllConfigurations(string binaryName)
    {
        var candidates = _resolver.GetCandidateBinaries("MyGame");
        var pattern = new WildcardPattern(candidates[0]);
        Assert.True(pattern.IsMatchFileName(binaryName));
    }

    [Theory]
    [InlineData("UnrealEditor.exe")]
    [InlineData("UnrealEditor-Win64-DebugGame.exe")]
    [InlineData("UnrealEditor-Win64-Development.exe")]
    [InlineData("UnrealEditor-Win64-Shipping.exe")]
    [InlineData("UnrealEditor-Win64-Debug.exe")]
    [InlineData("UnrealEditor-Cmd.exe")]
    public void UnrealEditorExePattern_MatchesAllEditorExecutableVariants(string exeName)
    {
        var pattern = new WildcardPattern("UnrealEditor*.exe");
        Assert.True(pattern.IsMatchFileName(exeName));
    }

    [Fact]
    public void IsUnrealSolution_UprojectAtRoot_ReturnsTrue()
    {
        Assert.True(_resolver.IsUnrealSolution(@"C:\Dev\MyGame"));
    }

    [Fact]
    public void IsUnrealSolution_NestedIntermediateDirectory_ReturnsTrue()
    {
        Assert.True(_resolver.IsUnrealSolution(@"C:\Dev\MyGame\Intermediate\ProjectFiles"));
    }

    [Fact]
    public void IsUnrealSolution_EngineSourceRoot_ReturnsTrue()
    {
        _fileSystem.WriteAllText(@"C:\UnrealEngine\Engine\Build\Build.version", "{}");
        Assert.True(_resolver.IsUnrealSolution(@"C:\UnrealEngine"));
    }

    [Fact]
    public void IsUnrealSolution_StandardNativeNonUnrealSolution_ReturnsFalse()
    {
        _fileSystem.WriteAllText(@"C:\Dev\NativeSample\NativeSample.sln", "...");
        Assert.False(_resolver.IsUnrealSolution(@"C:\Dev\NativeSample"));
    }
}
