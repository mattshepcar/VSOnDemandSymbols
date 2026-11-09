using VSOnDemandSymbols.Core.Policy;
using Xunit;

namespace VSOnDemandSymbols.Core.Tests.Policy;

public class BreakSymbolLoadModeTests
{
    [Theory]
    // Empty & invalid defaults to LocalOnly
    [InlineData(null, BreakSymbolLoadMode.LocalOnly)]
    [InlineData("", BreakSymbolLoadMode.LocalOnly)]
    [InlineData("   ", BreakSymbolLoadMode.LocalOnly)]
    [InlineData("invalid", BreakSymbolLoadMode.LocalOnly)]
    [InlineData("-1", BreakSymbolLoadMode.LocalOnly)]
    [InlineData("99", BreakSymbolLoadMode.LocalOnly)]
    // Numeric representations
    [InlineData("0", BreakSymbolLoadMode.Off)]
    [InlineData("1", BreakSymbolLoadMode.LocalOnly)]
    [InlineData("2", BreakSymbolLoadMode.All)]
    // Enum names & case-insensitivity
    [InlineData("Off", BreakSymbolLoadMode.Off)]
    [InlineData("localonly", BreakSymbolLoadMode.LocalOnly)]
    [InlineData("ALL", BreakSymbolLoadMode.All)]
    public void ParseOrDefault_ParsesExpectedValues(string? input, BreakSymbolLoadMode expected)
    {
        Assert.Equal(expected, BreakSymbolLoadModeExtensions.ParseOrDefault(input));
    }

    [Fact]
    public void ParseOrDefault_UsesSpecifiedDefault_WhenUnparseable()
    {
        Assert.Equal(BreakSymbolLoadMode.Off, BreakSymbolLoadModeExtensions.ParseOrDefault("bogus", BreakSymbolLoadMode.Off));
        Assert.Equal(BreakSymbolLoadMode.All, BreakSymbolLoadModeExtensions.ParseOrDefault(null, BreakSymbolLoadMode.All));
    }
}

