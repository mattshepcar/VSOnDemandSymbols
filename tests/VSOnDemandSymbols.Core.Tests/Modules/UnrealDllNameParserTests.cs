using VSOnDemandSymbols.Core.Modules;
using Xunit;

namespace VSOnDemandSymbols.Core.Tests.Modules;

public class UnrealDllNameParserTests
{
    private readonly UnrealDllNameParser _parser = new();

    [Theory]
    [InlineData("UnrealEditor-MyGame.dll", "MyGame", null, null, null, null)]
    [InlineData("UnrealEditor-Engine-Win64-DebugGame.dll", "Engine", "Win64", "DebugGame", null, null)]
    [InlineData("UnrealEditor-Core-Win64-Development.dll", "Core", "Win64", "Development", null, null)]
    [InlineData("UnrealEditor-MyGame-0001.dll", "MyGame", null, null, null, "0001")]
    [InlineData("UnrealEditor-MyGame-0001-Win64-DebugGame.dll", "MyGame", "Win64", "DebugGame", null, "0001")]
    [InlineData("UnrealEditor-MyGame-Win64-DebugGame-1234.dll", "MyGame", "Win64", "DebugGame", null, "1234")]
    [InlineData("UnrealEditor-MyGame-Win64-Development-arm64.dll", "MyGame", "Win64", "Development", "arm64", null)]
    [InlineData("UnrealEditor-MyGame-arm64.dll", "MyGame", null, null, "arm64", null)]
    [InlineData("UE4Editor-MyGame.dll", "MyGame", null, null, null, null)]
    [InlineData("UnrealGame-ShooterGame.dll", "ShooterGame", null, null, null, null)]
    [InlineData("UnrealClient-MyGame.dll", "MyGame", null, null, null, null)]
    [InlineData("UnrealServer-MyGame.dll", "MyGame", null, null, null, null)]
    public void TryParse_StandardDlls_Succeeds(
        string fileName,
        string expectedModule,
        string? expectedPlatform,
        string? expectedConfig,
        string? expectedArch,
        string? expectedHotReload)
    {
        bool success = _parser.TryParse(fileName, out var info);
        Assert.True(success);
        Assert.NotNull(info);
        Assert.Equal(expectedModule, info.ModuleName);
        Assert.Equal(ModuleKind.Unreal, info.Key.Kind);
        Assert.Equal(expectedModule, info.Key.Name);
        Assert.Equal(expectedPlatform, info.Platform);
        Assert.Equal(expectedConfig, info.Configuration);
        Assert.Equal(expectedArch, info.Architecture);
        Assert.Equal(expectedHotReload, info.HotReloadSuffix);
        Assert.False(info.IsLiveCodingPatch);
    }

    [Theory]
    [InlineData("UnrealEditor-MyGame.patch_0.exe", "MyGame")]
    [InlineData("UnrealEditor-Engine-Win64-DebugGame.patch_1.exe", "Engine")]
    [InlineData("UnrealEditor-MyGame.patch_2.dll", "MyGame")]
    public void TryParse_LiveCodingPatches_Succeeds(string fileName, string expectedModule)
    {
        bool success = _parser.TryParse(fileName, out var info);
        Assert.True(success);
        Assert.NotNull(info);
        Assert.Equal(expectedModule, info.ModuleName);
        Assert.True(info.IsLiveCodingPatch);
    }

    [Theory]
    [InlineData("api-ms-win-core-synch-l1-2-0.dll")]
    [InlineData("user32.dll")]
    [InlineData("ThirdPartyLib.dll")]
    [InlineData("UnrealEditor.exe")]
    [InlineData("")]
    public void TryParse_NonMatchingDlls_ReturnsFalse(string fileName)
    {
        bool success = _parser.TryParse(fileName, out var info);
        Assert.False(success);
        Assert.Null(info);
        Assert.Null(_parser.TryParseLoadedModule(fileName));
    }

    [Fact]
    public void CustomTargetPrefix_IsRecognized()
    {
        var parser = new UnrealDllNameParser(new[] { "CustomGameEditor" });
        bool success = parser.TryParse("CustomGameEditor-MyModule.dll", out var info);
        Assert.True(success);
        Assert.Equal("MyModule", info.ModuleName);
    }
}
