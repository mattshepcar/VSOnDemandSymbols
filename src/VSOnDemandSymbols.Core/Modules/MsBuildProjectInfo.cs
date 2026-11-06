using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using VSOnDemandSymbols.Core.Abstractions;
using VSOnDemandSymbols.Core.Policy;

namespace VSOnDemandSymbols.Core.Modules;

/// <summary>
/// Immutable snapshot of a single C++ MSBuild (<c>.vcxproj</c>) project for source-to-module resolution (SPEC §6.3, MODULE_RESOLUTION.md §6).
/// </summary>
public sealed class MsBuildProjectInfo
{
    public string ProjectFilePath { get; }
    public string ProjectDirectory { get; }
    public MsBuildConfigurationType ConfigurationType { get; }
    public string TargetFileName { get; }
    public IReadOnlyCollection<string> SourceFiles { get; }
    public IReadOnlyList<string> ReferencedProjectPaths { get; }

    public MsBuildProjectInfo(
        string projectFilePath,
        MsBuildConfigurationType configurationType,
        string targetFileName,
        IEnumerable<string>? sourceFiles = null,
        IEnumerable<string>? referencedProjectPaths = null)
    {
        if (string.IsNullOrWhiteSpace(projectFilePath))
        {
            throw new ArgumentException("Project file path cannot be empty.", nameof(projectFilePath));
        }

        ProjectFilePath = NormalizePath(projectFilePath);
        ProjectDirectory = Path.GetDirectoryName(ProjectFilePath) ?? string.Empty;
        ConfigurationType = configurationType;
        TargetFileName = WildcardPattern.ExtractFileNameSafe(targetFileName);

        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (sourceFiles != null)
        {
            foreach (string src in sourceFiles)
            {
                string norm = NormalizePath(src, ProjectDirectory);
                if (!string.IsNullOrEmpty(norm))
                {
                    sources.Add(norm);
                }
            }
        }
        SourceFiles = sources;

        var refs = new List<string>();
        if (referencedProjectPaths != null)
        {
            foreach (string r in referencedProjectPaths)
            {
                string norm = NormalizePath(r, ProjectDirectory);
                if (!string.IsNullOrEmpty(norm) && !refs.Contains(norm, StringComparer.OrdinalIgnoreCase))
                {
                    refs.Add(norm);
                }
            }
        }
        ReferencedProjectPaths = refs;
    }

    /// <summary>
    /// Parses a <c>.vcxproj</c> XML file using <see cref="IFileSystem"/> into an <see cref="MsBuildProjectInfo"/>.
    /// </summary>
    public static MsBuildProjectInfo? TryParseVcxproj(
        string vcxprojPath,
        IFileSystem fileSystem,
        string? activeConfiguration = null,
        string? activePlatform = null,
        string? overrideTargetFileName = null,
        MsBuildConfigurationType? overrideConfigType = null)
    {
        if (fileSystem == null)
        {
            throw new ArgumentNullException(nameof(fileSystem));
        }

        string cleanPath = NormalizePath(vcxprojPath);
        if (string.IsNullOrEmpty(cleanPath) ||
            !cleanPath.EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase) ||
            !fileSystem.FileExists(cleanPath))
        {
            return null;
        }

