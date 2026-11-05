using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace VSOnDemandSymbols.Core.State;

public sealed class SolutionState
{
    [JsonProperty("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonProperty("lastSessionStartUtc")]
    public DateTime? LastSessionStartUtc { get; set; }

    [JsonProperty("sessionCount")]
    public int SessionCount { get; set; }

    [JsonProperty("forceUnrealMode")]
    public bool? ForceUnrealMode { get; set; }

    [JsonProperty("modules")]
    public Dictionary<string, ModuleStateDto> Modules { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ModuleStateDto
{
    [JsonProperty("kind")]
    public string Kind { get; set; } = "Unreal";

    [JsonProperty("binaries")]
    public List<string> Binaries { get; set; } = new();

    [JsonProperty("pinned")]
    public bool Pinned { get; set; }

    [JsonProperty("lastTriggerUtc")]
    public DateTime LastTriggerUtc { get; set; }

    [JsonProperty("runsSinceTrigger")]
    public int RunsSinceTrigger { get; set; }

    [JsonProperty("lastReason")]
    public string LastReason { get; set; } = "Breakpoint";

    [JsonProperty("lastReasonDetail")]
    public string? LastReasonDetail { get; set; }

    [JsonProperty("triggerCount")]
    public int TriggerCount { get; set; }
}

