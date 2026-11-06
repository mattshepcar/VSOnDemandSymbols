using System;
using System.Collections.Generic;
using System.Linq;
using VSOnDemandSymbols.Core.Modules;
using VSOnDemandSymbols.Core.Policy;
using Xunit;

namespace VSOnDemandSymbols.Core.Tests.Policy;

public class CallStackEvaluatorTests
{
    private static ModuleKey? FakeResolveLoadedModule(string fileName)
    {
        var parser = new UnrealDllNameParser();
        return parser.TryParseLoadedModule(fileName);
    }

    [Fact]
    public void EvaluateBreak_ModeNone_ExaminesNoFrames_CS2()
    {
        var policy = new LoadPolicy { CallStackMode = CallStackMode.None };
        var evaluator = new CallStackEvaluator(policy);
        var examined = new HashSet<uint>();

        var threads = new[]
        {
            new CallStackThreadInput(100, isCurrentThread: true, new[]
            {
                new CallStackFrameInput("UnrealEditor-MyGame.dll", "AMyActor::Tick", hasSymbolsLoaded: false)
            })
        };

        var decisions = evaluator.EvaluateBreak(threads, isExceptionOrHardBreak: true, examined, FakeResolveLoadedModule);

        Assert.Empty(decisions);
        Assert.Empty(examined);
    }

    [Fact]
    public void EvaluateBreak_CurrentThread_ExaminesOnlyCurrentThread_CS1_CS2()
    {
        var policy = new LoadPolicy { CallStackMode = CallStackMode.CurrentThread };
        var evaluator = new CallStackEvaluator(policy);
        var examined = new HashSet<uint>();

        var threads = new[]
        {
            new CallStackThreadInput(100, isCurrentThread: true, new[]
            {
                new CallStackFrameInput("UnrealEditor-MyGame.dll", "0x0001", hasSymbolsLoaded: false)
            }),
            new CallStackThreadInput(200, isCurrentThread: false, new[]
            {
                new CallStackFrameInput("UnrealEditor-Engine.dll", "0x0002", hasSymbolsLoaded: false)
            })
        };

        // Even when isExceptionOrHardBreak is true, CurrentThread mode only examines the current thread
        var decisions = evaluator.EvaluateBreak(threads, isExceptionOrHardBreak: true, examined, FakeResolveLoadedModule);

        Assert.Single(decisions);
        Assert.Equal(new ModuleKey(ModuleKind.Unreal, "MyGame"), decisions[0].Key);
        Assert.Equal(TriggerReason.CallStack, decisions[0].Reason);
        Assert.Contains(100u, examined);
        Assert.DoesNotContain(200u, examined);
    }

    [Fact]
    public void EvaluateBreak_CurrentThreadAllThreadsOnException_NormalBreakExaminesCurrentOnly_ExceptionExaminesAllThreads_CS2()
    {
        var policy = new LoadPolicy { CallStackMode = CallStackMode.CurrentThreadAllThreadsOnException };
        var evaluator = new CallStackEvaluator(policy);

        var threads = new[]
        {
            new CallStackThreadInput(100, isCurrentThread: true, new[]
            {
                new CallStackFrameInput("UnrealEditor-MyGame.dll", "0x0001", hasSymbolsLoaded: false)
            }),
            new CallStackThreadInput(200, isCurrentThread: false, new[]
            {
                new CallStackFrameInput("UnrealEditor-RenderCore.dll", "0x0002", hasSymbolsLoaded: false)
            })
        };

        // 1. Normal break (e.g. user pause / breakpoint / step): only current thread (100)
        var examinedNormal = new HashSet<uint>();
        var normalDecisions = evaluator.EvaluateBreak(threads, isExceptionOrHardBreak: false, examinedNormal, FakeResolveLoadedModule);
        Assert.Single(normalDecisions);
        Assert.Equal(new ModuleKey(ModuleKind.Unreal, "MyGame"), normalDecisions[0].Key);
        Assert.Single(examinedNormal);
        Assert.Contains(100u, examinedNormal);

        // 2. Exception or hard break (__debugbreak / ensure / check): all threads (100 and 200)
        var examinedException = new HashSet<uint>();
        var exceptionDecisions = evaluator.EvaluateBreak(threads, isExceptionOrHardBreak: true, examinedException, FakeResolveLoadedModule);
        Assert.Equal(2, exceptionDecisions.Count);
        Assert.Contains(exceptionDecisions, d => d.Key == new ModuleKey(ModuleKind.Unreal, "MyGame"));
        Assert.Contains(exceptionDecisions, d => d.Key == new ModuleKey(ModuleKind.Unreal, "RenderCore"));
        Assert.Contains(100u, examinedException);
        Assert.Contains(200u, examinedException);
    }