        try
        {
            string xml = fileSystem.ReadAllText(cleanPath);
            var doc = XDocument.Parse(xml);
            XElement? root = doc.Root;
            if (root == null)
            {
                return null;
            }

            string projectDir = Path.GetDirectoryName(cleanPath) ?? string.Empty;
            string projectName = Path.GetFileNameWithoutExtension(cleanPath);

            string? conditionToken = !string.IsNullOrWhiteSpace(activeConfiguration) && !string.IsNullOrWhiteSpace(activePlatform)
                ? $"{activeConfiguration}|{activePlatform}"
                : activeConfiguration;

            MsBuildConfigurationType configType = overrideConfigType ?? ParseConfigurationType(root, conditionToken);
            string targetFileName = !string.IsNullOrWhiteSpace(overrideTargetFileName)
                ? WildcardPattern.ExtractFileNameSafe(overrideTargetFileName)
                : ResolveTargetFileName(root, projectName, configType, conditionToken);

            var sourceFiles = new List<string>();
            foreach (var elem in root.Descendants().Where(e =>
                string.Equals(e.Name.LocalName, "ClCompile", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.Name.LocalName, "ClInclude", StringComparison.OrdinalIgnoreCase)))
            {
                string? include = elem.Attribute("Include")?.Value;
                if (!string.IsNullOrWhiteSpace(include))
                {
                    sourceFiles.Add(NormalizePath(include!, projectDir));
                }
            }

            // Also check .vcxproj.filters if present in case additional files are listed there
            string filtersPath = cleanPath + ".filters";
            if (fileSystem.FileExists(filtersPath))
            {
                try
                {
                    var filtersDoc = XDocument.Parse(fileSystem.ReadAllText(filtersPath));
                    if (filtersDoc.Root != null)
                    {
                        foreach (var elem in filtersDoc.Root.Descendants().Where(e =>
                            string.Equals(e.Name.LocalName, "ClCompile", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(e.Name.LocalName, "ClInclude", StringComparison.OrdinalIgnoreCase)))
                        {
                            string? include = elem.Attribute("Include")?.Value;
                            if (!string.IsNullOrWhiteSpace(include))
                            {
                                sourceFiles.Add(NormalizePath(include!, projectDir));
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore malformed .filters file
                }
            }

            var referencedProjects = new List<string>();
            foreach (var elem in root.Descendants().Where(e =>
                string.Equals(e.Name.LocalName, "ProjectReference", StringComparison.OrdinalIgnoreCase)))
            {
                string? include = elem.Attribute("Include")?.Value;
                if (!string.IsNullOrWhiteSpace(include))
                {
                    referencedProjects.Add(NormalizePath(include!, projectDir));
                }
            }

            return new MsBuildProjectInfo(
                cleanPath,
                configType,
                targetFileName,
                sourceFiles,
                referencedProjects);
        }
        catch
        {
            return null;
        }
    }

    public static string NormalizePath(string? path, string? baseDirectory = null)
    {
        string clean = WildcardPattern.SanitizePathString(path);
        if (string.IsNullOrEmpty(clean))
        {
            return string.Empty;
        }

        clean = clean.Replace('/', '\\');
        try
        {
            if (!Path.IsPathRooted(clean) && !string.IsNullOrWhiteSpace(baseDirectory))
            {
                clean = Path.Combine(baseDirectory!, clean);
            }

            if (Path.IsPathRooted(clean))
            {
                clean = Path.GetFullPath(clean);
            }
        }
        catch
        {
            // Keep sanitized string if Path.GetFullPath fails on synthetic paths
        }

        return clean.TrimEnd('\\');
    }

    private static MsBuildConfigurationType ParseConfigurationType(XElement root, string? conditionToken)
    {
        string? matchedValue = FindPropertyValue(root, "ConfigurationType", conditionToken);
        if (string.IsNullOrWhiteSpace(matchedValue))
        {
            return MsBuildConfigurationType.Application;
        }

        return matchedValue!.Trim() switch
        {
            var s when string.Equals(s, "Application", StringComparison.OrdinalIgnoreCase) => MsBuildConfigurationType.Application,
            var s when string.Equals(s, "DynamicLibrary", StringComparison.OrdinalIgnoreCase) => MsBuildConfigurationType.DynamicLibrary,
            var s when string.Equals(s, "StaticLibrary", StringComparison.OrdinalIgnoreCase) => MsBuildConfigurationType.StaticLibrary,
            var s when string.Equals(s, "Makefile", StringComparison.OrdinalIgnoreCase) => MsBuildConfigurationType.Makefile,
            var s when string.Equals(s, "Utility", StringComparison.OrdinalIgnoreCase) => MsBuildConfigurationType.Makefile,
            _ => MsBuildConfigurationType.Unknown
        };
    }

    private static string ResolveTargetFileName(
        XElement root,
        string projectName,
        MsBuildConfigurationType configType,
        string? conditionToken)
    {
        string? rawTargetName = FindPropertyValue(root, "TargetName", conditionToken);
        string targetName = projectName;
        if (!string.IsNullOrWhiteSpace(rawTargetName))
        {
            targetName = rawTargetName!
                .Trim()
                .Replace("$(ProjectName)", projectName)
                .Replace("$(MSBuildProjectName)", projectName);
        }

        string? rawTargetExt = FindPropertyValue(root, "TargetExt", conditionToken);
        string targetExt = !string.IsNullOrWhiteSpace(rawTargetExt)
            ? rawTargetExt!.Trim()
            : configType switch
            {
                MsBuildConfigurationType.DynamicLibrary => ".dll",
                MsBuildConfigurationType.StaticLibrary => ".lib",
                _ => ".exe"
            };

        if (!targetExt.StartsWith(".", StringComparison.Ordinal))
        {
            targetExt = "." + targetExt;
        }

        if (targetName.EndsWith(targetExt, StringComparison.OrdinalIgnoreCase))
        {
            return targetName;
        }

        return targetName + targetExt;
    }

    private static string? FindPropertyValue(XElement root, string elementLocalName, string? conditionToken)
    {
        string? fallback = null;
        foreach (var propGroup in root.Elements().Where(e =>
            string.Equals(e.Name.LocalName, "PropertyGroup", StringComparison.OrdinalIgnoreCase)))
        {
            var propElem = propGroup.Elements().FirstOrDefault(e =>
                string.Equals(e.Name.LocalName, elementLocalName, StringComparison.OrdinalIgnoreCase));
            if (propElem == null || string.IsNullOrWhiteSpace(propElem.Value))
            {
                continue;
            }

            string? condition = propGroup.Attribute("Condition")?.Value;
            if (!string.IsNullOrWhiteSpace(conditionToken) &&
                !string.IsNullOrWhiteSpace(condition) &&
                condition!.IndexOf(conditionToken!, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return propElem.Value;
            }

            fallback ??= propElem.Value;
        }

        return fallback;
    }
}

