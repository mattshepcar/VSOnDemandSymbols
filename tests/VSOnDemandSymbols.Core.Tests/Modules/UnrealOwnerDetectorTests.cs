using System.IO;
using VSOnDemandSymbols.Core.Abstractions;
using VSOnDemandSymbols.Core.Modules;
using Xunit;

namespace VSOnDemandSymbols.Core.Tests.Modules;

public class UnrealOwnerDetectorTests
{
    private readonly InMemoryFileSystem _fileSystem;
    private readonly UnrealOwnerDetector _detector;

    public UnrealOwnerDetectorTests()
    {
        _fileSystem = new InMemoryFileSystem();
        _detector = new UnrealOwnerDetector(_fileSystem);
    }

    [Fact]
    public void DetectOwner_ProjectDirectory_ReturnsProject()
    {
        string projDir = @"C:\Dev\MyGame";
        _fileSystem.CreateDirectory(projDir);
        _fileSystem.WriteAllText(Path.Combine(projDir, "MyGame.uproject"), "{}");

        string srcDir = Path.Combine(projDir, "Source", "MyGame", "Private");
        _fileSystem.CreateDirectory(srcDir);

        var owner = _detector.DetectOwner(srcDir);
        Assert.Equal(UnrealOwnerKind.Project, owner.Kind);
        Assert.Equal(projDir, owner.RootDirectory);
        Assert.Equal(Path.Combine(projDir, "Binaries", "Win64"), owner.BinariesDirectory);
    }

    [Fact]
    public void DetectOwner_ProjectPlugin_ReturnsProjectPlugin()
    {
        string projDir = @"C:\Dev\MyGame";
        _fileSystem.CreateDirectory(projDir);
        _fileSystem.WriteAllText(Path.Combine(projDir, "MyGame.uproject"), "{}");

        string pluginDir = Path.Combine(projDir, "Plugins", "SuperPlugin");
        _fileSystem.CreateDirectory(pluginDir);
        _fileSystem.WriteAllText(Path.Combine(pluginDir, "SuperPlugin.uplugin"), "{}");

        string srcDir = Path.Combine(pluginDir, "Source", "SuperPlugin");
        _fileSystem.CreateDirectory(srcDir);

        var owner = _detector.DetectOwner(srcDir);
        Assert.Equal(UnrealOwnerKind.ProjectPlugin, owner.Kind);
        Assert.Equal(pluginDir, owner.RootDirectory);
        Assert.Equal(Path.Combine(pluginDir, "Binaries", "Win64"), owner.BinariesDirectory);
    }

    [Fact]
    public void DetectOwner_EngineRoot_ReturnsEngine()
    {
        string engineDir = @"D:\UnrealEngine";
        _fileSystem.CreateDirectory(engineDir);
        _fileSystem.WriteAllText(Path.Combine(engineDir, "Engine", "Build", "Build.version"), "{}");

        string engineSourceDir = Path.Combine(engineDir, "Engine", "Source", "Runtime", "Core", "Private");
        _fileSystem.CreateDirectory(engineSourceDir);

        var owner = _detector.DetectOwner(engineSourceDir, knownEngineRoot: engineDir);
        Assert.Equal(UnrealOwnerKind.Engine, owner.Kind);
        Assert.Equal(engineDir, owner.RootDirectory);
        Assert.Equal(Path.Combine(engineDir, "Engine", "Binaries", "Win64"), owner.BinariesDirectory);
    }
}
