using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace VSOnDemandSymbols.IntegrationTests;

public sealed class UpdateIncludeListMetric
{
    public string Reason { get; }
    public long TotalMs { get; }
    public long ResolveMs { get; }
    public int BreakpointsCount { get; }
    public long ApplyMs { get; }

    public UpdateIncludeListMetric(string reason, long totalMs, long resolveMs, int breakpointsCount, long applyMs)
    {
        Reason = reason;
        TotalMs = totalMs;
        ResolveMs = resolveMs;
        BreakpointsCount = breakpointsCount;
        ApplyMs = applyMs;
    }
}

public sealed class BreakModeProfileMetric
{
    public string Reason { get; }
    public long TotalMs { get; }
    public long StackWalkMs { get; }
    public int ThreadsInspected { get; }
    public int FramesInspected { get; }
    public long ModuleScanMs { get; }
    public int ModulesScanned { get; }
    public long SymbolLoadMs { get; }
    public int ModulesLoaded { get; }
    public int CandidateTargetModules { get; }
    public long ContextRefreshMs { get; }

    public BreakModeProfileMetric(
        string reason,
        long totalMs,
        long stackWalkMs,
        int threadsInspected,
        int framesInspected,
        long moduleScanMs,
        int modulesScanned,
        long symbolLoadMs,
        int modulesLoaded,
        int candidateTargetModules,
        long contextRefreshMs)
    {
        Reason = reason;
        TotalMs = totalMs;
        StackWalkMs = stackWalkMs;
        ThreadsInspected = threadsInspected;
        FramesInspected = framesInspected;
        ModuleScanMs = moduleScanMs;
        ModulesScanned = modulesScanned;
        SymbolLoadMs = symbolLoadMs;
        ModulesLoaded = modulesLoaded;
        CandidateTargetModules = candidateTargetModules;
        ContextRefreshMs = contextRefreshMs;
    }
}

public sealed class BreakpointBindProfileMetric
{
    public long TotalMs { get; }
    public long IncludeListMs { get; }
    public long ModuleScanMs { get; }
    public int ModulesScanned { get; }
    public long SymbolLoadMs { get; }
    public int ModulesLoaded { get; }

    public BreakpointBindProfileMetric(
        long totalMs,
        long includeListMs,
        long moduleScanMs,
        int modulesScanned,
        long symbolLoadMs,
        int modulesLoaded)
    {
        TotalMs = totalMs;
        IncludeListMs = includeListMs;
        ModuleScanMs = moduleScanMs;
        ModulesScanned = modulesScanned;
        SymbolLoadMs = symbolLoadMs;
        ModulesLoaded = modulesLoaded;
    }
}

public sealed class ModuleSymbolLoadMetric
{
    public string ModuleFileName { get; }
    public string Reason { get; }
    public bool LocalOnly { get; }
    public long ElapsedMs { get; }

    public ModuleSymbolLoadMetric(string moduleFileName, string reason, bool localOnly, long elapsedMs)
    {
        ModuleFileName = moduleFileName;
        Reason = reason;
        LocalOnly = localOnly;
        ElapsedMs = elapsedMs;
    }
}

public sealed class ParsedProfileReport
{
    public List<UpdateIncludeListMetric> IncludeListUpdates { get; } = new();
    public List<BreakModeProfileMetric> BreakModeEvents { get; } = new();
    public List<BreakpointBindProfileMetric> BreakpointBindEvents { get; } = new();
    public List<ModuleSymbolLoadMetric> ModuleLoads { get; } = new();
}

