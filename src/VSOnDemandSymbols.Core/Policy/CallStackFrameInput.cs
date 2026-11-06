namespace VSOnDemandSymbols.Core.Policy;

/// <summary>
/// Represents a single stack frame inspected during break mode or frame activation (SPEC §5.2, §5.3).
/// </summary>
public readonly struct CallStackFrameInput
{
    public string ModuleFileName { get; }
    public string? FunctionName { get; }
    public bool HasSymbolsLoaded { get; }

    public CallStackFrameInput(string moduleFileName, string? functionName, bool hasSymbolsLoaded)
    {
        ModuleFileName = WildcardPattern.ExtractFileNameSafe(moduleFileName);
        FunctionName = string.IsNullOrWhiteSpace(functionName) ? null : functionName!.Trim();
        HasSymbolsLoaded = hasSymbolsLoaded;
    }
}

