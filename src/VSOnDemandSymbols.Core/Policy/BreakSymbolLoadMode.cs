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
/// Helper extensions for parsing and reading <see cref="BreakSymbolLoadMode"/>.
/// </summary>
public static class BreakSymbolLoadModeExtensions
{
    public const string EnvironmentVariableName = "ODS_BREAK_SYMBOL_LOAD_MODE";

    /// <summary>
    /// Reads <see cref="BreakSymbolLoadMode"/> from the <c>ODS_BREAK_SYMBOL_LOAD_MODE</c> environment variable,
    /// falling back to <paramref name="defaultValue"/> if not set or invalid.
    /// </summary>
    public static BreakSymbolLoadMode FromEnvironment(BreakSymbolLoadMode defaultValue = BreakSymbolLoadMode.LocalOnly)
    {
        try
        {
            string? val = Environment.GetEnvironmentVariable(EnvironmentVariableName);
            return ParseOrDefault(val, defaultValue);
        }
        catch
        {
            return defaultValue;
        }
    }

    /// <summary>
    /// Parses a string into a <see cref="BreakSymbolLoadMode"/>, falling back to <paramref name="defaultValue"/>
    /// if null, empty, or unparseable. Supports numeric values, enum names, and common aliases.
    /// </summary>
    public static BreakSymbolLoadMode ParseOrDefault(string? value, BreakSymbolLoadMode defaultValue = BreakSymbolLoadMode.LocalOnly)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        value = value!.Trim();
        if (Enum.TryParse<BreakSymbolLoadMode>(value, ignoreCase: true, out var parsed) &&
            Enum.IsDefined(typeof(BreakSymbolLoadMode), parsed))
        {
            return parsed;
        }

        if (string.Equals(value, "none", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "disabled", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
        {
            return BreakSymbolLoadMode.Off;
        }

        if (string.Equals(value, "local", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "localonly", StringComparison.OrdinalIgnoreCase))
        {
            return BreakSymbolLoadMode.LocalOnly;
        }

        if (string.Equals(value, "remote", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "network", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "all", StringComparison.OrdinalIgnoreCase))
        {
            return BreakSymbolLoadMode.All;
        }

        return defaultValue;
    }
}
