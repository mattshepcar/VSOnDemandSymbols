using System.IO;
using VSOnDemandSymbols.Core.Abstractions;
using VSOnDemandSymbols.Core.Modules;
using Xunit;

namespace VSOnDemandSymbols.Core.Tests.Modules;

public class UnrealManifestReaderTests
{
    private readonly InMemoryFileSystem _fileSystem;
    private readonly UnrealManifestReader _reader;

    public UnrealManifestReaderTests()
    {
        _fileSystem = new InMemoryFileSystem();
        _reader = new UnrealManifestReader(_fileSystem, NullLogger.Instance);
    }

    [Fact]
    public void GetCandidateFileNames_ReadsFromModulesManifest()
    {
        string binDir = @"C:\Project\Binaries\Win64";
        string manifestPath = Path.Combine(binDir, "UnrealEditor.modules");

        _fileSystem.WriteAllText(manifestPath, @"{
            ""BuildId"": ""12345"",
            ""Modules"": {
                ""MyGame"": ""UnrealEditor-MyGame.dll"",
                ""MyGameEditor"": ""UnrealEditor-MyGameEditor.dll""
            }
        }");

        var candidates = _reader.GetCandidateFileNamesForModule(binDir, "MyGame");
        Assert.Single(candidates);
        Assert.Equal("UnrealEditor-MyGame.dll", candidates[0]);
    }

    [Fact]
    public void TryResolveModuleFromLoadedDll_ReverseMapsCorrectly()
    {
        string binDir = @"C:\Project\Binaries\Win64";
        string manifestPath = Path.Combine(binDir, "UnrealEditor.modules");

        _fileSystem.WriteAllText(manifestPath, @"{
            ""BuildId"": ""12345"",
            ""Modules"": {
                ""MyGame"": ""UnrealEditor-MyGame.dll""
            }
        }");

        string? mod = _reader.TryResolveModuleFromLoadedDll(binDir, @"C:\Project\Binaries\Win64\UnrealEditor-MyGame.dll");
        Assert.Equal("MyGame", mod);

        string? nonExistent = _reader.TryResolveModuleFromLoadedDll(binDir, "Unknown.dll");
        Assert.Null(nonExistent);
    }
}

