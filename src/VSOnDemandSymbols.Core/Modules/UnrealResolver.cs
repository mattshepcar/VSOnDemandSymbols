using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VSOnDemandSymbols.Core.Abstractions;

namespace VSOnDemandSymbols.Core.Modules;

public sealed class UnrealResolver : ISourceToModuleResolver, IModuleBinaryNaming
{
    private static readonly Regex IntermediateIncRegex = new(
        @"[/\\]Intermediate[/\\]Build[/\\][^/\\]+[/\\](?:[^/\\]+[/\\])?(?:Inc[/\\](?:.*[/\\])?(?<module>[^/\\]+)[/\\]UHT[/\\]|Inc[/\\](?<module>[^/\\]+)[/\\])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex IntermediateUnityRegex = new(
        @"[/\\]Intermediate[/\\]Build[/\\][^/\\]+[/\\](?:[^/\\]+[/\\])*(?:Module\.(?<module>[A-Za-z0-9_]+)(?:\.\d+(?:_of_\d+)?)?\.cpp)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex IntermediateModuleFileRegex = new(
        @"[/\\]Intermediate[/\\]Build[/\\][^/\\]+[/\\](?:[^/\\]+[/\\])+(?<module>[A-Za-z0-9_]+)[/\\](?:PerModuleInline\.gen\.cpp|Definitions\.[A-Za-z0-9_]+\.h)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly IFileSystem _fileSystem;
    private readonly ILogger _logger;
    private readonly UnrealOwnerDetector _ownerDetector;
    private readonly UnrealManifestReader _manifestReader;
    private readonly UnrealDllNameParser _dllParser;

    private readonly ConcurrentDictionary<string, string?> _dirToBuildCsCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyList<ModuleKey>> _sourceResolutionCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _projectPluginBinariesCache = new(StringComparer.OrdinalIgnoreCase);

    public string? EngineRoot { get; set; }
    public string? ProjectRoot { get; set; }

    public UnrealResolver(
        IFileSystem fileSystem,
        ILogger logger,
        UnrealOwnerDetector? ownerDetector = null,
        UnrealManifestReader? manifestReader = null,
        UnrealDllNameParser? dllParser = null,
        string? engineRoot = null,
        string? projectRoot = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _ownerDetector = ownerDetector ?? new UnrealOwnerDetector(_fileSystem);
        _manifestReader = manifestReader ?? new UnrealManifestReader(_fileSystem, _logger);
        _dllParser = dllParser ?? new UnrealDllNameParser();
        EngineRoot = engineRoot;
        ProjectRoot = projectRoot;
    }

    public IReadOnlyList<ModuleKey> Resolve(string sourceFilePath)
    {
        sourceFilePath = Policy.WildcardPattern.SanitizePathString(sourceFilePath);
        if (string.IsNullOrWhiteSpace(sourceFilePath))
        {
            return Array.Empty<ModuleKey>();
        }

        if (_sourceResolutionCache.TryGetValue(sourceFilePath, out var cached))
        {
            return cached;
        }

        var resolved = ResolveInternal(sourceFilePath);
        _sourceResolutionCache[sourceFilePath] = resolved;
        return resolved;
    }

    private IReadOnlyList<ModuleKey> ResolveInternal(string sourceFilePath)
    {
        // 1. Special case: Intermediate/Build generated files
        if (sourceFilePath.IndexOf("Intermediate", StringComparison.OrdinalIgnoreCase) >= 0 &&
            sourceFilePath.IndexOf("Build", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var incMatch = IntermediateIncRegex.Match(sourceFilePath);
            if (incMatch.Success)
            {
                string modName = incMatch.Groups["module"].Value;
                return new[] { new ModuleKey(ModuleKind.Unreal, modName) };
            }

            var unityMatch = IntermediateUnityRegex.Match(sourceFilePath);
            if (unityMatch.Success)
            {
                string modName = unityMatch.Groups["module"].Value;
                return new[] { new ModuleKey(ModuleKind.Unreal, modName) };
            }

            var modFileMatch = IntermediateModuleFileRegex.Match(sourceFilePath);
            if (modFileMatch.Success)
            {
                string modName = modFileMatch.Groups["module"].Value;
                return new[] { new ModuleKey(ModuleKind.Unreal, modName) };
            }
        }

        // 2. Build.cs walk-up
        string? dir = Path.GetDirectoryName(sourceFilePath);
        while (!string.IsNullOrEmpty(dir))
        {
            if (_dirToBuildCsCache.TryGetValue(dir, out var cachedBuildCs))
            {
                if (cachedBuildCs != null)
                {
                    return new[] { new ModuleKey(ModuleKind.Unreal, GetModuleNameFromBuildCs(cachedBuildCs)) };
                }
                return Array.Empty<ModuleKey>();
            }

            // Stop condition: root boundaries
            if (IsBoundaryDirectory(dir))
            {
                // Check if boundary dir itself has Build.cs before stopping
                var buildCsFilesAtBoundary = FindBuildCsFiles(dir);
                if (buildCsFilesAtBoundary.Count > 0)
                {
                    string chosen = PickBuildCs(dir, buildCsFilesAtBoundary);
                    _dirToBuildCsCache[dir] = chosen;
                    return new[] { new ModuleKey(ModuleKind.Unreal, GetModuleNameFromBuildCs(chosen)) };
                }

                _dirToBuildCsCache[dir] = null;
                break;
            }

            var buildCsFiles = FindBuildCsFiles(dir);
            if (buildCsFiles.Count > 0)
            {
                string chosen = PickBuildCs(dir, buildCsFiles);
                _dirToBuildCsCache[dir] = chosen;
                return new[] { new ModuleKey(ModuleKind.Unreal, GetModuleNameFromBuildCs(chosen)) };
            }

            var parent = Path.GetDirectoryName(dir);
            if (string.Equals(parent, dir, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            dir = parent;
        }

        return Array.Empty<ModuleKey>();
    }

    public IReadOnlyList<string> GetCandidateFileNames(ModuleKey key)
    {
        if (key.Kind != ModuleKind.Unreal)
        {
            return Array.Empty<string>();
        }

        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Try reading manifests from known binaries dirs
        var searchDirs = GetKnownBinariesDirectories();
        foreach (var dir in searchDirs)
        {
            var candidates = _manifestReader.GetCandidateFileNamesForModule(dir, key.Name);
            foreach (var c in candidates)
            {
                results.Add(c);
            }
        }

        // Always include standard UBT naming candidates (MODULE_RESOLUTION.md §4 & §8)
        // so switching between Development Editor and DebugGame Editor works even before
        // the new configuration's .modules manifest has been written.
        results.Add($"UnrealEditor-{key.Name}.dll");
        results.Add($"UnrealEditor-{key.Name}-Win64-DebugGame.dll");
        results.Add($"UnrealEditor-{key.Name}-Win64-Debug.dll");
        results.Add($"UnrealEditor-{key.Name}-Win64-Development.dll");

        return results.ToList();
    }

    public ModuleKey? TryParseLoadedModule(string modulePath)
    {
        var key = _dllParser.TryParseLoadedModule(modulePath);
        if (key.HasValue)
        {
            return key;
        }

        // Fallback: check manifests in known binaries directories
        string fileName = Policy.WildcardPattern.ExtractFileNameSafe(modulePath);
        if (fileName.Length == 0)
        {
            return null;
        }
        foreach (var dir in GetKnownBinariesDirectories())
        {
            string? mod = _manifestReader.TryResolveModuleFromLoadedDll(dir, fileName);
            if (mod != null)
            {
                return new ModuleKey(ModuleKind.Unreal, mod);
            }
        }

        return null;
    }

    public IEnumerable<string> GetKnownBinariesDirectories()
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(ProjectRoot))
        {
            dirs.Add(Path.Combine(ProjectRoot, "Binaries", "Win64"));
            foreach (string pluginBinDir in GetProjectPluginBinariesDirectories(ProjectRoot!))
            {
                dirs.Add(pluginBinDir);
            }
        }
        if (!string.IsNullOrEmpty(EngineRoot))
        {
            dirs.Add(Path.Combine(EngineRoot, "Binaries", "Win64"));
            dirs.Add(Path.Combine(EngineRoot, "Engine", "Binaries", "Win64"));
        }
        return dirs;
    }

    private IReadOnlyList<string> GetProjectPluginBinariesDirectories(string projectRoot)
    {
        return _projectPluginBinariesCache.GetOrAdd(projectRoot, root =>
        {
            var list = new List<string>();
            string pluginsRoot = Path.Combine(root, "Plugins");
            if (!_fileSystem.DirectoryExists(pluginsRoot))
            {
                return list;
            }

            try
            {
                foreach (string upluginPath in _fileSystem.EnumerateFiles(pluginsRoot, "*.uplugin", SearchOption.AllDirectories))
                {
                    string? pluginDir = Path.GetDirectoryName(upluginPath);
                    if (!string.IsNullOrEmpty(pluginDir))
                    {
                        list.Add(Path.Combine(pluginDir!, "Binaries", "Win64"));
                    }
                }
            }
            catch
            {
                // Ignore directory enumeration failures
            }

            return list;
        });
    }

    private bool IsBoundaryDirectory(string dir)
    {
        if (_fileSystem.EnumerateFiles(dir, "*.uplugin", SearchOption.TopDirectoryOnly).Any()) return true;
        if (_fileSystem.EnumerateFiles(dir, "*.uproject", SearchOption.TopDirectoryOnly).Any()) return true;
        if (_ownerDetector.IsEngineRoot(dir)) return true;
        if (Path.GetDirectoryName(dir) == null) return true; // Drive root

        return false;
    }

    private List<string> FindBuildCsFiles(string dir)
    {
        return _fileSystem.EnumerateFiles(dir, "*.Build.cs", SearchOption.TopDirectoryOnly).ToList();
    }

    private string PickBuildCs(string dir, List<string> buildCsFiles)
    {
        if (buildCsFiles.Count == 1)
        {
            return buildCsFiles[0];
        }

        string dirName = Path.GetFileName(dir);
        var match = buildCsFiles.FirstOrDefault(f =>
            string.Equals(GetModuleNameFromBuildCs(f), dirName, StringComparison.OrdinalIgnoreCase));

        if (match != null)
        {
            return match;
        }

        _logger.Warning($"Directory '{dir}' contains multiple *.Build.cs files: {string.Join(", ", buildCsFiles)}. Picking first.");
        return buildCsFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).First();
    }

    private static string GetModuleNameFromBuildCs(string buildCsPath)
    {
        string fileName = Path.GetFileName(buildCsPath);
        if (fileName.EndsWith(".Build.cs", StringComparison.OrdinalIgnoreCase))
        {
            return fileName.Substring(0, fileName.Length - ".Build.cs".Length);
        }
        return fileName;
    }
}

