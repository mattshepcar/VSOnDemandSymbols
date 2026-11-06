using System;
using System.Collections.Generic;
using System.IO;
using VSOnDemandSymbols.Core.Modules;
using VSOnDemandSymbols.Core.Policy;

namespace VSOnDemandSymbols.Debugger;

/// <summary>
/// Immutable, precomputed wanted-set snapshot published to <see cref="ConcordSymbolBridge"/>
/// so <see cref="ConcordSymbolMonitor.OnModuleInstanceLoad"/> can evaluate module loads in O(1)
/// with zero file I/O and zero UI-thread marshaling (NF-1).
/// </summary>
public sealed class WantedSetSnapshot
{
    public static readonly WantedSetSnapshot Empty = new(
        wantedKeys: new HashSet<ModuleKey>(),
        wantedFileNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        keyReasons: new Dictionary<ModuleKey, string>(),
        policy: new LoadPolicy(WildcardPattern.ParseMultiple("UnrealEditor.exe;UnrealEditor-Win64-*.exe")),
        dllParser: new UnrealDllNameParser(),
        manifestDllToModuleMap: new Dictionary<string, ModuleKey>(StringComparer.OrdinalIgnoreCase),
        isAllNativeScope: false,
        pinnedCount: 0,
        mruCount: 0,
        breakpointCount: 0,
        alwaysCount: 0,
        prunedCount: 0);

    private readonly HashSet<ModuleKey> _wantedKeys;
    private readonly HashSet<string> _wantedFileNames;
    private readonly Dictionary<ModuleKey, string> _keyReasons;
    private readonly Dictionary<string, ModuleKey> _manifestDllToModuleMap;

    public IReadOnlyCollection<ModuleKey> WantedKeys => _wantedKeys;
    public IReadOnlyCollection<string> WantedFileNames => _wantedFileNames;
    public IReadOnlyDictionary<ModuleKey, string> KeyReasons => _keyReasons;
    public LoadPolicy Policy { get; }
    public UnrealDllNameParser DllParser { get; }
    public bool IsAllNativeScope { get; }

    public int PinnedCount { get; }
    public int MruCount { get; }
    public int BreakpointCount { get; }
    public int AlwaysCount { get; }
    public int PrunedCount { get; }

    public WantedSetSnapshot(
        HashSet<ModuleKey> wantedKeys,
        HashSet<string> wantedFileNames,
        Dictionary<ModuleKey, string> keyReasons,
        LoadPolicy policy,
        UnrealDllNameParser dllParser,
        Dictionary<string, ModuleKey> manifestDllToModuleMap,
        bool isAllNativeScope,
        int pinnedCount,
        int mruCount,
        int breakpointCount,
        int alwaysCount,
        int prunedCount)
    {
        _wantedKeys = wantedKeys ?? throw new ArgumentNullException(nameof(wantedKeys));
        _wantedFileNames = wantedFileNames ?? throw new ArgumentNullException(nameof(wantedFileNames));
        _keyReasons = keyReasons ?? throw new ArgumentNullException(nameof(keyReasons));
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        DllParser = dllParser ?? throw new ArgumentNullException(nameof(dllParser));
        _manifestDllToModuleMap = manifestDllToModuleMap ?? throw new ArgumentNullException(nameof(manifestDllToModuleMap));
        IsAllNativeScope = isAllNativeScope;
        PinnedCount = pinnedCount;
        MruCount = mruCount;
        BreakpointCount = breakpointCount;
        AlwaysCount = alwaysCount;
        PrunedCount = prunedCount;
    }

    /// <summary>
    /// Maps a loaded DLL/EXE file name to a <see cref="ModuleKey"/> in memory without file I/O (NF-1).
    /// </summary>
    public ModuleKey? TryResolveLoadedModule(string fileNameOrPath)
    {
        if (string.IsNullOrEmpty(fileNameOrPath))
        {
            return null;
        }

        string fileName = Path.GetFileName(fileNameOrPath);

        ModuleKey? parsed = DllParser.TryParseLoadedModule(fileName);
        if (parsed.HasValue)
        {
            return parsed;
        }

        if (_manifestDllToModuleMap.TryGetValue(fileName, out ModuleKey manifestKey))
        {
            return manifestKey;
        }

        if (IsAllNativeScope)
        {
            return new ModuleKey(ModuleKind.MsBuild, fileName);
        }

        return null;
    }

    /// <summary>
    /// Evaluates whether symbols should be loaded for a module at module-load time (SPEC §5.7, NF-1).
    /// </summary>
    public bool ShouldLoadAtModuleLoad(string fileName, ModuleKey? key, out string reasonDescription)
    {
        reasonDescription = string.Empty;

        if (Policy.IsNeverLoad(fileName) || (key.HasValue && Policy.IsNeverLoad(key.Value)))
        {
            return false;
        }

        bool isKeyWanted = (key.HasValue && _wantedKeys.Contains(key.Value)) || _wantedFileNames.Contains(fileName);
        if (isKeyWanted)
        {
            if (key.HasValue && _keyReasons.TryGetValue(key.Value, out string? foundReason) && !string.IsNullOrEmpty(foundReason))
            {
                reasonDescription = foundReason!;
            }
            else
            {
                reasonDescription = "WantedSet";
            }

            return true;
        }

        if (Policy.IsAlwaysLoad(fileName) || (key.HasValue && Policy.IsAlwaysLoad(key.Value)))
        {
            reasonDescription = "AlwaysLoad";
            return true;
        }

        return false;
    }

    /// <summary>
    /// Creates a new immutable <see cref="WantedSetSnapshot"/> with an additional wanted module key
    /// (used when a trigger fires during a session for a module that is not loaded yet).
    /// </summary>
    public WantedSetSnapshot WithAddedWantedKey(
        ModuleKey key,
        string reasonDescription,
        IEnumerable<string>? candidateFileNames = null)
    {
        var newWantedKeys = new HashSet<ModuleKey>(_wantedKeys) { key };
        var newWantedFiles = new HashSet<string>(_wantedFileNames, StringComparer.OrdinalIgnoreCase);
        if (candidateFileNames != null)
        {
            foreach (string fn in candidateFileNames)
            {
                if (!string.IsNullOrWhiteSpace(fn))
                {
                    newWantedFiles.Add(Path.GetFileName(fn));
                }
            }
        }

        if (key.Kind == ModuleKind.MsBuild)
        {
            newWantedFiles.Add(key.Name);
        }

        var newReasons = new Dictionary<ModuleKey, string>(_keyReasons)
        {
            [key] = reasonDescription
        };

        return new WantedSetSnapshot(
            newWantedKeys,
            newWantedFiles,
            newReasons,
            Policy,
            DllParser,
            _manifestDllToModuleMap,
            IsAllNativeScope,
            PinnedCount,
            MruCount,
            BreakpointCount,
            AlwaysCount,
            PrunedCount);
    }
}