    [Fact]
    public void EvaluateBreak_AllThreads_ExaminesAllThreadsEvenOnNormalBreak_CS2()
    {
        var policy = new LoadPolicy { CallStackMode = CallStackMode.AllThreads };
        var evaluator = new CallStackEvaluator(policy);
        var examined = new HashSet<uint>();

        var threads = new[]
        {
            new CallStackThreadInput(100, isCurrentThread: true, new[]
            {
                new CallStackFrameInput("UnrealEditor-MyGame.dll", "0x0001", hasSymbolsLoaded: false)
            }),
            new CallStackThreadInput(200, isCurrentThread: false, new[]
            {
                new CallStackFrameInput("UnrealEditor-PhysicsCore.dll", "0x0002", hasSymbolsLoaded: false)
            })
        };

        var decisions = evaluator.EvaluateBreak(threads, isExceptionOrHardBreak: false, examined, FakeResolveLoadedModule);

        Assert.Equal(2, decisions.Count);
        Assert.Contains(100u, examined);
        Assert.Contains(200u, examined);
    }

    [Fact]
    public void EvaluateBreak_RespectsCallStackMaxFrames_CS3()
    {
        var policy = new LoadPolicy
        {
            CallStackMode = CallStackMode.CurrentThread,
            CallStackMaxFrames = 2
        };
        var evaluator = new CallStackEvaluator(policy);

        var threads = new[]
        {
            new CallStackThreadInput(100, isCurrentThread: true, new[]
            {
                new CallStackFrameInput("UnrealEditor-ModA.dll", "0x1", hasSymbolsLoaded: false),
                new CallStackFrameInput("UnrealEditor-ModB.dll", "0x2", hasSymbolsLoaded: false),
                new CallStackFrameInput("UnrealEditor-ModC.dll", "0x3", hasSymbolsLoaded: false)
            })
        };

        var decisions = evaluator.EvaluateBreak(threads, isExceptionOrHardBreak: false, null, FakeResolveLoadedModule);

        Assert.Equal(2, decisions.Count);
        Assert.Equal(new ModuleKey(ModuleKind.Unreal, "ModA"), decisions[0].Key);
        Assert.Equal(new ModuleKey(ModuleKind.Unreal, "ModB"), decisions[1].Key);
    }

    [Fact]
    public void EvaluateBreak_ExternalModules_ControlledByCallStackIncludeExternalModules_CS4()
    {
        var threads = new[]
        {
            new CallStackThreadInput(100, isCurrentThread: true, new[]
            {
                new CallStackFrameInput("UnrealEditor-MyGame.dll", "0x1", hasSymbolsLoaded: false),
                new CallStackFrameInput("kernel32.dll", "BaseThreadInitThunk", hasSymbolsLoaded: false)
            })
        };

        // Default: CallStackIncludeExternalModules = true
        var policyIncludeExt = new LoadPolicy { CallStackIncludeExternalModules = true };
        var evalInclude = new CallStackEvaluator(policyIncludeExt);
        var withExt = evalInclude.EvaluateBreak(threads, isExceptionOrHardBreak: false, null, FakeResolveLoadedModule);
        Assert.Equal(2, withExt.Count);
        Assert.Contains(withExt, d => d.Key == new ModuleKey(ModuleKind.External, "kernel32.dll"));

        // Disabled: CallStackIncludeExternalModules = false
        var policyNoExt = new LoadPolicy { CallStackIncludeExternalModules = false };
        var evalNoExt = new CallStackEvaluator(policyNoExt);
        var withoutExt = evalNoExt.EvaluateBreak(threads, isExceptionOrHardBreak: false, null, FakeResolveLoadedModule);
        Assert.Single(withoutExt);
        Assert.Equal(new ModuleKey(ModuleKind.Unreal, "MyGame"), withoutExt[0].Key);
    }

