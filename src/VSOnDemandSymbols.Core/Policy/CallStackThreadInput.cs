using System;
using System.Collections.Generic;

namespace VSOnDemandSymbols.Core.Policy;

/// <summary>
/// Represents a thread and its call-stack frames when execution breaks or a frame is activated (SPEC §5.2, §5.3).
/// </summary>
public sealed class CallStackThreadInput
{
    public uint ThreadId { get; }
    public bool IsCurrentThread { get; }
    public IReadOnlyList<CallStackFrameInput> Frames { get; }

    public CallStackThreadInput(
        uint threadId,
        bool isCurrentThread,
        IReadOnlyList<CallStackFrameInput> frames)
    {
        ThreadId = threadId;
        IsCurrentThread = isCurrentThread;
        Frames = frames ?? throw new ArgumentNullException(nameof(frames));
    }
}

