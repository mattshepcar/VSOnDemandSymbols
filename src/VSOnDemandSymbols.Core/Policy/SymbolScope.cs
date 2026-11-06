namespace VSOnDemandSymbols.Core.Policy;

/// <summary>
/// Controls which solutions the extension acts on (SPEC §7.1, §8.5).
/// </summary>
public enum SymbolScope
{
    /// <summary>
    /// The extension never changes symbol settings.
    /// </summary>
    Off,

    /// <summary>
    /// Acts only when the open solution is an Unreal solution (SPEC §7.2). Default.
    /// </summary>
    UnrealOnly,

    /// <summary>
    /// Acts for any solution that contains C++ projects. Unreal logic switches on automatically for Unreal solutions.
    /// </summary>
    AllNative
}

