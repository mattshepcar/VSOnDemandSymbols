namespace VSOnDemandSymbols.Core.Modules;

/// <summary>
/// MSBuild C++ project <c>ConfigurationType</c> values (MODULE_RESOLUTION.md §6).
/// </summary>
public enum MsBuildConfigurationType
{
    Unknown,
    Application,
    DynamicLibrary,
    StaticLibrary,
    Makefile
}

