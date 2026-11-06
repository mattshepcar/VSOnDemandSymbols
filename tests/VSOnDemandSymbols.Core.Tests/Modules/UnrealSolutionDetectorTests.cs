using VSOnDemandSymbols.Core.Modules;
using Xunit;

namespace VSOnDemandSymbols.Core.Tests.Modules;

public class UnrealSolutionDetectorTests
{
    private readonly InMemoryFileSystem _fs = new();
    private readonly UnrealSolutionDetector _detector;

    public UnrealSolutionDetectorTests()
    {
        _detector = new UnrealSolutionDetector(_fs);
    }

    [Fact]
    public void Detect_SolutionFolderContainsUproject_ReturnsUnrealWithProjectRoot()
    {
        _fs.WriteAllText(@"D:\Games\MyGame\MyGame.uproject", "{}");
        _fs.WriteAllText(@"D:\Games\MyGame\MyGame.sln", "");

        var result = _detector.Detect(@"D:\Games\MyGame\MyGame.sln");

        Assert.True(result.IsUnreal);
        Assert.Equal(@"D:\Games\MyGame", result.ProjectRoot);
        Assert.Equal(@"D:\Games\MyGame\MyGame.uproject", result.UprojectPath);
        Assert.Equal("MyGame", result.ProjectName);
    }

    [Fact]
    public void Detect_IntermediateProjectFilesWithEngineAndProjectAncestors_ResolvesBothRoots()
    {
        _fs.WriteAllText(@"D:\UnrealEngine\Engine\Build\Build.version", "{}");
        _fs.WriteAllText(@"D:\Games\Shooter\Shooter.uproject", "{}");

        string[] projects =
        {
            @"D:\UnrealEngine\Engine\Intermediate\ProjectFiles\UE5.vcxproj",
            @"D:\Games\Shooter\Intermediate\ProjectFiles\Shooter.vcxproj"
        };

        var result = _detector.Detect(@"D:\Games\Shooter\Shooter.sln", projects);

        Assert.True(result.IsUnreal);
        Assert.Equal(@"D:\UnrealEngine", result.EngineRoot);
        Assert.Equal(@"D:\Games\Shooter", result.ProjectRoot);
        Assert.Equal("Shooter", result.ProjectName);
    }

    [Fact]
    public void Detect_EngineOnlySolution_ReturnsUnrealWithEngineRootAndNullProjectRoot()
    {
        _fs.WriteAllText(@"D:\UnrealEngine\Engine\Build\Build.version", "{}");

        string[] projects =
        {
            @"D:\UnrealEngine\Engine\Intermediate\ProjectFiles\UE5.vcxproj"
        };

        var result = _detector.Detect(@"D:\UnrealEngine\UE5.sln", projects);

        Assert.True(result.IsUnreal);
        Assert.Equal(@"D:\UnrealEngine", result.EngineRoot);
        Assert.Null(result.ProjectRoot);
        Assert.Null(result.ProjectName);
    }

    [Fact]
    public void Detect_NonUnrealSolution_ReturnsNotUnreal()
    {
        _fs.WriteAllText(@"C:\Projects\NativeApp\NativeApp.sln", "");
        _fs.WriteAllText(@"C:\Projects\NativeApp\App\App.vcxproj", "");

        var result = _detector.Detect(
            @"C:\Projects\NativeApp\NativeApp.sln",
            new[] { @"C:\Projects\NativeApp\App\App.vcxproj" });

        Assert.False(result.IsUnreal);
        Assert.Null(result.EngineRoot);
        Assert.Null(result.ProjectRoot);
    }

    [Fact]
    public void Detect_ForceUnrealModeOverride_HonorsTrueAndFalse()
    {
        _fs.WriteAllText(@"C:\Projects\NativeApp\NativeApp.sln", "");
        var forcedOn = _detector.Detect(@"C:\Projects\NativeApp\NativeApp.sln", forceUnrealMode: true);
        Assert.True(forcedOn.IsUnreal);

        _fs.WriteAllText(@"D:\Games\MyGame\MyGame.uproject", "{}");
        var forcedOff = _detector.Detect(@"D:\Games\MyGame\MyGame.sln", forceUnrealMode: false);
        Assert.False(forcedOff.IsUnreal);
        Assert.Equal(@"D:\Games\MyGame", forcedOff.ProjectRoot);
    }

    [Theory]
    [InlineData(@"D:\UE\Engine\Binaries\Win64\UnrealEditor.exe", "MyGame", true)]
    [InlineData(@"D:\UE\Engine\Binaries\Win64\UnrealEditor-Win64-DebugGame.exe", "MyGame", true)]
    [InlineData(@"D:\UE\Engine\Binaries\Win64\UE4Editor.exe", null, true)]
    [InlineData(@"D:\Games\MyGame\Binaries\Win64\MyGameEditor.exe", "MyGame", true)]
    [InlineData(@"D:\Games\MyGame\Binaries\Win64\MyGame-Win64-DebugGame.exe", "MyGame", true)]
    [InlineData(@"C:\Tools\ShaderCompileWorker.exe", "MyGame", false)]
    public void IsQualifyingUnrealProcess_MatchesExpectedPatterns(
        string processPath,
        string? projectName,
        bool expected)
    {
        bool actual = UnrealSolutionDetector.IsQualifyingUnrealProcess(processPath, null, projectName);
        Assert.Equal(expected, actual);
    }
}

