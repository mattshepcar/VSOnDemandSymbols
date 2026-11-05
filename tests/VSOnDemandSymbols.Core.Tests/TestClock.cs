using System;
using VSOnDemandSymbols.Core.Abstractions;

namespace VSOnDemandSymbols.Core.Tests;

public sealed class TestClock : IClock
{
    public DateTime UtcNow { get; set; }

    public TestClock(DateTime initialUtc)
    {
        UtcNow = initialUtc;
    }

    public void Advance(TimeSpan delta)
    {
        UtcNow += delta;
    }
}

