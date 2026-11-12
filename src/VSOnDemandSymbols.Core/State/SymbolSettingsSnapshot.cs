using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace VSOnDemandSymbols.Core.State;

/// <summary>
/// Snapshot of Visual Studio symbol settings managed or monitored by the extension.
/// </summary>
public sealed class SymbolSettingsSnapshot
{
    [JsonProperty("isManualLoad")]
    public bool IsManualLoad { get; set; }

    [JsonProperty("isLoadAdjacent")]
    public bool IsLoadAdjacent { get; set; }

    [JsonProperty("automaticSymbolLoad")]
    public bool AutomaticSymbolLoad { get; set; } = true;

    [JsonProperty("useMsSymbolServers")]
    public bool UseMsSymbolServers { get; set; }

    [JsonProperty("useNugetSymbolServers")]
    public bool UseNugetSymbolServers { get; set; }

    [JsonProperty("symbolPaths")]
    public List<SymbolOptionBackupDto> SymbolPaths { get; set; } = new();

    [JsonProperty("includeList")]
    public List<SymbolOptionBackupDto> IncludeList { get; set; } = new();

    public SymbolSettingsSnapshot Clone()
    {
        var clone = new SymbolSettingsSnapshot
        {
            IsManualLoad = IsManualLoad,
            IsLoadAdjacent = IsLoadAdjacent,
            AutomaticSymbolLoad = AutomaticSymbolLoad,
            UseMsSymbolServers = UseMsSymbolServers,
            UseNugetSymbolServers = UseNugetSymbolServers,
            SymbolPaths = new List<SymbolOptionBackupDto>(SymbolPaths.Count),
            IncludeList = new List<SymbolOptionBackupDto>(IncludeList.Count)
        };

        foreach (var opt in SymbolPaths)
        {
            clone.SymbolPaths.Add(new SymbolOptionBackupDto
            {
                Name = opt.Name,
                IsEnabled = opt.IsEnabled
            });
        }

        foreach (var opt in IncludeList)
        {
            clone.IncludeList.Add(new SymbolOptionBackupDto
            {
                Name = opt.Name,
                IsEnabled = opt.IsEnabled
            });
        }

        return clone;
    }

    /// <summary>
    /// Checks whether the primary load-mode flags and IncludeList are equal.
    /// Order-independent set comparison is used for IncludeList.
    /// </summary>
    public bool ManagedFieldsEqual(SymbolSettingsSnapshot? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null) return false;

        if (IsManualLoad != other.IsManualLoad ||
            IsLoadAdjacent != other.IsLoadAdjacent ||
            AutomaticSymbolLoad != other.AutomaticSymbolLoad)
        {
            return false;
        }

        return OptionListsEqual(IncludeList, other.IncludeList);
    }

    /// <summary>
    /// Checks whether remote symbol server flags and SymbolPaths are equal.
    /// </summary>
    public bool ServerSettingsEqual(SymbolSettingsSnapshot? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null) return false;

        if (UseMsSymbolServers != other.UseMsSymbolServers ||
            UseNugetSymbolServers != other.UseNugetSymbolServers)
        {
            return false;
        }

        return OptionListsEqual(SymbolPaths, other.SymbolPaths);
    }

    private static bool OptionListsEqual(IReadOnlyList<SymbolOptionBackupDto>? a, IReadOnlyList<SymbolOptionBackupDto>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a.Count != b.Count) return false;

        var setA = new Dictionary<string, bool>(a.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var opt in a)
        {
            if (!string.IsNullOrWhiteSpace(opt.Name))
            {
                setA[opt.Name] = opt.IsEnabled;
            }
        }

        foreach (var opt in b)
        {
            if (string.IsNullOrWhiteSpace(opt.Name)) continue;
            if (!setA.TryGetValue(opt.Name, out bool enabled) || enabled != opt.IsEnabled)
            {
                return false;
            }
        }

        return setA.Count == b.Count;
    }
}
