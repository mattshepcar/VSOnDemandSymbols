using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using VSOnDemandSymbols.Core.Abstractions;

namespace VSOnDemandSymbols.Core.Modules;

public sealed class UnrealModuleManifestDto
{
    [JsonProperty("BuildId")]
    public string? BuildId { get; set; }

    [JsonProperty("Modules")]
    public Dictionary<string, string> Modules { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonProperty("LibraryDependencies")]
    public Dictionary<string, List<string>>? LibraryDependencies { get; set; }
}

public sealed class UnrealManifestReader
{
    private readonly IFileSystem _fileSystem;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<string, (DateTime LastWriteTimeUtc, UnrealModuleManifestDto Manifest)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public UnrealManifestReader(IFileSystem fileSystem, ILogger logger)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IReadOnlyList<string> GetCandidateFileNamesForModule(string binariesDirectory, string moduleName)
    {
        if (string.IsNullOrEmpty(binariesDirectory) || !_fileSystem.DirectoryExists(binariesDirectory))
        {
            return Array.Empty<string>();
        }

        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var manifestFiles = _fileSystem.EnumerateFiles(binariesDirectory, "*.modules", SearchOption.TopDirectoryOnly);
        foreach (var manifestPath in manifestFiles)
        {
            var manifest = ReadManifest(manifestPath);
            if (manifest != null && manifest.Modules.TryGetValue(moduleName, out var dllFileName))
            {
                results.Add(dllFileName);
            }
        }

        return results.ToList();
    }

    public string? TryResolveModuleFromLoadedDll(string binariesDirectory, string loadedDllFileName)
    {
        if (string.IsNullOrEmpty(binariesDirectory) || !_fileSystem.DirectoryExists(binariesDirectory))
        {
            return null;
        }

        string fileName = Path.GetFileName(loadedDllFileName);
        var manifestFiles = _fileSystem.EnumerateFiles(binariesDirectory, "*.modules", SearchOption.TopDirectoryOnly);
        foreach (var manifestPath in manifestFiles)
        {
            var manifest = ReadManifest(manifestPath);
            if (manifest != null)
            {
                foreach (var kvp in manifest.Modules)
                {
                    if (string.Equals(kvp.Value, fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        return kvp.Key;
                    }
                }
            }
        }

        return null;
    }

    private UnrealModuleManifestDto? ReadManifest(string manifestPath)
    {
        try
        {
            var lastWrite = _fileSystem.GetLastWriteTimeUtc(manifestPath);
            if (_cache.TryGetValue(manifestPath, out var cached) && cached.LastWriteTimeUtc == lastWrite)
            {
                return cached.Manifest;
            }

            string json = _fileSystem.ReadAllText(manifestPath);
            var manifest = JsonConvert.DeserializeObject<UnrealModuleManifestDto>(json);
            if (manifest != null)
            {
                _cache[manifestPath] = (lastWrite, manifest);
                return manifest;
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Failed to read or parse Unreal module manifest '{manifestPath}': {ex.Message}");
        }

        return null;
    }
}

