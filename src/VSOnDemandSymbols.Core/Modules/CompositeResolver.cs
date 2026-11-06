using System;
using System.Collections.Generic;
using VSOnDemandSymbols.Core.Policy;

namespace VSOnDemandSymbols.Core.Modules;

/// <summary>
/// Combines <see cref="UnrealResolver"/> and <see cref="MsBuildResolver"/> (MODULE_RESOLUTION.md §1, ARCHITECTURE.md §2).
/// Tries <see cref="UnrealResolver"/> first when the solution is Unreal, then falls back to <see cref="MsBuildResolver"/>
/// when <see cref="Scope"/> is <see cref="SymbolScope.AllNative"/>.
/// </summary>
public sealed class CompositeResolver : ISourceToModuleResolver, IModuleBinaryNaming
{
    public UnrealResolver UnrealResolver { get; }
    public MsBuildResolver MsBuildResolver { get; }

    public bool IsUnrealSolution { get; set; }
    public SymbolScope Scope { get; set; } = SymbolScope.UnrealOnly;

    public CompositeResolver(UnrealResolver unrealResolver, MsBuildResolver msBuildResolver)
    {
        UnrealResolver = unrealResolver ?? throw new ArgumentNullException(nameof(unrealResolver));
        MsBuildResolver = msBuildResolver ?? throw new ArgumentNullException(nameof(msBuildResolver));
    }

    public IReadOnlyList<ModuleKey> Resolve(string sourceFilePath)
    {
        if (Scope == SymbolScope.Off)
        {
            return Array.Empty<ModuleKey>();
        }

        if (IsUnrealSolution)
        {
            var unrealResult = UnrealResolver.Resolve(sourceFilePath);
            if (unrealResult.Count > 0)
            {
                return unrealResult;
            }
        }

        if (Scope == SymbolScope.AllNative)
        {
            return MsBuildResolver.Resolve(sourceFilePath);
        }

        return Array.Empty<ModuleKey>();
    }

    public IReadOnlyList<string> GetCandidateFileNames(ModuleKey key)
    {
        return key.Kind switch
        {
            ModuleKind.Unreal => UnrealResolver.GetCandidateFileNames(key),
            ModuleKind.MsBuild => MsBuildResolver.GetCandidateFileNames(key),
            ModuleKind.External when !string.IsNullOrWhiteSpace(key.Name) => new[] { WildcardPattern.ExtractFileNameSafe(key.Name) },
            _ => Array.Empty<string>()
        };
    }

    public ModuleKey? TryParseLoadedModule(string modulePath)
    {
        if (IsUnrealSolution)
        {
            var unrealKey = UnrealResolver.TryParseLoadedModule(modulePath);
            if (unrealKey.HasValue)
            {
                return unrealKey;
            }
        }

        if (Scope == SymbolScope.AllNative)
        {
            return MsBuildResolver.TryParseLoadedModule(modulePath);
        }

        return null;
    }
}