    [Fact]
    public void EvaluateBreak_SkipsFramesWithSymbolsAndNeverLoadPatterns_CS1()
    {
        var policy = new LoadPolicy(neverLoadPatterns: WildcardPattern.ParseMultiple("ue:BlockedMod; ntdll.dll"));
        var evaluator = new CallStackEvaluator(policy);

        var threads = new[]
        {
            new CallStackThreadInput(100, isCurrentThread: true, new[]
            {
                new CallStackFrameInput("UnrealEditor-AlreadyLoaded.dll", "Func", hasSymbolsLoaded: true),
                new CallStackFrameInput("UnrealEditor-BlockedMod.dll", "0x1", hasSymbolsLoaded: false),
                new CallStackFrameInput("ntdll.dll", "0x2", hasSymbolsLoaded: false),
                new CallStackFrameInput("UnrealEditor-WantedMod.dll", "0x3", hasSymbolsLoaded: false)
            })
        };

        var decisions = evaluator.EvaluateBreak(threads, isExceptionOrHardBreak: false, null, FakeResolveLoadedModule);

        Assert.Single(decisions);
        Assert.Equal(new ModuleKey(ModuleKind.Unreal, "WantedMod"), decisions[0].Key);
    }

    [Fact]
    public void EvaluateFrameActivation_Disabled_DoesNothing_FA1()
    {
        var policy = new LoadPolicy { LoadOnFrameActivation = false };
        var evaluator = new CallStackEvaluator(policy);
        var examined = new HashSet<uint>();

        var frame = new CallStackFrameInput("UnrealEditor-MyGame.dll", "0x1", hasSymbolsLoaded: false);
        var thread = new CallStackThreadInput(200, isCurrentThread: true, new[] { frame });

        var decisions = evaluator.EvaluateFrameActivation(frame, thread, examined, FakeResolveLoadedModule);

        Assert.Empty(decisions);
        Assert.Empty(examined);
    }

    [Fact]
    public void EvaluateFrameActivation_AlreadyExaminedThread_LoadsOnlyActivatedFrame_FA1()
    {
        var policy = new LoadPolicy { LoadOnFrameActivation = true };
        var evaluator = new CallStackEvaluator(policy);
        var examined = new HashSet<uint> { 100u };

        var activatedFrame = new CallStackFrameInput("UnrealEditor-DeepFrameMod.dll", "0x99", hasSymbolsLoaded: false);
        var thread = new CallStackThreadInput(100u, isCurrentThread: true, new[]
        {
            new CallStackFrameInput("UnrealEditor-OtherFrameMod.dll", "0x100", hasSymbolsLoaded: false)
        });

        var decisions = evaluator.EvaluateFrameActivation(activatedFrame, thread, examined, FakeResolveLoadedModule);

        Assert.Single(decisions);
        Assert.Equal(new ModuleKey(ModuleKind.Unreal, "DeepFrameMod"), decisions[0].Key);
        Assert.Equal(TriggerReason.FrameActivated, decisions[0].Reason);
    }

    [Fact]
    public void EvaluateFrameActivation_UnexaminedThread_ExaminesEntireThreadStack_FA1_FA2()
    {
        var policy = new LoadPolicy { LoadOnFrameActivation = true };
        var evaluator = new CallStackEvaluator(policy);
        var examined = new HashSet<uint> { 100u }; // Thread 100 was examined on break, Thread 200 was not

        var activatedFrame = new CallStackFrameInput("UnrealEditor-WorkerTop.dll", "0x1", hasSymbolsLoaded: false);
        var thread200 = new CallStackThreadInput(200u, isCurrentThread: true, new[]
        {
            activatedFrame,
            new CallStackFrameInput("UnrealEditor-WorkerMid.dll", "0x2", hasSymbolsLoaded: false),
            new CallStackFrameInput("kernel32.dll", "0x3", hasSymbolsLoaded: false)
        });

        var decisions = evaluator.EvaluateFrameActivation(activatedFrame, thread200, examined, FakeResolveLoadedModule);

        Assert.Equal(3, decisions.Count);
        Assert.All(decisions, d => Assert.Equal(TriggerReason.FrameActivated, d.Reason));
        Assert.Contains(decisions, d => d.Key == new ModuleKey(ModuleKind.Unreal, "WorkerTop"));
        Assert.Contains(decisions, d => d.Key == new ModuleKey(ModuleKind.Unreal, "WorkerMid"));
        Assert.Contains(decisions, d => d.Key == new ModuleKey(ModuleKind.External, "kernel32.dll"));
        Assert.Contains(200u, examined);
    }
}

