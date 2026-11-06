using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VSOnDemandSymbols.Core.Modules;

namespace VSOnDemandSymbols.Core.Policy;

public sealed class WildcardPattern
{
    private static readonly char[] PathSeparators = { '\\', '/' };
    private readonly Regex _regex;

    public string RawPattern { get; }
    public bool IsUnrealModulePattern { get; }
    public string Pattern { get; }

    public WildcardPattern(string rawPattern)
    {
        if (string.IsNullOrWhiteSpace(rawPattern))
        {
            throw new ArgumentException("Pattern cannot be empty or whitespace.", nameof(rawPattern));
        }

        RawPattern = rawPattern.Trim();
        if (RawPattern.StartsWith("ue:", StringComparison.OrdinalIgnoreCase))
        {
            IsUnrealModulePattern = true;
            Pattern = RawPattern.Substring(3).Trim();
        }
        else
        {
            IsUnrealModulePattern = false;
            Pattern = RawPattern;
        }

        _regex = BuildRegex(Pattern);
    }

    public bool IsMatchFileName(string fileNameOrPath)
    {
        if (string.IsNullOrEmpty(fileNameOrPath)) return false;
        if (IsUnrealModulePattern) return false;

        string fileName = ExtractFileNameSafe(fileNameOrPath);
        if (fileName.Length == 0) return false;
        return _regex.IsMatch(fileName);
    }

    /// <summary>
    /// Strips any embedded NUL characters (e.g. from Concord <c>DkmProcess.Path</c> buffers) and trims whitespace.
    /// Never throws on invalid path characters.
    /// </summary>
    public static string SanitizePathString(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        int nullIndex = value!.IndexOf('\0');
        if (nullIndex >= 0)
        {
            value = value.Substring(0, nullIndex);
        }

        return value.Trim();
    }

    /// <summary>
    /// Extracts the file name segment from a path or file name without calling <see cref="Path.GetFileName(string)"/>
    /// (which throws <see cref="ArgumentException"/> in .NET Framework when the string contains <c>\0</c> or other invalid characters).
    /// </summary>
    public static string ExtractFileNameSafe(string? fileNameOrPath)
    {
        string clean = SanitizePathString(fileNameOrPath);
        if (clean.Length == 0)
        {
            return string.Empty;
        }

        int lastSep = clean.LastIndexOfAny(PathSeparators);
        return lastSep >= 0 ? clean.Substring(lastSep + 1) : clean;
    }

    /// <summary>
    /// Normalizes a raw stack frame module string (which may contain address offsets, bang suffixes,
    /// or lack the .dll extension) into a candidate binary file name.
    /// Returns null for external code, unknown symbols, or invalid input.
    /// </summary>
    public static string? NormalizeFrameModuleName(string? rawModuleName)
    {
        if (string.IsNullOrWhiteSpace(rawModuleName))
        {
            return null;
        }

        string trimmed = rawModuleName!.Trim();
        if (trimmed.StartsWith("[", StringComparison.Ordinal) || trimmed.StartsWith("<", StringComparison.Ordinal))
        {
            return null;
        }

        int bangIdx = trimmed.IndexOf('!');
        if (bangIdx >= 0)
        {
            trimmed = trimmed.Substring(0, bangIdx).Trim();
        }

        string fileName = ExtractFileNameSafe(trimmed);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        if (fileName.IndexOf('.') < 0)
        {
            return fileName + ".dll";
        }

        return fileName;
    }

    public bool IsMatchModule(string moduleName, IEnumerable<string>? candidateFileNames = null)
    {
        if (IsUnrealModulePattern)
        {
            return _regex.IsMatch(moduleName);
        }

        if (candidateFileNames != null)
        {
            foreach (var fn in candidateFileNames)
            {
                if (IsMatchFileName(fn)) return true;
            }
        }

        return IsMatchFileName(moduleName);
    }

    public static IReadOnlyList<WildcardPattern> ParseMultiple(string? semicolonSeparatedPatterns)
    {
        if (string.IsNullOrWhiteSpace(semicolonSeparatedPatterns))
        {
            return Array.Empty<WildcardPattern>();
        }

        var list = new List<WildcardPattern>();
        var parts = semicolonSeparatedPatterns!.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                list.Add(new WildcardPattern(trimmed));
            }
        }

        return list;
    }

    public static bool MatchesFileName(string rawPattern, string fileNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(rawPattern) || string.IsNullOrEmpty(fileNameOrPath))
        {
            return false;
        }

        return new WildcardPattern(rawPattern).IsMatchFileName(fileNameOrPath);
    }

    public static bool MatchesAnyFileName(IEnumerable<WildcardPattern> patterns, string fileNameOrPath)
    {
        foreach (var p in patterns)
        {
            if (p.IsMatchFileName(fileNameOrPath)) return true;
        }
        return false;
    }

    public static bool MatchesAnyModule(IEnumerable<WildcardPattern> patterns, string moduleName, IEnumerable<string>? candidateFileNames = null)
    {
        foreach (var p in patterns)
        {
            if (p.IsMatchModule(moduleName, candidateFileNames)) return true;
        }
        return false;
    }

    private static Regex BuildRegex(string pattern)
    {
        string escaped = Regex.Escape(pattern);
        string regexPattern = "^" + escaped.Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public override string ToString() => RawPattern;
}

