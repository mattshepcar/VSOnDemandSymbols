using System;

namespace VSOnDemandSymbols.Core.Mru;

public sealed class MruOptions
{
    public bool MruEnabled { get; set; } = true;
    public int MruMaxRuns { get; set; } = 5;
    public TimeSpan MruMaxAge { get; set; } = TimeSpan.FromHours(24);
    public int MruMaxEntries { get; set; } = 200;
}

