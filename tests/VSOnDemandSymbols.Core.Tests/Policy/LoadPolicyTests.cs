using System.Collections.Generic;
using VSOnDemandSymbols.Core.Modules;
using VSOnDemandSymbols.Core.Policy;
using Xunit;

namespace VSOnDemandSymbols.Core.Tests.Policy;

public class LoadPolicyTests
{
    [Fact]
    public void ComputeWantedSet_UnionsAllSources_SubtractsNeverLoad()
    {
        var pinned = new[] { new ModuleKey(ModuleKind.Unreal, "PinnedMod") };
        var mru = new[] { new ModuleKey(ModuleKind.Unreal, "MruMod") };
        var bp = new[] { new ModuleKey(ModuleKind.Unreal, "BpMod") };
        var rb = new[] { new ModuleKey(ModuleKind.Unreal, "RbMod") };
        var never = WildcardPattern.ParseMultiple("ue:MruMod; BpMod.dll");

        var policy = new LoadPolicy(neverLoadPatterns: never);
        var wanted = policy.ComputeWantedSet(
            pinnedKeys: pinned,
            mruKeys: mru,
            breakpointKeys: bp,
            recentlyBuiltKeys: rb,
            recentlyBuiltGuardTripped: false);

        Assert.Contains(new ModuleKey(ModuleKind.Unreal, "PinnedMod"), wanted);
        Assert.Contains(new ModuleKey(ModuleKind.Unreal, "RbMod"), wanted);
        // MruMod matches ue:MruMod -> excluded
        Assert.DoesNotContain(new ModuleKey(ModuleKind.Unreal, "MruMod"), wanted);
    }

    [Fact]
    public void ComputeWantedSet_MassRebuildGuardTripped_ExcludesRecentlyBuilt()
    {
        var pinned = new[] { new ModuleKey(ModuleKind.Unreal, "PinnedMod") };
        var rb = new[] { new ModuleKey(ModuleKind.Unreal, "RbMod") };

        var policy = new LoadPolicy();
        var wanted = policy.ComputeWantedSet(
            pinnedKeys: pinned,
            mruKeys: System.Array.Empty<ModuleKey>(),
            breakpointKeys: System.Array.Empty<ModuleKey>(),
            recentlyBuiltKeys: rb,
            recentlyBuiltGuardTripped: true);

        Assert.Contains(new ModuleKey(ModuleKind.Unreal, "PinnedMod"), wanted);
        Assert.DoesNotContain(new ModuleKey(ModuleKind.Unreal, "RbMod"), wanted);
    }

    [Fact]
    public void ShouldLoadOnDemand_NeverLoadBlocksAutomatic_AllowsManual()
    {
        var key = new ModuleKey(ModuleKind.Unreal, "BlockedMod");
        var never = WildcardPattern.ParseMultiple("ue:BlockedMod");
        var policy = new LoadPolicy(neverLoadPatterns: never);

        Assert.False(policy.ShouldLoadOnDemand(key, TriggerReason.Breakpoint));
        Assert.False(policy.ShouldLoadOnDemand(key, TriggerReason.CallStack));
        Assert.False(policy.ShouldLoadOnDemand(key, TriggerReason.RecentlyBuilt));
        // Manual MUST still load
        Assert.True(policy.ShouldLoadOnDemand(key, TriggerReason.Manual));
    }

    [Fact]
    public void ShouldLoadAtModuleLoad_EvaluatesPrecedenceCorrectly()
    {
        var always = WildcardPattern.ParseMultiple("UnrealEditor.exe; ue:AlwaysMod");
        var never = WildcardPattern.ParseMultiple("Blocked.dll; ue:NeverMod");
        var policy = new LoadPolicy(alwaysLoadPatterns: always, neverLoadPatterns: never);

        // Never-load wins first
        Assert.False(policy.ShouldLoadAtModuleLoad("Blocked.dll", new ModuleKey(ModuleKind.Unreal, "BlockedMod"), isKeyInWantedSet: true));
        Assert.False(policy.ShouldLoadAtModuleLoad("Foo.dll", new ModuleKey(ModuleKind.Unreal, "NeverMod"), isKeyInWantedSet: true));

        // Always-load wins next
        Assert.True(policy.ShouldLoadAtModuleLoad("UnrealEditor.exe", null, isKeyInWantedSet: false));
        Assert.True(policy.ShouldLoadAtModuleLoad("Any.dll", new ModuleKey(ModuleKind.Unreal, "AlwaysMod"), isKeyInWantedSet: false));

        // Otherwise depends on isKeyInWantedSet
        Assert.True(policy.ShouldLoadAtModuleLoad("Normal.dll", new ModuleKey(ModuleKind.Unreal, "NormalMod"), isKeyInWantedSet: true));
        Assert.False(policy.ShouldLoadAtModuleLoad("Normal.dll", new ModuleKey(ModuleKind.Unreal, "NormalMod"), isKeyInWantedSet: false));
    }
}

