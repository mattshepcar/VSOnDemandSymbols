using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace VSOnDemandSymbols.Core.State;

/// <summary>
/// Persisted record of the user's symbol settings before any instance modified them (ADR-16).
/// Captured once when the first lease is created; deleted when the last lease is released.
/// </summary>
public sealed class BaselineDto
{
    [JsonProperty("activeSinceUtc")]
    public DateTime ActiveSinceUtc { get; set; }

    [JsonProperty("snapshot")]
    public SymbolSettingsSnapshot Snapshot { get; set; } = new();
}

/// <summary>
/// Lease representing an active debug session that requires manual symbol load mode (ADR-16).
/// Stored in <c>%LOCALAPPDATA%/OnDemandSymbols/&lt;VS_HIVE&gt;/leases/&lt;PID&gt;.json</c>.
/// </summary>
public sealed class SessionLeaseDto
{
    [JsonProperty("processId")]
    public int ProcessId { get; set; }

    [JsonProperty("wantedBinaries")]
    public List<string> WantedBinaries { get; set; } = new();

    [JsonProperty("serverMaskActive")]
    public bool ServerMaskActive { get; set; }

    [JsonProperty("updatedUtc")]
    public DateTime UpdatedUtc { get; set; }
}
