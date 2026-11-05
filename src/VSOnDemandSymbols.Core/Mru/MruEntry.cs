using System;
using System.Collections.Generic;
using VSOnDemandSymbols.Core.Modules;
using VSOnDemandSymbols.Core.Policy;

namespace VSOnDemandSymbols.Core.Mru;

public sealed class MruEntry
{
    public ModuleKey Key { get; }
    public List<string> Binaries { get; set; } = new();
    public bool Pinned { get; set; }
    public DateTime LastTriggerUtc { get; set; }
    public int RunsSinceTrigger { get; set; }
    public TriggerReason LastReason { get; set; }
    public string? LastReasonDetail { get; set; }
    public int TriggerCount { get; set; }

    public MruEntry(ModuleKey key)
    {
        Key = key;
    }
}

