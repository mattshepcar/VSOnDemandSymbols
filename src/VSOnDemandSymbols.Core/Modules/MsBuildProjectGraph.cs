using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VSOnDemandSymbols.Core.Abstractions;

namespace VSOnDemandSymbols.Core.Modules;

/// <summary>
/// Resolves source files to MSBuild output binaries (<c>.dll</c> / <c>.exe</c>) across a graph of
/// <see cref="MsBuildProjectInfo"/> instances, following transitive reverse <c>ProjectReference</c>s
/// from static libraries and ignoring <c>Makefile</c>/NMake projects (SPEC §6.3, MODULE_RESOLUTION.md §6).
/// </summary>
public sealed class MsBuildProjectGraph : IProjectModelProvider
{
    public static readonly MsBuildProjectGraph Empty = new(Array.Empty<MsBuildProjectInfo>());

    private readonly List<MsBuildProjectInfo> _projects;
    private readonly Dictionary<string, MsBuildProjectInfo> _projectsByPath;
    private readonly Dictionary<string, List<MsBuildProjectInfo>> _reverseReferences;

    public IReadOnlyList<MsBuildProjectInfo> Projects => _projects;

    public MsBuildProjectGraph(IEnumerable<MsBuildProjectInfo>? projects)
    {
        _projects = new List<MsBuildProjectInfo>();
        _projectsByPath = new Dictionary<string, MsBuildProjectInfo>(StringComparer.OrdinalIgnoreCase);
        _reverseReferences = new Dictionary<string, List<MsBuildProjectInfo>>(StringComparer.OrdinalIgnoreCase);

        if (projects != null)
        {
            foreach (var proj in projects)
            {
                // MODULE_RESOLUTION.md §6.4: Ignore Makefile/NMake projects (Unreal's generated projects are NMake).
                if (proj == null ||
                    proj.ConfigurationType == MsBuildConfigurationType.Makefile ||
                    proj.ConfigurationType == MsBuildConfigurationType.Unknown)
                {
                    continue;
                }

                _projects.Add(proj);
                _projectsByPath[proj.ProjectFilePath] = proj;
            }
        }

        // Build reverse reference graph: referencedProjectPath -> list of projects that reference it
        foreach (var proj in _projects)
        {
            foreach (string refPath in proj.ReferencedProjectPaths)
            {
                if (!_reverseReferences.TryGetValue(refPath, out var dependents))
                {
                    dependents = new List<MsBuildProjectInfo>();
                    _reverseReferences[refPath] = dependents;
                }

                dependents.Add(proj);
            }
        }
    }

    public static MsBuildProjectGraph FromProjectFiles(
        IEnumerable<string> vcxprojPaths,
        IFileSystem fileSystem,
        string? activeConfiguration = null,
        string? activePlatform = null)
    {
        if (vcxprojPaths == null)
        {
            return Empty;
        }

        var infos = new List<MsBuildProjectInfo>();
        foreach (string path in vcxprojPaths)
        {
            var info = MsBuildProjectInfo.TryParseVcxproj(path, fileSystem, activeConfiguration, activePlatform);
            if (info != null)
            {
                infos.Add(info);
            }
        }

        return new MsBuildProjectGraph(infos);
    }

    public IReadOnlyList<string> ResolveSourceFileToOutputs(string sourceFilePath)
    {
        string normalizedSource = MsBuildProjectInfo.NormalizePath(sourceFilePath);
        if (string.IsNullOrEmpty(normalizedSource) || _projects.Count == 0)
        {
            return Array.Empty<string>();
        }

        // Step 1 (SPEC §6.3.1): Find all projects in the solution that explicitly contain the file.
        var owningProjects = new List<MsBuildProjectInfo>();
        foreach (var proj in _projects)
        {
            if (proj.SourceFiles.Contains(normalizedSource))
            {
                owningProjects.Add(proj);
            }
        }

        // Step 2 (SPEC §6.3.4): Files that aren't in any project: try the nearest project folder above the file.
        if (owningProjects.Count == 0)
        {
            owningProjects.AddRange(FindNearestAncestorProjects(normalizedSource));
        }

        if (owningProjects.Count == 0)
        {
            return Array.Empty<string>();
        }

        // Step 3 (SPEC §6.3.2 & §6.3.3): Collect DLL/EXE outputs, following reverse project references for static libraries.
        var outputs = new List<string>();
        var seenOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<MsBuildProjectInfo>(owningProjects);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visitedProjects.Add(current.ProjectFilePath))
            {
                continue;
            }

            if (current.ConfigurationType == MsBuildConfigurationType.Application ||
                current.ConfigurationType == MsBuildConfigurationType.DynamicLibrary)
            {
                if (!string.IsNullOrWhiteSpace(current.TargetFileName) &&
                    seenOutputs.Add(current.TargetFileName))
                {
                    outputs.Add(current.TargetFileName);
                }
            }
            else if (current.ConfigurationType == MsBuildConfigurationType.StaticLibrary)
            {
                if (_reverseReferences.TryGetValue(current.ProjectFilePath, out var dependents))
                {
                    foreach (var dependent in dependents)
                    {
                        if (!visitedProjects.Contains(dependent.ProjectFilePath))
                        {
                            queue.Enqueue(dependent);
                        }
                    }
                }
            }
        }

        return outputs;
    }

    public IReadOnlyCollection<string> GetAllKnownBinaryFileNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var proj in _projects)
        {
            if ((proj.ConfigurationType == MsBuildConfigurationType.Application ||
                 proj.ConfigurationType == MsBuildConfigurationType.DynamicLibrary) &&
                !string.IsNullOrWhiteSpace(proj.TargetFileName))
            {
                names.Add(proj.TargetFileName);
            }
        }

        return names;
    }

    private List<MsBuildProjectInfo> FindNearestAncestorProjects(string normalizedSourcePath)
    {
        var result = new List<MsBuildProjectInfo>();
        string? sourceDir = Path.GetDirectoryName(normalizedSourcePath);
        if (string.IsNullOrEmpty(sourceDir))
        {
            return result;
        }

        int bestDepth = -1;
        foreach (var proj in _projects)
        {
            if (string.IsNullOrEmpty(proj.ProjectDirectory))
            {
                continue;
            }

            if (IsSameOrSubDirectory(sourceDir!, proj.ProjectDirectory))
            {
                int depth = proj.ProjectDirectory.Length;
                if (depth > bestDepth)
                {
                    bestDepth = depth;
                    result.Clear();
                    result.Add(proj);
                }
                else if (depth == bestDepth)
                {
                    result.Add(proj);
                }
            }
        }

        return result;
    }

    private static bool IsSameOrSubDirectory(string candidateDir, string ancestorDir)
    {
        if (string.Equals(candidateDir, ancestorDir, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string prefix = ancestorDir.EndsWith("\\", StringComparison.Ordinal)
            ? ancestorDir
            : ancestorDir + "\\";
        return candidateDir.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}

