using System;

namespace VSOnDemandSymbols.Core.Policy;

/// <summary>
/// Controls symbol loading behavior when execution breaks or the thread context changes.
/// </summary>
public enum BreakSymbolLoadMode
{
    /// <summary>
    /// Do not automatically load symbols when execution breaks.
    /// </summary>
    Off = 0,

    /// <summary>
    /// Automatically load locally available symbols (adjacent PDBs, local symbol paths, and local symbol cache)
    /// without querying remote/internet symbol servers (default).
    /// </summary>
    LocalOnly = 1,

    /// <summary>
    /// Automatically load symbols for call stacks and threads including querying configured remote symbol servers.
    /// </summary>
    All = 2
}

/// <summary>
/// Helper extensions for parsing <see cref="BreakSymbolLoadMode"/>.
/// </summary>
public static class BreakSymbolLoadModeExtensions
{
    /// <summary>
    /// Parses a string into a <see cref="BreakSymbolLoadMode"/>, falling back to <paramref name="defaultValue"/>
    /// if null, empty, or unparseable.
    /// </summary>
    public static BreakSymbolLoadMode ParseOrDefault(string? value, BreakSymbolLoadMode defaultValue = BreakSymbolLoadMode.LocalOnly)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (Enum.TryParse<BreakSymbolLoadMode>(value!.Trim(), ignoreCase: true, out var parsed) &&
            Enum.IsDefined(typeof(BreakSymbolLoadMode), parsed))
        {
            return parsed;
        }

        return defaultValue;
    }
}
