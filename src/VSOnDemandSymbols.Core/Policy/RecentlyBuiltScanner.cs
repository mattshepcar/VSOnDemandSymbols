using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VSOnDemandSymbols.Core.Abstractions;
using VSOnDemandSymbols.Core.Modules;

namespace VSOnDemandSymbols.Core.Policy;

public sealed class RecentlyBuiltOptions
{
    public bool Enabled { get; set; } = true;
    public int MaxModules { get; set; } = 40;
    public TimeSpan FirstSessionRecentWindow { get; set; } = TimeSpan.FromMinutes(60);
}

public sealed class RecentlyBuiltScanResult
{
    public IReadOnlyList<ModuleKey> Modules { get; }
    public bool IsGuardTripped { get; }
    public int TotalDetectedCount { get; }

    public RecentlyBuiltScanResult(IReadOnlyList<ModuleKey> modules, bool isGuardTripped, int totalDetectedCount)
    {
        Modules = modules;
        IsGuardTripped = isGuardTripped;
        TotalDetectedCount = totalDetectedCount;
    }
}

public sealed class RecentlyBuiltScanner
{
    private readonly IClock _clock;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger _logger;

    public RecentlyBuiltOptions Options { get; set; }

    public RecentlyBuiltScanner(
        IClock clock,
        IFileSystem fileSystem,
        ILogger logger,
        RecentlyBuiltOptions? options = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Options = options ?? new RecentlyBuiltOptions();
    }

    public DateTime GetCutoffTimeUtc(DateTime? lastSessionStartUtc)
    {
        if (lastSessionStartUtc.HasValue)
        {
            return lastSessionStartUtc.Value;
        }

        return _clock.UtcNow - Options.FirstSessionRecentWindow;
    }

    public RecentlyBuiltScanResult Scan(
        IEnumerable<string> binariesDirectories,
        DateTime? lastSessionStartUtc,
        Func<string, (ModuleKey? Key, bool IsLiveCodingPatch)> parseModuleBinary)
    {
        if (!Options.Enabled)
        {
            return new RecentlyBuiltScanResult(Array.Empty<ModuleKey>(), false, 0);
        }

        var cutoffUtc = GetCutoffTimeUtc(lastSessionStartUtc);
        var standardModules = new HashSet<ModuleKey>();
        var exemptModules = new HashSet<ModuleKey>(); // e.g. Live Coding patches (RB-5)

        foreach (var dir in binariesDirectories)
        {
            if (!_fileSystem.DirectoryExists(dir)) continue;

            // Search dll, exe, pdb
            var files = _fileSystem.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase));

            foreach (var file in files)
            {
                var lastWriteUtc = _fileSystem.GetLastWriteTimeUtc(file);
                if (lastWriteUtc > cutoffUtc)
                {
                    var (key, isLiveCodingPatch) = parseModuleBinary(file);
                    if (key.HasValue)
                    {
                        if (isLiveCodingPatch)
                        {
                            exemptModules.Add(key.Value);
                        }
                        else
                        {
                            standardModules.Add(key.Value);
                        }
                    }
                }
            }
        }

        int totalCount = standardModules.Count + exemptModules.Count;
        bool guardTripped = standardModules.Count > Options.MaxModules;

        if (guardTripped)
        {
            _logger.Warning($"Mass-rebuild guard tripped: {standardModules.Count} modules were recently built (> {Options.MaxModules}). Disabling recently-built automatic loads for this session (exempt live coding patches: {exemptModules.Count}).");
            return new RecentlyBuiltScanResult(exemptModules.ToList(), true, totalCount);
        }

        var result = new List<ModuleKey>(standardModules);
        result.AddRange(exemptModules);
        return new RecentlyBuiltScanResult(result, false, totalCount);
    }

    public bool IsRecentlyBuilt(string binaryPath, DateTime? lastSessionStartUtc)
    {
        if (!_fileSystem.FileExists(binaryPath)) return false;

        var cutoffUtc = GetCutoffTimeUtc(lastSessionStartUtc);
        return _fileSystem.GetLastWriteTimeUtc(binaryPath) > cutoffUtc;
    }
}

