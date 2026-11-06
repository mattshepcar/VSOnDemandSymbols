using VSOnDemandSymbols.Core.Modules;
using Xunit;

namespace VSOnDemandSymbols.Core.Tests.Modules;

public class FunctionBreakpointParserTests
{
    [Theory]
    [InlineData("UnrealEditor-MyGame.dll!AMyActor::Tick", "UnrealEditor-MyGame.dll")]
    [InlineData("UnrealEditor-Engine!UWorld::Tick", "UnrealEditor-Engine")]
    [InlineData("CorePlugin.dll!ComputePluginValue", "CorePlugin.dll")]
    public void TryExtractModuleName_BangSyntax_ExtractsModule_BP4(string expr, string expectedModule)
    {
        bool ok = FunctionBreakpointParser.TryExtractModuleName(expr, out string? moduleName);

        Assert.True(ok);
        Assert.Equal(expectedModule, moduleName);
    }

    [Theory]
    [InlineData("{,,UnrealEditor-MyGame.dll}AMyActor::Tick", "UnrealEditor-MyGame.dll")]
    [InlineData("{,MyActor.cpp,UnrealEditor-MyGame-Win64-DebugGame.dll}AMyActor::BeginPlay", "UnrealEditor-MyGame-Win64-DebugGame.dll")]
    public void TryExtractModuleName_ContextOperatorSyntax_ExtractsModule_BP4(string expr, string expectedModule)
    {
        bool ok = FunctionBreakpointParser.TryExtractModuleName(expr, out string? moduleName);

        Assert.True(ok);
        Assert.Equal(expectedModule, moduleName);
    }

    [Theory]
    [InlineData("AMyActor::Tick")]
    [InlineData("main")]
    [InlineData("")]
    [InlineData("   ")]
    public void TryExtractModuleName_UnqualifiedFunction_ReturnsFalse_BP4(string expr)
    {
        bool ok = FunctionBreakpointParser.TryExtractModuleName(expr, out string? moduleName);

        Assert.False(ok);
        Assert.Null(moduleName);
    }
}

