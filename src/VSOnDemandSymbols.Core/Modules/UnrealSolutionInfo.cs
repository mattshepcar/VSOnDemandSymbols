namespace VSOnDemandSymbols.Core.Modules;

/// <summary>
/// Result of Unreal solution detection (SPEC §7.2).
/// </summary>
public sealed class UnrealSolutionInfo
{
    public static readonly UnrealSolutionInfo NotUnreal = new(false, null, null, null, null);

    public bool IsUnreal { get; }
    public string? EngineRoot { get; }
    public string? ProjectRoot { get; }
    public string? UprojectPath { get; }
    public string? ProjectName { get; }

    public UnrealSolutionInfo(
        bool isUnreal,
        string? engineRoot,
        string? projectRoot,
        string? uprojectPath,
        string? projectName)
    {
        IsUnreal = isUnreal;
        EngineRoot = engineRoot;
        ProjectRoot = projectRoot;
        UprojectPath = uprojectPath;
        ProjectName = projectName;
    }
}

