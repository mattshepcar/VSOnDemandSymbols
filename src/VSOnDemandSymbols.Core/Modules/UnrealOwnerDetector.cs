using System;
using System.IO;
using System.Linq;
using VSOnDemandSymbols.Core.Abstractions;

namespace VSOnDemandSymbols.Core.Modules;

public sealed class UnrealOwnerDetector
{
    private readonly IFileSystem _fileSystem;

    public UnrealOwnerDetector(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    public UnrealOwner DetectOwner(string startDirectory, string? knownEngineRoot = null, string? knownProjectRoot = null)
    {
        if (string.IsNullOrWhiteSpace(startDirectory) || !_fileSystem.DirectoryExists(startDirectory))
        {
            return new UnrealOwner(UnrealOwnerKind.Unknown, startDirectory ?? "", "");
        }

        string? current = Path.GetFullPath(startDirectory);
        string? pluginDir = null;

        while (!string.IsNullOrEmpty(current))
        {
            // Check for *.uplugin
            if (pluginDir == null && HasFilesMatching(current, "*.uplugin"))
            {
                pluginDir = current;
            }

            // Check for *.uproject
            if (HasFilesMatching(current, "*.uproject") ||
                (knownProjectRoot != null && string.Equals(current, knownProjectRoot, StringComparison.OrdinalIgnoreCase)))
            {
                if (pluginDir != null)
                {
                    return new UnrealOwner(
                        UnrealOwnerKind.ProjectPlugin,
                        pluginDir,
                        Path.Combine(pluginDir, "Binaries", "Win64"));
                }

                return new UnrealOwner(
                    UnrealOwnerKind.Project,
                    current,
                    Path.Combine(current, "Binaries", "Win64"));
            }

            // Check for Engine root
            if (IsEngineRoot(current, out string engineRoot, out string engineBinaries))
            {
                if (pluginDir != null)
                {
                    return new UnrealOwner(
                        UnrealOwnerKind.EnginePlugin,
                        pluginDir,
                        Path.Combine(pluginDir, "Binaries", "Win64"));
                }

                return new UnrealOwner(
                    UnrealOwnerKind.Engine,
                    engineRoot,
                    engineBinaries);
            }

            if (knownEngineRoot != null && string.Equals(current, knownEngineRoot, StringComparison.OrdinalIgnoreCase))
            {
                string engineBinariesDir = Path.Combine(current, "Engine", "Binaries", "Win64");
                if (pluginDir != null)
                {
                    return new UnrealOwner(
                        UnrealOwnerKind.EnginePlugin,
                        pluginDir,
                        Path.Combine(pluginDir, "Binaries", "Win64"));
                }

                return new UnrealOwner(
                    UnrealOwnerKind.Engine,
                    current,
                    engineBinariesDir);
            }

            var parent = Path.GetDirectoryName(current);
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            current = parent;
        }

        // If plugin was found but no parent uproject/engine detected yet
        if (pluginDir != null)
        {
            return new UnrealOwner(
                UnrealOwnerKind.ProjectPlugin,
                pluginDir,
                Path.Combine(pluginDir, "Binaries", "Win64"));
        }

        return new UnrealOwner(UnrealOwnerKind.Unknown, startDirectory, "");
    }

    public bool IsEngineRoot(string directory)
    {
        return IsEngineRoot(directory, out _, out _);
    }

    public bool IsEngineRoot(string directory, out string engineRoot, out string binariesDirectory)
    {
        engineRoot = "";
        binariesDirectory = "";
        if (string.IsNullOrEmpty(directory)) return false;

        string engineBuildVersion = Path.Combine(directory, "Engine", "Build", "Build.version");
        if (_fileSystem.FileExists(engineBuildVersion))
        {
            engineRoot = directory;
            binariesDirectory = Path.Combine(directory, "Engine", "Binaries", "Win64");
            return true;
        }

        string directBuildVersion = Path.Combine(directory, "Build", "Build.version");
        if (_fileSystem.FileExists(directBuildVersion))
        {
            string dirName = Path.GetFileName(directory);
            if (string.Equals(dirName, "Engine", StringComparison.OrdinalIgnoreCase))
            {
                engineRoot = Path.GetDirectoryName(directory) ?? directory;
                binariesDirectory = Path.Combine(directory, "Binaries", "Win64");
            }
            else
            {
                engineRoot = directory;
                binariesDirectory = Path.Combine(directory, "Binaries", "Win64");
            }
            return true;
        }

        return false;
    }

    private bool HasFilesMatching(string directory, string searchPattern)
    {
        return _fileSystem.EnumerateFiles(directory, searchPattern, SearchOption.TopDirectoryOnly).Any();
    }
}
