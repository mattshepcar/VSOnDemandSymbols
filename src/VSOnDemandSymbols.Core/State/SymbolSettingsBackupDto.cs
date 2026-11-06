using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace VSOnDemandSymbols.Core.State;

/// <summary>
/// An entry in the user's original Visual Studio symbol settings IncludeList.
/// </summary>
public sealed class SymbolOptionBackupDto
{
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("isEnabled")]
    public bool IsEnabled { get; set; } = true;
}

/// <summary>
/// Persisted backup of the user's original Visual Studio symbol settings boolean flags and IncludeList (SPEC §7.4, §9.2).
/// </summary>
public sealed class SymbolSettingsBackupDto
{
    [JsonProperty("isManualLoad")]
    public bool IsManualLoad { get; set; }

    [JsonProperty("isLoadAdjacent")]
    public bool IsLoadAdjacent { get; set; }

    [JsonProperty("automaticSymbolLoad")]
    public bool AutomaticSymbolLoad { get; set; } = true;

    [JsonProperty("includeList")]
    public List<SymbolOptionBackupDto> IncludeList { get; set; } = new();

    [JsonProperty("timestampUtc")]
    public DateTime TimestampUtc { get; set; }

    [JsonProperty("processId")]
    public int ProcessId { get; set; }
}

