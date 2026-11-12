using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace VSOnDemandSymbols.Core.State;

[JsonConverter(typeof(StringEnumConverter))]
public enum BaselineState
{
    Active,
    Released
}

/// <summary>
/// Persisted record of the user's symbol settings before any instance modified them (ADR-16).
/// Captured once when the first lease is created; never overwritten while Active.
/// </summary>
public sealed class BaselineDto
{
    [JsonProperty("state")]
    public BaselineState State { get; set; } = BaselineState.Active;

    [JsonProperty("activeSinceUtc")]
    public DateTime ActiveSinceUtc { get; set; }

    [JsonProperty("releasedUtc")]
    public DateTime? ReleasedUtc { get; set; }

    [JsonProperty("snapshot")]
    public SymbolSettingsSnapshot Snapshot { get; set; } = new();
}

[JsonConverter(typeof(StringEnumConverter))]
public enum LeaseKind
{
    /// <summary>
    /// Instance is debugging an Unreal project and requests manual-load mode with wanted binaries.
    /// </summary>
    Unreal,

    /// <summary>
    /// Instance is debugging a non-Unreal target and requires the user's original baseline settings.
    /// Priority rule P1: Baseline leases force baseline settings across the hive.
    /// </summary>
    Baseline
}

/// <summary>
/// Ephemeral lease representing a live Visual Studio process that requires a specific symbol load state (ADR-16).
/// Stored in <c>%LOCALAPPDATA%/OnDemandSymbols/&lt;VS_HIVE&gt;/leases/&lt;PID&gt;.json</c>.
/// </summary>
public sealed class SessionLeaseDto
{
    [JsonProperty("processId")]
    public int ProcessId { get; set; }

    [JsonProperty("processStartTimeUtc")]
    public DateTime ProcessStartTimeUtc { get; set; }

    [JsonProperty("kind")]
    public LeaseKind Kind { get; set; } = LeaseKind.Unreal;

    [JsonProperty("wantedBinaries")]
    public List<string> WantedBinaries { get; set; } = new();

    [JsonProperty("serverMaskActive")]
    public bool ServerMaskActive { get; set; }

    [JsonProperty("updatedUtc")]
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>
/// Record of an interval during which symbol settings were in a modified state,
/// together with the baseline restored at the end of that interval.
/// </summary>
public sealed class DirtyWindowDto
{
    [JsonProperty("activeSinceUtc")]
    public DateTime ActiveSinceUtc { get; set; }

    [JsonProperty("releasedUtc")]
    public DateTime ReleasedUtc { get; set; }

    [JsonProperty("restoredBaseline")]
    public SymbolSettingsSnapshot RestoredBaseline { get; set; } = new();
}

/// <summary>
/// Rolling log of the last dirty windows (capped at 20 entries).
/// Enables a devenv instance that started while settings were modified to repair
/// its in-memory settings at launch time even after the baseline has been released.
/// </summary>
public sealed class DirtyWindowLogDto
{
    public const int MaxEntries = 20;

    [JsonProperty("windows")]
    public List<DirtyWindowDto> Windows { get; set; } = new();
}
