namespace VSOnDemandSymbols.Core.Policy;

/// <summary>
/// Controls which threads have their call stacks symbolized when the debugger enters break mode (SPEC §5.2 CS-2, §8.5).
/// </summary>
public enum CallStackMode
{
    /// <summary>
    /// Do not automatically load symbols for call-stack frames on break.
    /// </summary>
    None,

    /// <summary>
    /// Examine only the current (breaking) thread.
    /// </summary>
    CurrentThread,

    /// <summary>
    /// Examine the current thread on normal breaks (breakpoint, step, user pause),
    /// and all threads on exceptions, unhandled exceptions/crashes, or hard-coded breaks (<c>__debugbreak</c> / <c>check</c> / <c>ensure</c>).
    /// This is the default (SPEC §5.2 CS-2).
    /// </summary>
    CurrentThreadAllThreadsOnException,

    /// <summary>
    /// Examine all threads on every break.
    /// </summary>
    AllThreads
}

