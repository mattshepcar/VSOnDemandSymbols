using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VSOnDemandSymbols.Core.Abstractions;

namespace VSOnDemandSymbols.Core.Modules;

/// <summary>
/// Resolves C++ source files to Unreal Engine module names and candidate binary file names
/// (SPEC.md §4.1).
/// </summary>
public sealed class UnrealModuleResolver
{
    private static readonly Regex IntermediateIncRegex = new(
        @"[/\\]Intermediate[/\\]Build[/\\][^/\\]+[/\\](?:[^/\\]+[/\\])?(?:Inc[/\\](?:.*[/\\])?(?<module>[^/\\]+)[/\\]UHT[/\\]|Inc[/\\](?<module>[^/\\]+)[/\\])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex IntermediateUnityRegex = new(
        @"[/\\]Intermediate[/\\]Build[/\\][^/\\]+[/\\](?:[^/\\]+[/\\])*(?:Module\.(?<module>[A-Za-z0-9_]+)(?:\.\d+(?:_of_\d+)?)?\.cpp)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly IFileSystem _fileSystem;
    private readonly ConcurrentDictionary<string, string?> _dirToModuleCache = new(StringComparer.OrdinalIgnoreCase);

    public UnrealModuleResolver(IFileSystem? fileSystem = null)
    {
        _fileSystem = fileSystem ?? PhysicalFileSystem.Instance;
    }

    /// <summary>
    /// Resolves a source file path to its owning Unreal module name by walking up to <c>*.Build.cs</c>
    /// or parsing intermediate UHT / unity paths.
    /// </summary>
    public string? ResolveModuleFromSource(string sourceFilePath)
    {
        if (string.IsNullOrWhiteSpace(sourceFilePath))
        {
            return null;
        }

        sourceFilePath = Policy.WildcardPattern.SanitizePathString(sourceFilePath);

        // 1. Special case: Intermediate UHT and unity files
        if (sourceFilePath.IndexOf("Intermediate", StringComparison.OrdinalIgnoreCase) >= 0 &&
            sourceFilePath.IndexOf("Build", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var incMatch = IntermediateIncRegex.Match(sourceFilePath);
            if (incMatch.Success)
            {
                return incMatch.Groups["module"].Value;
            }

            var unityMatch = IntermediateUnityRegex.Match(sourceFilePath);
            if (unityMatch.Success)
            {
                return unityMatch.Groups["module"].Value;
            }
        }

        // 2. Walk up directory tree to find *.Build.cs
        string? dir = Path.GetDirectoryName(sourceFilePath);
        while (!string.IsNullOrEmpty(dir))
        {
            if (_dirToModuleCache.TryGetValue(dir, out string? cachedModule))
            {
                return cachedModule;
            }

            // Boundary check: stop at project, plugin, or engine root
            if (IsBoundaryDirectory(dir))
            {
                string? modAtBoundary = TryFindModuleInDirectory(dir);
                _dirToModuleCache[dir] = modAtBoundary;
                return modAtBoundary;
            }

            string? foundModule = TryFindModuleInDirectory(dir);
            if (foundModule != null)
            {
                _dirToModuleCache[dir] = foundModule;
                return foundModule;
            }

            string? parent = Path.GetDirectoryName(dir);
            if (string.Equals(parent, dir, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            dir = parent;
        }

        return null;
    }

    /// <summary>
    /// Checks whether the specified solution directory belongs to an Unreal Engine project or engine source.
    /// </summary>
    public bool IsUnrealSolution(string solutionDir)
    {
        if (string.IsNullOrWhiteSpace(solutionDir))
        {
            return false;
        }

        try
        {
            string? current = solutionDir;
            while (!string.IsNullOrEmpty(current))
            {
                if (_fileSystem.EnumerateFiles(current, "*.uproject", SearchOption.TopDirectoryOnly).Any() ||
                    _fileSystem.FileExists(Path.Combine(current, "Engine", "Build", "Build.version")))
                {
                    return true;
                }

                string? parent = Path.GetDirectoryName(current);
                if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                current = parent;
            }
        }
        catch
        {
            // Ignore file lookup errors
        }

        return false;
    }

    /// <summary>
    /// Returns candidate binary names for an Unreal module name.
    /// Uses a wildcard pattern (<c>UnrealEditor-{moduleName}*.dll</c>) to match all build configurations
    /// (undecorated Development, DebugGame, Debug, Shipping, hot reload, etc.) with a single clean wildcard entry.
    /// </summary>
    public IReadOnlyList<string> GetCandidateBinaries(string moduleName)
    {
        if (string.IsNullOrWhiteSpace(moduleName))
        {
            return Array.Empty<string>();
        }

        return new[]
        {
            $"UnrealEditor-{moduleName}*.dll"
        };
    }

    private bool IsBoundaryDirectory(string dir)
    {
        try
        {
            if (_fileSystem.DirectoryExists(Path.Combine(dir, "Engine", "Source")))
            {
                return true;
            }

            if (_fileSystem.EnumerateFiles(dir, "*.uproject", SearchOption.TopDirectoryOnly).Any() ||
                _fileSystem.EnumerateFiles(dir, "*.uplugin", SearchOption.TopDirectoryOnly).Any())
            {
                return true;
            }
        }
        catch
        {
            // Ignore boundary lookup errors
        }

        return false;
    }

    private string? TryFindModuleInDirectory(string dir)
    {
        try
        {
            foreach (string file in _fileSystem.EnumerateFiles(dir, "*.Build.cs", SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileName(file);
                if (fileName.EndsWith(".Build.cs", StringComparison.OrdinalIgnoreCase))
                {
                    return fileName.Substring(0, fileName.Length - ".Build.cs".Length);
                }
            }
        }
        catch
        {
            // Ignore directory search errors
        }

        return null;
    }
}
