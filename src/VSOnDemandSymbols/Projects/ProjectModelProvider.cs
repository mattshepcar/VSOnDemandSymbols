using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using VSOnDemandSymbols.Core.Abstractions;
using VSOnDemandSymbols.Core.Modules;
using VSOnDemandSymbols.Core.Policy;

namespace VSOnDemandSymbols.Projects;

/// <summary>
/// Exposes solution/project information (<c>TargetPath</c>, project membership, static-library reverse references)
/// to <c>VSOnDemandSymbols.Core</c> through <see cref="IProjectModelProvider"/> (ARCHITECTURE.md §3, MODULE_RESOLUTION.md §6).
/// Snapshots are built on the UI thread and read lock-free from any thread.
/// </summary>
public sealed class ProjectModelProvider : IProjectModelProvider
{
    private readonly IFileSystem _fileSystem;
    private readonly ILogger _logger;
    private MsBuildProjectGraph _currentGraph = MsBuildProjectGraph.Empty;

    public MsBuildProjectGraph CurrentGraph => Volatile.Read(ref _currentGraph);

    public ProjectModelProvider(IFileSystem fileSystem, ILogger logger)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IReadOnlyList<string> ResolveSourceFileToOutputs(string sourceFilePath)
    {
        return CurrentGraph.ResolveSourceFileToOutputs(sourceFilePath);
    }

    public void Clear()
    {
        Volatile.Write(ref _currentGraph, MsBuildProjectGraph.Empty);
    }

    /// <summary>
    /// Builds an immutable <see cref="MsBuildProjectGraph"/> snapshot of all C++ (<c>.vcxproj</c>) projects
    /// in the open Visual Studio solution on the UI thread (MODULE_RESOLUTION.md §6).
    /// </summary>
    public MsBuildProjectGraph RebuildSnapshot(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (dte == null)
        {
            throw new ArgumentNullException(nameof(dte));
        }

        var projectInfos = new List<MsBuildProjectInfo>();
        try
        {
            EnvDTE.Projects? projects = dte.Solution?.Projects;
            if (projects != null)
            {
                foreach (Project project in projects)
                {
                    CollectProjectsRecursive(project, projectInfos);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Failed to enumerate projects for MSBuild model snapshot: {ex.Message}");
        }

        var graph = new MsBuildProjectGraph(projectInfos);
        Volatile.Write(ref _currentGraph, graph);
        return graph;
    }

    private void CollectProjectsRecursive(Project? project, List<MsBuildProjectInfo> projectInfos)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (project == null)
        {
            return;
        }

        try
        {
            if (string.Equals(project.Kind, ProjectKinds.vsProjectKindSolutionFolder, StringComparison.OrdinalIgnoreCase))
            {
                if (project.ProjectItems != null)
                {
                    foreach (ProjectItem item in project.ProjectItems)
                    {
                        if (item?.SubProject != null)
                        {
                            CollectProjectsRecursive(item.SubProject, projectInfos);
                        }
                    }
                }

                return;
            }

            string? fullName = project.FullName;
            if (string.IsNullOrWhiteSpace(fullName) ||
                !fullName!.EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string? activeConfigName = null;
            string? activePlatformName = null;
            try
            {
                Configuration? activeCfg = project.ConfigurationManager?.ActiveConfiguration;
                if (activeCfg != null)
                {
                    activeConfigName = activeCfg.ConfigurationName;
                    activePlatformName = activeCfg.PlatformName;
                }
            }
            catch
            {
                // Ignore active configuration query failure
            }

            TryReadVcProjectProperties(
                project,
                out string? evaluatedTargetFileName,
                out MsBuildConfigurationType? evaluatedConfigType);

            var info = MsBuildProjectInfo.TryParseVcxproj(
                fullName!,
                _fileSystem,
                activeConfigName,
                activePlatformName,
                evaluatedTargetFileName,
                evaluatedConfigType);

            if (info != null)
            {
                projectInfos.Add(info);
            }
        }
        catch
        {
            // Ignore unloaded or inaccessible project
        }
    }

    private static void TryReadVcProjectProperties(
        Project project,
        out string? targetFileName,
        out MsBuildConfigurationType? configType)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        targetFileName = null;
        configType = null;

        try
        {
            object? vcProject = project.Object;
            if (vcProject == null)
            {
                return;
            }

            Type vcProjType = vcProject.GetType();
            PropertyInfo? activeCfgProp = vcProjType.GetProperty("ActiveConfiguration");
            object? vcConfig = activeCfgProp?.GetValue(vcProject);
            if (vcConfig == null)
            {
                return;
            }

            Type vcCfgType = vcConfig.GetType();

            // Read ConfigurationType enum (0 = typeUnknown/Makefile, 1 = typeApplication, 2 = typeDynamicLibrary, 4 = typeStaticLibrary)
            PropertyInfo? cfgTypeProp = vcCfgType.GetProperty("ConfigurationType");
            object? rawCfgType = cfgTypeProp?.GetValue(vcConfig);
            if (rawCfgType != null)
            {
                string cfgTypeStr = rawCfgType.ToString() ?? string.Empty;
                int cfgTypeInt = Convert.ToInt32(rawCfgType);
                if (cfgTypeInt == 1 || cfgTypeStr.IndexOf("Application", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    configType = MsBuildConfigurationType.Application;
                }
                else if (cfgTypeInt == 2 || cfgTypeStr.IndexOf("Dynamic", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    configType = MsBuildConfigurationType.DynamicLibrary;
                }
                else if (cfgTypeInt == 4 || cfgTypeStr.IndexOf("Static", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    configType = MsBuildConfigurationType.StaticLibrary;
                }
                else if (cfgTypeInt == 0 || cfgTypeInt == 10 || cfgTypeStr.IndexOf("MakeFile", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         cfgTypeStr.IndexOf("Utility", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    configType = MsBuildConfigurationType.Makefile;
                }
            }

            // Try Evaluate("$(TargetPath)") or PrimaryOutput
            MethodInfo? evalMethod = vcCfgType.GetMethod("Evaluate", new[] { typeof(string) });
            if (evalMethod != null && evalMethod.Invoke(vcConfig, new object[] { "$(TargetPath)" }) is string evaluatedPath)
            {
                string fileName = WildcardPattern.ExtractFileNameSafe(evaluatedPath);
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    targetFileName = fileName;
                    return;
                }
            }

            PropertyInfo? primaryOutProp = vcCfgType.GetProperty("PrimaryOutput");
            if (primaryOutProp?.GetValue(vcConfig) is string primaryOut)
            {
                string fileName = WildcardPattern.ExtractFileNameSafe(primaryOut);
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    targetFileName = fileName;
                }
            }
        }
        catch
        {
            // Fall back to .vcxproj XML properties
        }
    }
}

