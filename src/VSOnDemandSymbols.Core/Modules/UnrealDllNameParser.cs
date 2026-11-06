using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace VSOnDemandSymbols.Core.Modules;

public sealed class ModuleParseInfo
{
    public ModuleKey Key { get; }
    public string AppPrefix { get; }
    public string ModuleName { get; }
    public string? Platform { get; }
    public string? Configuration { get; }
    public string? Architecture { get; }
    public string? HotReloadSuffix { get; }
    public bool IsLiveCodingPatch { get; }

    public ModuleParseInfo(
        ModuleKey key,
        string appPrefix,
        string moduleName,
        string? platform,
        string? configuration,
        string? architecture,
        string? hotReloadSuffix,
        bool isLiveCodingPatch)
    {
        Key = key;
        AppPrefix = appPrefix;
        ModuleName = moduleName;
        Platform = platform;
        Configuration = configuration;
        Architecture = architecture;
        HotReloadSuffix = hotReloadSuffix;
        IsLiveCodingPatch = isLiveCodingPatch;
    }
}

public sealed class UnrealDllNameParser
{
    // Regex matching confirmed UBT naming rules:
    // {AppPrefix}-{ModuleName}[-{HotReload}][-{Platform}-{Configuration}][-{HotReload}][-{Arch}].dll
    private static readonly Regex DllRegex = new(
        @"^(?<app>[A-Za-z0-9_]+)-(?<module>[A-Za-z0-9_]+)(?:-(?<hotreload1>\d{4}))?(?:-(?<platform>Win64)-(?<config>Debug|DebugGame|Development|Test|Shipping))?(?:-(?<hotreload2>\d{4}))?(?:-(?<arch>arm64|arm64ec))?\.dll$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Live Coding patches: {AppPrefix}-{ModuleName}[-{Platform}-{Configuration}].patch_{N}.(exe|dll)
    private static readonly Regex PatchRegex = new(
        @"^(?<app>[A-Za-z0-9_]+)-(?<module>[A-Za-z0-9_]+)(?:-[^.]+)?\.patch_\d+\.(?:exe|dll)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly HashSet<string> _knownAppPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "UnrealEditor",
        "UE4Editor",
        "UnrealGame",
        "UnrealClient",
        "UnrealServer"
    };

    public UnrealDllNameParser(IEnumerable<string>? customTargetPrefixes = null)
    {
        if (customTargetPrefixes != null)
        {
            foreach (var prefix in customTargetPrefixes)
            {
                if (!string.IsNullOrWhiteSpace(prefix))
                {
                    _knownAppPrefixes.Add(prefix.Trim());
                }
            }
        }
    }

    public void RegisterAppPrefix(string prefix)
    {
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            _knownAppPrefixes.Add(prefix.Trim());
        }
    }

    public ModuleKey? TryParseLoadedModule(string modulePath)
    {
        return TryParse(modulePath, out var info) ? info.Key : null;
    }

    public bool TryParse(string modulePath, out ModuleParseInfo info)
    {
        info = null!;
        if (string.IsNullOrEmpty(modulePath)) return false;

        string fileName = Policy.WildcardPattern.ExtractFileNameSafe(modulePath);
        if (fileName.Length == 0) return false;

        // Fast-path prefix check: must contain a hyphen and match known prefix before hyphen
        int hyphenIndex = fileName.IndexOf('-');
        if (hyphenIndex <= 0)
        {
            return false;
        }

        string prefix = fileName.Substring(0, hyphenIndex);
        if (!_knownAppPrefixes.Contains(prefix))
        {
            return false;
        }

        // Live Coding patch check (.patch_0.exe or .patch_0.dll)
        if (fileName.IndexOf(".patch_", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var patchMatch = PatchRegex.Match(fileName);
            if (patchMatch.Success)
            {
                string app = patchMatch.Groups["app"].Value;
                string mod = patchMatch.Groups["module"].Value;
                var key = new ModuleKey(ModuleKind.Unreal, mod);
                info = new ModuleParseInfo(key, app, mod, null, null, null, null, isLiveCodingPatch: true);
                return true;
            }
        }

        // Standard DLL check
        if (!fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var match = DllRegex.Match(fileName);
        if (match.Success)
        {
            string app = match.Groups["app"].Value;
            string mod = match.Groups["module"].Value;
            string? platform = match.Groups["platform"].Success ? match.Groups["platform"].Value : null;
            string? config = match.Groups["config"].Success ? match.Groups["config"].Value : null;
            string? arch = match.Groups["arch"].Success ? match.Groups["arch"].Value : null;

            string? hotreload = null;
            if (match.Groups["hotreload1"].Success)
            {
                hotreload = match.Groups["hotreload1"].Value;
            }
            else if (match.Groups["hotreload2"].Success)
            {
                hotreload = match.Groups["hotreload2"].Value;
            }

            var key = new ModuleKey(ModuleKind.Unreal, mod);
            info = new ModuleParseInfo(key, app, mod, platform, config, arch, hotreload, isLiveCodingPatch: false);
            return true;
        }

        return false;
    }
}
