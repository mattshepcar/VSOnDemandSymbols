namespace VSOnDemandSymbols.Core.Modules;

public enum UnrealOwnerKind
{
    Unknown,
    Engine,
    Project,
    EnginePlugin,
    ProjectPlugin
}

public sealed class UnrealOwner
{
    public UnrealOwnerKind Kind { get; }
    public string RootDirectory { get; }
    public string BinariesDirectory { get; }

    public UnrealOwner(UnrealOwnerKind kind, string rootDirectory, string binariesDirectory)
    {
        Kind = kind;
        RootDirectory = rootDirectory;
        BinariesDirectory = binariesDirectory;
    }
}

