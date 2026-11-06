using System;
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
    // Off aliases
    [InlineData("none", BreakSymbolLoadMode.Off)]
    [InlineData("disabled", BreakSymbolLoadMode.Off)]
    [InlineData("false", BreakSymbolLoadMode.Off)]
    // LocalOnly aliases
    [InlineData("local", BreakSymbolLoadMode.LocalOnly)]
    // All aliases
    [InlineData("remote", BreakSymbolLoadMode.All)]
    [InlineData("network", BreakSymbolLoadMode.All)]
    [InlineData("true", BreakSymbolLoadMode.All)]
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

    [Fact]
    public void FromEnvironment_ReadsEnvironmentVariable()
    {
        string? original = Environment.GetEnvironmentVariable(BreakSymbolLoadModeExtensions.EnvironmentVariableName);
        try
        {
            Environment.SetEnvironmentVariable(BreakSymbolLoadModeExtensions.EnvironmentVariableName, "Off");
            Assert.Equal(BreakSymbolLoadMode.Off, BreakSymbolLoadModeExtensions.FromEnvironment());

            Environment.SetEnvironmentVariable(BreakSymbolLoadModeExtensions.EnvironmentVariableName, "All");
            Assert.Equal(BreakSymbolLoadMode.All, BreakSymbolLoadModeExtensions.FromEnvironment());

            Environment.SetEnvironmentVariable(BreakSymbolLoadModeExtensions.EnvironmentVariableName, "1");
            Assert.Equal(BreakSymbolLoadMode.LocalOnly, BreakSymbolLoadModeExtensions.FromEnvironment());

            Environment.SetEnvironmentVariable(BreakSymbolLoadModeExtensions.EnvironmentVariableName, null);
            Assert.Equal(BreakSymbolLoadMode.LocalOnly, BreakSymbolLoadModeExtensions.FromEnvironment());
        }
        finally
        {
            Environment.SetEnvironmentVariable(BreakSymbolLoadModeExtensions.EnvironmentVariableName, original);
        }
    }
}
