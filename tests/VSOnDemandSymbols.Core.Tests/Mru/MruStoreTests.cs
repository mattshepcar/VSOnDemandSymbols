using System;
using System.Linq;
using VSOnDemandSymbols.Core.Modules;
using VSOnDemandSymbols.Core.Mru;
using VSOnDemandSymbols.Core.Policy;
using Xunit;

namespace VSOnDemandSymbols.Core.Tests.Mru;

public class MruStoreTests
{
    private readonly TestClock _clock;
    private readonly MruStore _store;

    public MruStoreTests()
    {
        _clock = new TestClock(new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc));
        _store = new MruStore(_clock);
    }

    [Fact]
    public void Mru_HitUpdatesMetadata_MRU1()
    {
        var key = new ModuleKey(ModuleKind.Unreal, "MyGame");
        _store.RecordHit(key, TriggerReason.Breakpoint, "MyActor.cpp:42", new[] { "UnrealEditor-MyGame.dll" });

        var entry = _store.TryGetEntry(key);
        Assert.NotNull(entry);
        Assert.Equal(_clock.UtcNow, entry.LastTriggerUtc);
        Assert.Equal(0, entry.RunsSinceTrigger);
        Assert.Equal(TriggerReason.Breakpoint, entry.LastReason);
        Assert.Equal("MyActor.cpp:42", entry.LastReasonDetail);
        Assert.Equal(1, entry.TriggerCount);
        Assert.Contains("UnrealEditor-MyGame.dll", entry.Binaries);

        // Second hit increments count and updates detail
        _clock.Advance(TimeSpan.FromMinutes(5));
        _store.RecordHit(key, TriggerReason.Manual, "User click");

        Assert.Equal(_clock.UtcNow, entry.LastTriggerUtc);
        Assert.Equal(0, entry.RunsSinceTrigger);
        Assert.Equal(TriggerReason.Manual, entry.LastReason);
        Assert.Equal("User click", entry.LastReasonDetail);
        Assert.Equal(2, entry.TriggerCount);
    }

    [Fact]
    public void Mru_RunsSinceTriggerIncrementedAtSessionEnd_MRU3()
    {
        var key1 = new ModuleKey(ModuleKind.Unreal, "ModuleA");
        var key2 = new ModuleKey(ModuleKind.Unreal, "ModuleB");

        _store.StartSessionAndPrune();
        _store.RecordHit(key1, TriggerReason.Breakpoint);
        _store.RecordHit(key2, TriggerReason.Breakpoint);

        // At end of session 1, both were hit during session 1, so RunsSinceTrigger remains 0
        _store.EndSession();
        Assert.Equal(0, _store.TryGetEntry(key1)!.RunsSinceTrigger);
        Assert.Equal(0, _store.TryGetEntry(key2)!.RunsSinceTrigger);

        // Session 2: only ModuleA gets a hit
        _store.StartSessionAndPrune();
        _store.RecordHit(key1, TriggerReason.CallStack);
        _store.EndSession();

        Assert.Equal(0, _store.TryGetEntry(key1)!.RunsSinceTrigger);
        Assert.Equal(1, _store.TryGetEntry(key2)!.RunsSinceTrigger);
    }

    [Fact]
    public void Mru_PrunedByMaxRunsBoundary_MRU4_MRU5()
    {
        // SPEC §5.6 MRU-5: "module that triggered in run N is still loaded at the start of runs N+1 through N+5
        // (provided it's less than 24 hours since the trigger), and pruned at the start of run N+6."
        var key = new ModuleKey(ModuleKind.Unreal, "TransientModule");

        // Run 1: trigger occurs
        _store.StartSessionAndPrune();
        _store.RecordHit(key, TriggerReason.Breakpoint);
        _store.EndSession();
        // After Run 1 ended, RunsSinceTrigger is 0 because it was hit in Run 1

        // Run 2: starts. RunsSinceTrigger is 0. No hit during run 2.
        _store.StartSessionAndPrune();
        Assert.Contains(key, _store.GetWantedKeys());
        _store.EndSession(); // RunsSinceTrigger becomes 1

        // Run 3: starts. RunsSinceTrigger is 1. No hit.
        _store.StartSessionAndPrune();
        Assert.Contains(key, _store.GetWantedKeys());
        _store.EndSession(); // RunsSinceTrigger becomes 2

        // Run 4: starts. RunsSinceTrigger is 2. No hit.
        _store.StartSessionAndPrune();
        Assert.Contains(key, _store.GetWantedKeys());
        _store.EndSession(); // RunsSinceTrigger becomes 3

        // Run 5: starts. RunsSinceTrigger is 3. No hit.
        _store.StartSessionAndPrune();
        Assert.Contains(key, _store.GetWantedKeys());
        _store.EndSession(); // RunsSinceTrigger becomes 4

        // Run 6: starts. RunsSinceTrigger is 4 < 5. Still loaded in run 6!
        _store.StartSessionAndPrune();
        Assert.Contains(key, _store.GetWantedKeys());
        _store.EndSession(); // RunsSinceTrigger becomes 5

        // Run 7: starts. RunsSinceTrigger is 5 >= 5 (MruMaxRuns). Pruned now!
        var pruned = _store.StartSessionAndPrune();
        Assert.Contains(key, pruned);
        Assert.DoesNotContain(key, _store.GetWantedKeys());
        Assert.Null(_store.TryGetEntry(key));
    }

    [Fact]
    public void Mru_PrunedByAgeBoundary_MRU4()
    {
        var key = new ModuleKey(ModuleKind.Unreal, "AgeModule");

        _store.StartSessionAndPrune();
        _store.RecordHit(key, TriggerReason.CallStack);
        _store.EndSession();

        // 23 hours, 59 minutes, 59 seconds: should NOT be pruned
        _clock.Advance(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(59));
        var prunedBefore = _store.StartSessionAndPrune();
        Assert.DoesNotContain(key, prunedBefore);
        Assert.NotNull(_store.TryGetEntry(key));

        // Advance 1 second to reach exactly 24 hours: should be pruned
        _clock.Advance(TimeSpan.FromSeconds(1));
        var prunedAt24h = _store.StartSessionAndPrune();
        Assert.Contains(key, prunedAt24h);
        Assert.Null(_store.TryGetEntry(key));
    }

    [Fact]
    public void Mru_BreakpointRehitPreventsPruning_MRU6()
    {
        var key = new ModuleKey(ModuleKind.Unreal, "PersistentBpModule");

        // Simulate 10 runs where BP keeps re-hitting every run
        for (int i = 0; i < 10; i++)
        {
            _clock.Advance(TimeSpan.FromHours(5));
            _store.StartSessionAndPrune();
            _store.RecordHit(key, TriggerReason.Breakpoint, "Bp.cpp:10");
            _store.EndSession();
        }

        Assert.Equal(0, _store.TryGetEntry(key)!.RunsSinceTrigger);
        Assert.Contains(key, _store.GetWantedKeys());
    }

    [Fact]
    public void Mru_MaxEntriesEvictsOldestUnpinned_MRU7()
    {
        _store.Options.MruMaxEntries = 3;

        var k1 = new ModuleKey(ModuleKind.Unreal, "Mod1");
        var k2 = new ModuleKey(ModuleKind.Unreal, "Mod2");
        var k3 = new ModuleKey(ModuleKind.Unreal, "Mod3");
        var k4 = new ModuleKey(ModuleKind.Unreal, "Mod4");

        _clock.Advance(TimeSpan.FromMinutes(1));
        _store.RecordHit(k1, TriggerReason.Breakpoint);

        _clock.Advance(TimeSpan.FromMinutes(1));
        _store.RecordHit(k2, TriggerReason.Breakpoint);

        _clock.Advance(TimeSpan.FromMinutes(1));
        _store.RecordHit(k3, TriggerReason.Breakpoint);

        _clock.Advance(TimeSpan.FromMinutes(1));
        _store.RecordHit(k4, TriggerReason.Breakpoint);

        // 4 unpinned entries, capacity is 3. StartSessionAndPrune should evict oldest (k1)
        var pruned = _store.StartSessionAndPrune();
        Assert.Contains(k1, pruned);
        Assert.Null(_store.TryGetEntry(k1));
        Assert.NotNull(_store.TryGetEntry(k2));
        Assert.NotNull(_store.TryGetEntry(k3));
        Assert.NotNull(_store.TryGetEntry(k4));
    }

    [Fact]
    public void Mru_PinnedEntriesNeverPrunedOrEvicted_MRU8()
    {
        _store.Options.MruMaxEntries = 1;
        var pinnedKey = new ModuleKey(ModuleKind.Unreal, "PinnedMod");
        var unpinnedKey = new ModuleKey(ModuleKind.Unreal, "UnpinnedMod");

        _store.RecordHit(pinnedKey, TriggerReason.Manual);
        _store.Pin(pinnedKey);

        // Make pinnedKey very old (> 100 days) and runs = 100
        _store.TryGetEntry(pinnedKey)!.RunsSinceTrigger = 100;
        _clock.Advance(TimeSpan.FromDays(100));

        // Add an unpinned key
        _store.RecordHit(unpinnedKey, TriggerReason.Breakpoint);

        // Pruning runs
        var pruned = _store.StartSessionAndPrune();
        Assert.DoesNotContain(pinnedKey, pruned);
        Assert.True(_store.TryGetEntry(pinnedKey)!.Pinned);
        Assert.Contains(pinnedKey, _store.GetWantedKeys());
    }
}

