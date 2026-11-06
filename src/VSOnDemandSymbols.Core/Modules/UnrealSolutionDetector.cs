using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VSOnDemandSymbols.Core.Abstractions;
using VSOnDemandSymbols.Core.Policy;

namespace VSOnDemandSymbols.Core.Modules;

/// <summary>
/// Detects whether a Visual Studio solution is an Unreal Engine solution (SPEC §7.2)
/// and evaluates process qualification patterns (SPEC §7.3, §8.5).
/// </summary>
public sealed class UnrealSolutionDetector
{
    public const string DefaultUnrealProcessPatterns = "UnrealEditor*.exe;UE4Editor*.exe";

    private readonly IFileSystem _fileSystem;
    private readonly UnrealOwnerDetector _ownerDetector;

    public UnrealSolutionDetector(IFileSystem fileSystem, UnrealOwnerDetector? ownerDetector = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _ownerDetector = ownerDetector ?? new UnrealOwnerDetector(_fileSystem);
    }

    /// <summary>
    /// Evaluates SPEC §7.2 rules to determine whether a solution is an Unreal solution,
    /// and locates its EngineRoot and ProjectRoot.
    /// </summary>
    public UnrealSolutionInfo Detect(
        string? solutionFilePath,
        IEnumerable<string>? projectFilePaths = null,
        bool? forceUnrealMode = null)
    {
        string? engineRoot = null;
        string? projectRoot = null;
        string? uprojectPath = null;
        bool hasIntermediateProjectWithEngineAncestor = false;

        // 1. Check solution directory
        if (!string.IsNullOrWhiteSpace(solutionFilePath))
        {
            string? solutionDir = Path.GetDirectoryName(solutionFilePath);
            if (!string.IsNullOrEmpty(solutionDir) && _fileSystem.DirectoryExists(solutionDir))
            {
                string? foundUproject = TryFindUprojectInDirectory(solutionDir!);
                if (foundUproject != null)
                {
                    projectRoot = solutionDir;
                    uprojectPath = foundUproject;
                }

                string? foundEngine = TryFindEngineRootInAncestors(solutionDir!);
                if (foundEngine != null)
                {
                    engineRoot = foundEngine;
                }
            }
        }

        // 2. Check project directories and Intermediate/ProjectFiles ancestors
        if (projectFilePaths != null)
        {
            foreach (string rawProjectPath in projectFilePaths)
            {
                if (string.IsNullOrWhiteSpace(rawProjectPath))
                {
                    continue;
                }

                string? projectDir = Path.GetDirectoryName(rawProjectPath);
                if (string.IsNullOrEmpty(projectDir))
                {
                    continue;
                }

                if (_fileSystem.DirectoryExists(projectDir))
                {
                    if (projectRoot == null)
                    {
                        string? foundUproject = TryFindUprojectInDirectory(projectDir!);
                        if (foundUproject != null)
                        {
                            projectRoot = projectDir;
                            uprojectPath = foundUproject;
                        }
                    }
                }

                if (IsUnderIntermediateProjectFiles(rawProjectPath))
                {
                    InspectAncestorsOfIntermediateProject(
                        projectDir!,
                        ref projectRoot,
                        ref uprojectPath,
                        ref engineRoot,
                        ref hasIntermediateProjectWithEngineAncestor);
                }
            }
        }

        // 3. If projectRoot was found but engineRoot was not yet found, check ancestors of projectRoot
        if (projectRoot != null && engineRoot == null)
        {
            engineRoot = TryFindEngineRootInAncestors(projectRoot);
        }

        string? projectName = !string.IsNullOrEmpty(uprojectPath)
            ? Path.GetFileNameWithoutExtension(uprojectPath)
            : null;

        bool detectedUnreal = (projectRoot != null) || hasIntermediateProjectWithEngineAncestor;
        bool isUnreal = forceUnrealMode ?? detectedUnreal;

        if (!isUnreal && engineRoot == null && projectRoot == null)
        {
            return UnrealSolutionInfo.NotUnreal;
        }

        return new UnrealSolutionInfo(isUnreal, engineRoot, projectRoot, uprojectPath, projectName);
    }

    /// <summary>
    /// Builds the effective wildcard patterns for qualifying Unreal processes (SPEC §7.3, §8.5).
    /// Appends <c>&lt;ProjectName&gt;*.exe</c> automatically when a project name is present.
    /// </summary>
    public static IReadOnlyList<WildcardPattern> GetProcessPatterns(
        string? configuredPatterns = null,
        string? projectName = null)
    {
        string raw = string.IsNullOrWhiteSpace(configuredPatterns)
            ? DefaultUnrealProcessPatterns
            : configuredPatterns!;

        if (!string.IsNullOrWhiteSpace(projectName))
        {
            raw = $"{raw};{projectName!.Trim()}*.exe";
        }

        return WildcardPattern.ParseMultiple(raw);
    }

    /// <summary>
    /// Returns true if the given process path or executable name matches the Unreal process patterns (SPEC §7.3).
    /// </summary>
    public static bool IsQualifyingUnrealProcess(
        string processNameOrPath,
        string? configuredPatterns = null,
        string? projectName = null)
    {
        string cleanFileName = WildcardPattern.ExtractFileNameSafe(processNameOrPath);
        if (string.IsNullOrWhiteSpace(cleanFileName))
        {
            return false;
        }

        var patterns = GetProcessPatterns(configuredPatterns, projectName);
        return WildcardPattern.MatchesAnyFileName(patterns, cleanFileName);
    }

    private void InspectAncestorsOfIntermediateProject(
        string startDir,
        ref string? projectRoot,
        ref string? uprojectPath,
        ref string? engineRoot,
        ref bool hasEngineAncestor)
    {
        string? current = startDir;
        while (!string.IsNullOrEmpty(current))
        {
            if (projectRoot == null && _fileSystem.DirectoryExists(current))
            {
                string? foundUproject = TryFindUprojectInDirectory(current);
                if (foundUproject != null)
                {
                    projectRoot = current;
                    uprojectPath = foundUproject;
                }
            }

            if (_ownerDetector.IsEngineRoot(current, out string resolvedEngineRoot, out _))
            {
                engineRoot ??= resolvedEngineRoot;
                hasEngineAncestor = true;
            }

            string? parent = Path.GetDirectoryName(current);
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }
    }

    private string? TryFindEngineRootInAncestors(string startDir)
    {
        string? current = startDir;
        while (!string.IsNullOrEmpty(current))
        {
            if (_ownerDetector.IsEngineRoot(current, out string resolvedEngineRoot, out _))
            {
                return resolvedEngineRoot;
            }

            string? parent = Path.GetDirectoryName(current);
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }

        return null;
    }

    private string? TryFindUprojectInDirectory(string directory)
    {
        try
        {
            return _fileSystem
                .EnumerateFiles(directory, "*.uproject", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsUnderIntermediateProjectFiles(string path)
    {
        string normalized = path.Replace('/', '\\');
        return normalized.IndexOf(@"\Intermediate\ProjectFiles\", StringComparison.OrdinalIgnoreCase) >= 0 ||
               normalized.EndsWith(@"\Intermediate\ProjectFiles", StringComparison.OrdinalIgnoreCase);
    }
}

