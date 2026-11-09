using VSOnDemandSymbols.Core.Policy;
using Xunit;

namespace VSOnDemandSymbols.Core.Tests.Policy;

public class WildcardPatternTests
{
    [Fact]
    public void FileNamePattern_MatchesWildcards()
    {
        var pattern = new WildcardPattern("UnrealEditor*.exe");
        Assert.True(pattern.IsMatchFileName("UnrealEditor.exe"));
        Assert.True(pattern.IsMatchFileName("UnrealEditor-Win64-Debug.exe"));
        Assert.True(pattern.IsMatchFileName(@"D:\Path\To\UnrealEditor.exe"));
        Assert.False(pattern.IsMatchFileName("UE4Editor.exe"));
    }

    [Fact]
    public void FileNamePattern_QuestionMarkWildcard_MatchesSingleCharacter()
    {
        var pattern = new WildcardPattern("Module?.dll");
        Assert.True(pattern.IsMatchFileName("Module1.dll"));
        Assert.True(pattern.IsMatchFileName("ModuleA.dll"));
        Assert.False(pattern.IsMatchFileName("Module12.dll"));
        Assert.False(pattern.IsMatchFileName("Module.dll"));
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



    [Theory]
    [InlineData("UnrealEditor-MyGame*.dll", "UnrealEditor-MyGame.dll", true)]
    [InlineData("UnrealEditor-MyGame*.dll", "UnrealEditor-MyGame-Win64-DebugGame.dll", true)]
    [InlineData("UnrealEditor-MyGame*.dll", @"D:\Binaries\Win64\UnrealEditor-MyGame-Win64-Development.dll", true)]
    [InlineData("UnrealEditor-MyGame*.dll", "UnrealEditor-OtherGame.dll", false)]
    [InlineData("UnrealEditor-MyGame*.dll", "", false)]
    public void MatchesFileName_ValidatesCandidateAgainstLoadedModuleNames(string pattern, string fileName, bool expected)
    {
        Assert.Equal(expected, WildcardPattern.MatchesFileName(pattern, fileName));
    }

    [Fact]
    public void MatchesAnyFileName_MatchesWhenAnyPatternMatches()
    {
        var patterns = new[]
        {
            new WildcardPattern("UnrealEditor-MyGame*.dll"),
            new WildcardPattern("UnrealEditor-Combat*.dll")
        };

        Assert.True(WildcardPattern.MatchesAnyFileName(patterns, "UnrealEditor-Combat-Win64-DebugGame.dll"));
        Assert.True(WildcardPattern.MatchesAnyFileName(patterns, "UnrealEditor-MyGame.dll"));
        Assert.False(WildcardPattern.MatchesAnyFileName(patterns, "UnrealEditor-Engine.dll"));
    }

    [Theory]
    [InlineData("UnrealEditor-Engine.dll", "UnrealEditor-Engine.dll")]
    [InlineData("UnrealEditor-Engine", "UnrealEditor-Engine.dll")]
    [InlineData("UnrealEditor.exe", "UnrealEditor.exe")]
    [InlineData("UnrealEditor-Engine.dll!00007ffc12345678", "UnrealEditor-Engine.dll")]
    [InlineData("UnrealEditor-Core!UE::Log()", "UnrealEditor-Core.dll")]
    [InlineData("UnrealEditor-Core!UE::Log(char const*, ...)", "UnrealEditor-Core.dll")]
    [InlineData(@"D:\Engine\Binaries\Win64\UnrealEditor-Engine.dll", "UnrealEditor-Engine.dll")]
    [InlineData("ntdll.dll!NtWaitForSingleObject", "ntdll.dll")]
    [InlineData("[External Code]", null)]
    [InlineData("<Unknown>", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeFrameModuleName_HandlesVariousStackFrameFormats(string? input, string? expected)
    {
        Assert.Equal(expected, WildcardPattern.NormalizeFrameModuleName(input));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\ntdll.dll", true)]
    [InlineData(@"C:\Windows\System32\KernelBase.dll", true)]
    [InlineData("ntdll.dll", true)]
    [InlineData("kernel32.dll", true)]
    [InlineData("user32.dll", true)]
    [InlineData("win32u.dll", true)]
    [InlineData("mswsock.dll", true)]
    [InlineData("dxgi.dll", true)]
    [InlineData("ntdll.dll!NtWaitForSingleObject", true)]
    [InlineData("KernelBase!WaitForSingleObjectEx", true)]
    [InlineData("UnrealEditor-Core.dll", false)]
    [InlineData(@"D:\UnrealEngine\Engine\Binaries\Win64\UnrealEditor-Core.dll", false)]
    [InlineData("EOSSDK-Win64-Shipping.dll", false)]
    [InlineData("[External Code]", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSystemModule_IdentifiesOsAndRuntimeLibraries(string? input, bool expected)
    {
        Assert.Equal(expected, WildcardPattern.IsSystemModule(input));
    }

    [Theory]
    [InlineData("UnrealEditor-Core.dll", true)]
    [InlineData(@"D:\UnrealEngine\Engine\Binaries\Win64\UnrealEditor-Core.dll", true)]
    [InlineData("UnrealEditor-WebSockets.dll!00007ffa1d386250", true)]
    [InlineData("EOSSDK-Win64-Shipping.dll", true)]
    [InlineData("LyraEditor.dll", true)]
    [InlineData(@"C:\Windows\System32\ntdll.dll", false)]
    [InlineData("kernel32.dll", false)]
    [InlineData("[External Code]", false)]
    [InlineData("<Unknown>", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsUserCodeModule_DistinguishesUserCodeFromSystemLibraries(string? input, bool expected)
    {
        Assert.Equal(expected, WildcardPattern.IsUserCodeModule(input));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("MyModule.dll\0extra_trailing_bytes", "MyModule.dll")]
    [InlineData("  D:\\Engine\\UnrealEditor-Core.dll\0garbage  ", @"D:\Engine\UnrealEditor-Core.dll")]
    public void SanitizePathString_EmbeddedNullAndWhitespace_SanitizesSafely(string? input, string expected)
    {
        Assert.Equal(expected, WildcardPattern.SanitizePathString(input));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\ntdll.dll", "ntdll.dll")]
    [InlineData("D:/Engine/Binaries/Win64/UnrealEditor-Engine.dll", "UnrealEditor-Engine.dll")]
    [InlineData("UnrealEditor-Core.dll\0null_trailer", "UnrealEditor-Core.dll")]
    [InlineData(@"D:\Path\Invalid<Char>|Pipe.dll", "Invalid<Char>|Pipe.dll")]
    [InlineData(@"C:\Folder\", "")]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void ExtractFileNameSafe_EmbeddedNullAndInvalidCharacters_ExtractsWithoutThrowing(string? input, string expected)
    {
        Assert.Equal(expected, WildcardPattern.ExtractFileNameSafe(input));
    }

    [Fact]
    public void MatchesFileName_RepeatedCalls_UsesCachedRegexConsistently()
    {
        for (int i = 0; i < 100; i++)
        {
            Assert.True(WildcardPattern.MatchesFileName("UnrealEditor-MyGame*.dll", "UnrealEditor-MyGame.dll"));
            Assert.False(WildcardPattern.MatchesFileName("UnrealEditor-MyGame*.dll", "UnrealEditor-OtherGame.dll"));
        }
    }
}

