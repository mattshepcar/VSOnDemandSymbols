using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using VSOnDemandSymbols.Core.Abstractions;
using VSOnDemandSymbols.Core.Policy;

namespace VSOnDemandSymbols.Core.Modules;

/// <summary>
/// Resolves source files and binaries for non-Unreal MSBuild (<c>.vcxproj</c>) C++ projects via
/// <see cref="IProjectModelProvider"/> (SPEC §6.3, MODULE_RESOLUTION.md §6).
/// </summary>
public sealed class MsBuildResolver : ISourceToModuleResolver, IModuleBinaryNaming
{
    private readonly IProjectModelProvider _projectModelProvider;
    private readonly ConcurrentDictionary<string, IReadOnlyList<ModuleKey>> _resolutionCache =
        new(StringComparer.OrdinalIgnoreCase);

    public MsBuildResolver(IProjectModelProvider projectModelProvider)
    {
        _projectModelProvider = projectModelProvider ?? throw new ArgumentNullException(nameof(projectModelProvider));
    }

    public IReadOnlyList<ModuleKey> Resolve(string sourceFilePath)
    {
        string cleanPath = WildcardPattern.SanitizePathString(sourceFilePath);
        if (string.IsNullOrWhiteSpace(cleanPath))
        {
            return Array.Empty<ModuleKey>();
        }

        return _resolutionCache.GetOrAdd(cleanPath, ResolveInternal);
    }

    public IReadOnlyList<string> GetCandidateFileNames(ModuleKey key)
    {
        if (key.Kind != ModuleKind.MsBuild || string.IsNullOrWhiteSpace(key.Name))
        {
            return Array.Empty<string>();
        }

        return new[] { WildcardPattern.ExtractFileNameSafe(key.Name) };
    }

    public ModuleKey? TryParseLoadedModule(string modulePath)
    {
        string fileName = WildcardPattern.ExtractFileNameSafe(modulePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        if (fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return new ModuleKey(ModuleKind.MsBuild, fileName);
        }

        return null;
    }

    public void InvalidateCache()
    {
        _resolutionCache.Clear();
    }

    private IReadOnlyList<ModuleKey> ResolveInternal(string sourceFilePath)
    {
        var outputs = _projectModelProvider.ResolveSourceFileToOutputs(sourceFilePath);
        if (outputs == null || outputs.Count == 0)
        {
            return Array.Empty<ModuleKey>();
        }

        var keys = new List<ModuleKey>();
        var seen = new HashSet<ModuleKey>();
        foreach (string output in outputs)
        {
            string fileName = WildcardPattern.ExtractFileNameSafe(output);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                var key = new ModuleKey(ModuleKind.MsBuild, fileName);
                if (seen.Add(key))
                {
                    keys.Add(key);
                }
            }
        }

        return keys;
    }
}

