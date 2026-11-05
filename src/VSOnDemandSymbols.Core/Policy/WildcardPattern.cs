using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VSOnDemandSymbols.Core.Modules;

namespace VSOnDemandSymbols.Core.Policy;

public sealed class WildcardPattern
{
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

        string fileName = Path.GetFileName(fileNameOrPath);
        return _regex.IsMatch(fileName);
    }

    public bool IsMatchModule(ModuleKey key, IEnumerable<string>? candidateFileNames = null)
    {
        if (IsUnrealModulePattern)
        {
            if (key.Kind != ModuleKind.Unreal) return false;
            return _regex.IsMatch(key.Name);
        }

        if (candidateFileNames != null)
        {
            foreach (var fn in candidateFileNames)
            {
                if (IsMatchFileName(fn)) return true;
            }
        }

        // If no candidate filenames provided or it's MsBuild, also try the key name as a filename
        if (key.Kind == ModuleKind.MsBuild && IsMatchFileName(key.Name))
        {
            return true;
        }

        return false;
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

    public static bool MatchesAnyFileName(IEnumerable<WildcardPattern> patterns, string fileNameOrPath)
    {
        foreach (var p in patterns)
        {
            if (p.IsMatchFileName(fileNameOrPath)) return true;
        }
        return false;
    }

    public static bool MatchesAnyModule(IEnumerable<WildcardPattern> patterns, ModuleKey key, IEnumerable<string>? candidateFileNames = null)
    {
        foreach (var p in patterns)
        {
            if (p.IsMatchModule(key, candidateFileNames)) return true;
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

