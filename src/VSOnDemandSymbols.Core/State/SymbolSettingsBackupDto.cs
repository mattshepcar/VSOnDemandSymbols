using System;
using Newtonsoft.Json;

namespace VSOnDemandSymbols.Core.State;

/// <summary>
/// Persisted backup of the user's original Visual Studio symbol settings boolean flags (SPEC §7.4, §9.2, D-13, D-15).
/// </summary>
public sealed class SymbolSettingsBackupDto
{
    [JsonProperty("isManualLoad")]
    public bool IsManualLoad { get; set; }

    [JsonProperty("isLoadAdjacent")]
    public bool IsLoadAdjacent { get; set; }

    [JsonProperty("automaticSymbolLoad")]
    public bool AutomaticSymbolLoad { get; set; } = true;

    [JsonProperty("timestampUtc")]
    public DateTime TimestampUtc { get; set; }

    [JsonProperty("processId")]
    public int ProcessId { get; set; }
}