/// <summary>
/// Parses structured <c>[Profile]</c> and on-demand symbol load timing entries from the
/// "On-Demand Symbols" Output window text.
/// </summary>
public static class ProfileMetricsParser
{
    private static readonly Regex UpdateIncludeListRegex = new(
        @"\[Profile\]\s+UpdateIncludeList\s+\((?<reason>[^)]+)\):\s+total=(?<total>\d+)ms\s+\(resolve=(?<resolve>\d+)ms\s+\[breakpoints=(?<bps>\d+)\],\s+apply=(?<apply>\d+)ms\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BreakModeRegex = new(
        @"\[Profile\]\s+(?<reason>BreakMode|ContextChanged):\s+total=(?<total>\d+)ms\s+\(stackWalk=(?<stackWalk>\d+)ms\s+\[threads=(?<threads>\d+),\s+frames=(?<frames>\d+)\],\s+moduleScan=(?<moduleScan>\d+)ms\s+\[modules=(?<modules>\d+)\],\s+symbolLoad=(?<symbolLoad>\d+)ms\s+\[loaded=(?<loaded>\d+)/(?<targets>\d+)\],\s+contextRefresh=(?<contextRefresh>\d+)ms\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BreakpointBindRegex = new(
        @"\[Profile\]\s+BreakpointBind:\s+total=(?<total>\d+)ms\s+\(includeList=(?<includeList>\d+)ms,\s+moduleScan=(?<moduleScan>\d+)ms\s+\[modules=(?<modules>\d+)\],\s+symbolLoad=(?<symbolLoad>\d+)ms\s+\[loaded=(?<loaded>\d+)\]\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ModuleLoadRegex = new(
        @"Loading symbols on demand(?<local>\s+\(local-only\))?:\s+(?<module>[^\s—]+)\s+—\s+(?<reason>[^\s(]+)\s+\((?<ms>\d+)\s+ms\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ParsedProfileReport Parse(string outputText)
    {
        var report = new ParsedProfileReport();
        if (string.IsNullOrWhiteSpace(outputText))
        {
            return report;
        }

        foreach (Match match in UpdateIncludeListRegex.Matches(outputText))
        {
            report.IncludeListUpdates.Add(new UpdateIncludeListMetric(
                reason: match.Groups["reason"].Value,
                totalMs: ParseLong(match.Groups["total"].Value),
                resolveMs: ParseLong(match.Groups["resolve"].Value),
                breakpointsCount: ParseInt(match.Groups["bps"].Value),
                applyMs: ParseLong(match.Groups["apply"].Value)));
        }

        foreach (Match match in BreakModeRegex.Matches(outputText))
        {
            report.BreakModeEvents.Add(new BreakModeProfileMetric(
                reason: match.Groups["reason"].Value,
                totalMs: ParseLong(match.Groups["total"].Value),
                stackWalkMs: ParseLong(match.Groups["stackWalk"].Value),
                threadsInspected: ParseInt(match.Groups["threads"].Value),
                framesInspected: ParseInt(match.Groups["frames"].Value),
                moduleScanMs: ParseLong(match.Groups["moduleScan"].Value),
                modulesScanned: ParseInt(match.Groups["modules"].Value),
                symbolLoadMs: ParseLong(match.Groups["symbolLoad"].Value),
                modulesLoaded: ParseInt(match.Groups["loaded"].Value),
                candidateTargetModules: ParseInt(match.Groups["targets"].Value),
                contextRefreshMs: ParseLong(match.Groups["contextRefresh"].Value)));
        }

        foreach (Match match in BreakpointBindRegex.Matches(outputText))
        {
            report.BreakpointBindEvents.Add(new BreakpointBindProfileMetric(
                totalMs: ParseLong(match.Groups["total"].Value),
                includeListMs: ParseLong(match.Groups["includeList"].Value),
                moduleScanMs: ParseLong(match.Groups["moduleScan"].Value),
                modulesScanned: ParseInt(match.Groups["modules"].Value),
                symbolLoadMs: ParseLong(match.Groups["symbolLoad"].Value),
                modulesLoaded: ParseInt(match.Groups["loaded"].Value)));
        }

        foreach (Match match in ModuleLoadRegex.Matches(outputText))
        {
            report.ModuleLoads.Add(new ModuleSymbolLoadMetric(
                moduleFileName: match.Groups["module"].Value,
                reason: match.Groups["reason"].Value,
                localOnly: match.Groups["local"].Success,
                elapsedMs: ParseLong(match.Groups["ms"].Value)));
        }

        return report;
    }

    private static long ParseLong(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result) ? result : 0L;

    private static int ParseInt(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? result : 0;
}
