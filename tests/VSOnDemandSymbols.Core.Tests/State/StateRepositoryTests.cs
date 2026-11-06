using System;
using VSOnDemandSymbols.Core.Abstractions;
using VSOnDemandSymbols.Core.Modules;
using VSOnDemandSymbols.Core.Mru;
using VSOnDemandSymbols.Core.Policy;
using VSOnDemandSymbols.Core.State;
using Xunit;

namespace VSOnDemandSymbols.Core.Tests.State;

public class StateRepositoryTests
{
    private readonly InMemoryFileSystem _fileSystem;
    private readonly StateRepository _repository;

    public StateRepositoryTests()
    {
        _fileSystem = new InMemoryFileSystem();
        _repository = new StateRepository(_fileSystem, NullLogger.Instance);
    }

    [Fact]
    public void Load_NonExistentFile_ReturnsFreshState()
    {
        var state = _repository.Load(@"C:\Sol\.vs\Sol\OnDemandSymbols\state.json");
        Assert.NotNull(state);
        Assert.Equal(1, state.SchemaVersion);
        Assert.Empty(state.Modules);
    }

    [Fact]
    public void SaveAndLoad_RoundTrip_PreservesState()
    {
        string path = @"C:\Sol\.vs\Sol\OnDemandSymbols\state.json";
        var state = new SolutionState
        {
            SchemaVersion = 1,
            SessionCount = 42,
            ForceUnrealMode = true
        };
        state.Modules["MyGame"] = new ModuleStateDto
        {
            Kind = "Unreal",
            Binaries = { "UnrealEditor-MyGame.dll" },
            Pinned = true,
            LastTriggerUtc = new DateTime(2026, 11, 5, 19, 58, 12, DateTimeKind.Utc),
            RunsSinceTrigger = 0,
            LastReason = "Breakpoint",
            LastReasonDetail = "MyActor.cpp(42)",
            TriggerCount = 17
        };

        _repository.Save(path, state);
        Assert.True(_fileSystem.FileExists(path));

        var loaded = _repository.Load(path);
        Assert.NotNull(loaded);
        Assert.Equal(1, loaded.SchemaVersion);
        Assert.Equal(42, loaded.SessionCount);
        Assert.True(loaded.ForceUnrealMode);
        Assert.Single(loaded.Modules);

        var mod = loaded.Modules["MyGame"];
        Assert.Equal("Unreal", mod.Kind);
        Assert.Contains("UnrealEditor-MyGame.dll", mod.Binaries);
        Assert.True(mod.Pinned);
        Assert.Equal(17, mod.TriggerCount);
    }

    [Fact]
    public void Load_CorruptFile_RenamesToBadAndReturnsFreshState()
    {
        string path = @"C:\Sol\.vs\Sol\OnDemandSymbols\state.json";
        _fileSystem.WriteAllText(path, "{ corrupted json: !!! broken ");

        var state = _repository.Load(path);
        Assert.NotNull(state);
        Assert.Empty(state.Modules);

        // Check corrupt file was moved to .bad
        Assert.False(_fileSystem.FileExists(path));
        Assert.True(_fileSystem.FileExists(path + ".bad"));
    }

    [Fact]
    public void MruStore_Conversion_RoundTripsAccurately()
    {
        var clock = new TestClock(new DateTime(2026, 11, 5, 15, 0, 0, DateTimeKind.Utc));
        var store = new MruStore(clock);
        var key = new ModuleKey(ModuleKind.Unreal, "Engine");
        store.RecordHit(key, TriggerReason.CallStack, "Crash.cpp:10", new[] { "UnrealEditor-Engine.dll" });
        store.Pin(key);

        var state = StateRepository.CreateFromMru(store, sessionCount: 5);
        Assert.Single(state.Modules);

        var restoredStore = new MruStore(clock);
        StateRepository.PopulateMruStore(state, restoredStore);

        var entry = restoredStore.TryGetEntry(key);
        Assert.NotNull(entry);
        Assert.True(entry.Pinned);
        Assert.Equal(TriggerReason.CallStack, entry.LastReason);
        Assert.Equal("Crash.cpp:10", entry.LastReasonDetail);
        Assert.Equal(1, entry.TriggerCount);
        Assert.Contains("UnrealEditor-Engine.dll", entry.Binaries);
    }

    [Fact]
    public void GetSolutionStateFilePath_ReturnsExpectedVsFolderLocation()
    {
        string path = StateRepository.GetSolutionStateFilePath(@"D:\MyProject\MyGame.sln");
        Assert.Equal(@"D:\MyProject\.vs\MyGame\OnDemandSymbols\state.json", path);
    }

    [Fact]
    public void GetNoSolutionStateFilePath_ReturnsConsistentHashedLocation()
    {
        string path1 = StateRepository.GetNoSolutionStateFilePath(@"C:\LocalAppData", @"D:\UE\Engine\Binaries\Win64\UnrealEditor.exe");
        string path2 = StateRepository.GetNoSolutionStateFilePath(@"C:\LocalAppData", @"d:\ue\engine\binaries\win64\unrealeditor.exe");

        Assert.Equal(path1, path2);
        Assert.StartsWith(@"C:\LocalAppData\OnDemandSymbols\nosolution\", path1);
        Assert.EndsWith(@"\state.json", path1);
    }
}
