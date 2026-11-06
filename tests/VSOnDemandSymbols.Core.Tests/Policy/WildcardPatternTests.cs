using VSOnDemandSymbols.Core.Modules;
using VSOnDemandSymbols.Core.Policy;
using Xunit;

namespace VSOnDemandSymbols.Core.Tests.Policy;

public class WildcardPatternTests
{
    [Fact]
    public void FileNamePattern_MatchesWildcards()
    {
        var pattern = new WildcardPattern("UnrealEditor*.exe");
        Assert.False(pattern.IsUnrealModulePattern);
        Assert.True(pattern.IsMatchFileName("UnrealEditor.exe"));
        Assert.True(pattern.IsMatchFileName("UnrealEditor-Win64-Debug.exe"));
        Assert.True(pattern.IsMatchFileName(@"D:\Path\To\UnrealEditor.exe"));
        Assert.False(pattern.IsMatchFileName("UE4Editor.exe"));
    }

    [Fact]
    public void UnrealModulePattern_MatchesUePrefix()
    {
        var pattern = new WildcardPattern("ue:Engine*");
        Assert.True(pattern.IsUnrealModulePattern);
        Assert.Equal("Engine*", pattern.Pattern);

        Assert.True(pattern.IsMatchModule("Engine"));
        Assert.True(pattern.IsMatchModule("EngineSettings"));
        Assert.False(pattern.IsMatchModule("Core"));
        Assert.False(pattern.IsMatchFileName("Engine.dll"));
    }

    [Fact]
    public void ParseMultiple_SplitsSemicolons()
    {
        var patterns = WildcardPattern.ParseMultiple("UnrealEditor*.exe; ue:Engine; ; *.dll ");
        Assert.Equal(3, patterns.Count);
        Assert.Equal("UnrealEditor*.exe", patterns[0].RawPattern);
        Assert.Equal("ue:Engine", patterns[1].RawPattern);
        Assert.Equal("*.dll", patterns[2].RawPattern);
    }
}

