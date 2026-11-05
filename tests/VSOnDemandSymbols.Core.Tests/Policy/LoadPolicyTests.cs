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
        var sessionTriggered = new[] { new ModuleKey(ModuleKind.Unreal, "SessionMod") };
        var never = WildcardPattern.ParseMultiple("ue:MruMod; BpMod.dll");

        var policy = new LoadPolicy(neverLoadPatterns: never);
        var wanted = policy.ComputeWantedSet(
            pinnedKeys: pinned,
            mruKeys: mru,
            breakpointKeys: bp,
            sessionTriggeredKeys: sessionTriggered);

        Assert.Contains(new ModuleKey(ModuleKind.Unreal, "PinnedMod"), wanted);
        Assert.Contains(new ModuleKey(ModuleKind.Unreal, "BpMod"), wanted);
        Assert.Contains(new ModuleKey(ModuleKind.Unreal, "SessionMod"), wanted);
        // MruMod matches ue:MruMod -> excluded
        Assert.DoesNotContain(new ModuleKey(ModuleKind.Unreal, "MruMod"), wanted);
    }

    [Fact]
    public void ShouldLoadOnDemand_NeverLoadBlocksAutomatic_AllowsManual()
    {
        var key = new ModuleKey(ModuleKind.Unreal, "BlockedMod");
        var never = WildcardPattern.ParseMultiple("ue:BlockedMod");
        var policy = new LoadPolicy(neverLoadPatterns: never);

        Assert.False(policy.ShouldLoadOnDemand(key, TriggerReason.Breakpoint));
        Assert.False(policy.ShouldLoadOnDemand(key, TriggerReason.CallStack));
        Assert.False(policy.ShouldLoadOnDemand(key, TriggerReason.FrameActivated));
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
