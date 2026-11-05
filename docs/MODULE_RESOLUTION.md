# Module Resolution

How a source file (for example the file a breakpoint is in) is mapped to a **module key**, how a key is mapped to **DLL file names**, and how a **loaded DLL** is mapped back to a key.

> [!IMPORTANT]
> The Unreal naming rules in §4 are **provisional**. They come from general knowledge of UE 5.x and must be checked against the Unreal Build Tool source in `D:\UnrealEngine` once it has finished downloading. Task: [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) Phase 2. Record the confirmed rules, with file and line references into the UBT source, in §4 and §8 of this document, and update the golden test cases.

---

## 1. Interfaces (Core library, no VS dependencies)

```csharp
public enum ModuleKind { Unreal, MsBuild, External }

public readonly record struct ModuleKey(ModuleKind Kind, string Name); // Name compared case-insensitively

public interface ISourceToModuleResolver
{
    // Returns zero or more keys. Empty result = unresolved.
    IReadOnlyList<ModuleKey> Resolve(string sourceFilePath);
}

public interface IModuleBinaryNaming
{
    // Exact file names the key may appear as (used for the VS include list).
    IReadOnlyList<string> GetCandidateFileNames(ModuleKey key);

    // Reverse map from a loaded module's full path. Null if it's not one of ours.
    ModuleKey? TryParseLoadedModule(string modulePath);
}
```

A `CompositeResolver` tries the `UnrealResolver` first, when the solution is Unreal, then the `MsBuildResolver`. All resolvers cache their results and are thread-safe.

---

## 2. Unreal: source file → module name

### 2.1 Generated and intermediate files

If the path contains `\Intermediate\Build\<Platform>\<TargetOrArch...>\Inc\<Module>\` or `\Intermediate\Build\<Platform>\...\<Target>\<Configuration>\<Module>\` (unity files, `Module.<Name>.cpp`, `.gen.cpp`), take `<Module>` from the path. *(Verify the exact folder layouts in UE 5.x. UHT output is `Inc/<Module>/UHT/` in UE 5.1+.)*

### 2.2 Build.cs walk-up

```
dir = folder of file
loop:
    if dir contains exactly one "*.Build.cs" → module = file name minus ".Build.cs"; done
    if dir contains several "*.Build.cs" → pick the one whose name matches dir name; else log a warning and pick the first in ordinal order
    if dir contains "*.uplugin" or "*.uproject", or dir is the Engine root, or dir is the drive root → unresolved
    dir = parent(dir)
```

- Cache `directory → Build.cs path` (including negative results) in a `ConcurrentDictionary`. Invalidate the cache when `*.Build.cs` files are created or deleted (a `FileSystemWatcher` on the solution's source roots, filtered to `*.Build.cs`), or when the solution reloads.
- Parsing the `.Build.cs` contents is **not** required to get the name: UBT requires the class name to equal the file name. Content parsing is only needed if Phase 2 finds that some `ModuleRules` property affects the binary name (candidates: `BinariesSubFolder`, `ShortName`, `bUsePrecompiled`, `PrecompileForTargets`). If it does, use a lightweight regex over the file, never compile it.
- Ignore `*.Target.cs` (those are targets, not modules).
- `ThirdParty` external modules (`Type = ModuleType.External`) have no DLL of their own. In v1 they resolve to their folder's module key, which never matches a loaded DLL, so they're simply inert. Optional later improvement: detect `Type = ModuleType.External` and log it.

### 2.3 Module owner (root) detection

For naming and binaries folders, also determine which root owns the module:

| Owner | How to detect (nearest ancestor) | Binaries folder |
|---|---|---|
| Engine plugin | `*.uplugin`, under the Engine root | `<PluginDir>/Binaries/Win64` |
| Project plugin | `*.uplugin`, under the project root | `<PluginDir>/Binaries/Win64` |
| Project | folder containing `*.uproject` | `<ProjectDir>/Binaries/Win64` |
| Engine | `Engine/` (has `Build/Build.version`) | `Engine/Binaries/Win64` |

Also handle `Engine/Platforms/<Platform>/...`, `Engine/Restricted/<Kind>/...` and `<Project>/Platforms/...` trees, which have their own `Source` and `Plugins` folders. *(Verify where UBT puts their binaries.)*

---

## 3. Unreal: `.modules` manifests (preferred source of truth)

UBT writes a JSON manifest next to the binaries in each binaries folder. Typical name: `UnrealEditor.modules`; a config-suffixed variant such as `UnrealEditor-Win64-DebugGame.modules` exists for non-default configurations. Shape:

```json
{
  "BuildId": "37670630",
  "Modules": {
    "MyGame": "UnrealEditor-MyGame.dll",
    "MyGameEditor": "UnrealEditor-MyGameEditor.dll"
  }
}
```

- When resolving names for module `M` owned by root `R`: read every `*.modules` file in `R`'s binaries folder, collect every file name mapped to `M` (there can be one per configuration), and return them all as candidates.
- Cache each manifest keyed by path and last-write time.
- The runtime engine (`FModuleManager`) uses these manifests to find module DLLs. Phase 2 must confirm this in `Engine/Source/Runtime/Core/Private/Modules/ModuleManager.cpp` and `ModuleManifest.cs` in UBT, including how the manifest file name is formed for each configuration.
- If no manifest exists (module never built), fall back to the naming rules in §4.

---

## 4. Unreal: naming rules (fallback) — PROVISIONAL

Expected pattern for **modular** (non-monolithic) targets on Win64:

```
{AppPrefix}-{ModuleName}[-{Platform}-{Configuration}].dll
```

| Part | Provisional rule | Verify in UBT |
|---|---|---|
| `AppPrefix` | `UnrealEditor` for editor targets in UE5 (`UE4Editor` in UE4). `UnrealGame` for modular game targets. With a **unique build environment** (`BuildEnvironment = TargetBuildEnvironment.Unique`), engine modules may be named after the target instead (for example `MyGameEditor-Engine.dll`) and placed in the project's binaries folder. | `UEBuildTarget.cs` (`MakeBinaryFileName`, `AppName`, `GetAppNameForTarget`?), `TargetRules.cs` (`BuildEnvironment`, `bUseUniqueBuildEnvironment`) |
| `ModuleName` | The UBT module name, unchanged. | `UEBuildModuleCPP.cs`, `UEBuildBinary.cs` |
| Config suffix | Omitted when the binary's configuration equals the target's `UndecoratedConfiguration` (default `Development`). Otherwise `-Win64-<Config>`. In **DebugGame** editor builds, engine modules are built in Development (no suffix) while project modules get `-Win64-DebugGame`. | `UEBuildTarget.cs` (`MakeBinaryFileName`, `UndecoratedConfiguration`), handling of DebugGame per module (`UEBuildModule` / `bTreatAsEngineModule`?) |
| Architecture suffix | None for x64 by default. Check arm64/arm64ec naming. | `UnrealArch`, `MakeBinaryFileName` |
| Hot reload | `{normal name minus .dll}-{NNNN}.dll`, a 4-digit random/sequence suffix. | `HotReload.cs` / `UEBuildTarget.cs` (`HotReloadModuleNameSuffix`?) |
| Live Coding patches | Live++ creates patch images for modified modules. Exact naming (`*.patch_N.exe`?), location, and whether they show up as normal module loads with their own PDB need to be confirmed. | `Engine/Source/Developer/Windows/LiveCoding/`, `Engine/Source/Programs/UnrealBuildTool/...LiveCoding...` |
| Monolithic | Everything in one EXE: `{TargetName}[-Win64-{Config}].exe`. The extension then has nothing to do except the EXE. | `TargetRules.LinkType`, `bShouldCompileAsDLL` |
| Program targets | `Engine/Binaries/Win64/<Program>.exe`, usually monolithic. | — |

Phase 2 checklist (when `D:\UnrealEngine` is available):
- [ ] Read `Engine/Build/Build.version` and record the engine version here.
- [ ] Confirm `MakeBinaryFileName` logic and quote the relevant code (with paths) in §8.
- [ ] Confirm how DebugGame decides per module between the decorated and undecorated name.
- [ ] Confirm unique build environment naming and output location.
- [ ] Confirm `.modules` manifest file naming per configuration and its JSON schema.
- [ ] Confirm the hot reload suffix format.
- [ ] Confirm Live Coding patch file naming and PDB naming.
- [ ] Check whether any `ModuleRules` field changes the binary name or folder (`BinariesSubFolder`, precompiled modules in installed builds).
- [ ] Confirm the Intermediate folder layouts used in §2.1.
- [ ] Add golden test cases (§7) for every confirmed rule.

---

## 5. Unreal: loaded DLL → module key (reverse mapping)

Module names are C# identifiers, so they never contain `-`. That makes splitting on `-` safe.

Provisional regex, matched against the file name (case-insensitive):

```
^(?<app>[A-Za-z0-9_]+)-(?<module>[A-Za-z0-9_]+)
 (?:-(?<platform>Win64)-(?<config>Debug|DebugGame|Development|Test|Shipping))?
 (?:-(?<hotreload>\d{4}))?
 \.dll$
```

- `app` must be one of the known prefixes for this solution: `UnrealEditor`, `UE4Editor`, `UnrealGame`, or a target name found in `*.Target.cs` files of the project. This avoids misreading third-party DLLs such as `api-ms-win-core-...dll`.
- The result is `ModuleKey(Unreal, module)`.
- Live Coding patch names get their own pattern once confirmed.
- Unit-test this exhaustively. It runs for every one of the ~800 module loads, so it must be fast: precompiled `Regex` or a hand-written parser, plus an allocation-free fast path that rejects names without a known prefix.

---

## 6. MSBuild (non-Unreal) resolution

1. `DTE.Solution.FindProjectItem(path)` → `ContainingProject`. Also check other projects, because a file can be shared. Using `IVsSolution` and `IVsProject.IsDocumentInProject` over all C++ projects is the robust route.
2. For each project, read `TargetPath` for the active configuration and platform through `IVsBuildPropertyStorage.GetPropertyValue("TargetPath", configName, PersistStorageType.PST_PROJECT_FILE)`, or `VCConfiguration.Evaluate("$(TargetPath)")` (VCProjectEngine).
3. If `ConfigurationType == StaticLibrary`: find the projects that reference it (`ProjectReference` items, through VCProjectEngine `VCReferences`) transitively, and use their outputs.
4. Ignore **Makefile/NMake projects** here. Unreal's generated projects are NMake, and their output is the editor EXE. Unreal solutions must go through the Unreal resolver.
5. Cache per (project, configuration). Invalidate on solution, project or configuration change events.
6. Keys: `ModuleKey(MsBuild, Path.GetFileName(TargetPath))`. Reverse mapping: a loaded module whose file name equals a known key.

All of this needs the UI thread and VS services. Implement it behind an `IProjectModelProvider` interface in the VSIX project, so the Core logic stays unit-testable.

---

## 7. Golden test cases (to extend in Phase 2)

| Input | Expected |
|---|---|
| `Engine/Source/Runtime/Engine/Private/Actor.cpp` | `Unreal:Engine` |
| `Engine/Source/Runtime/Core/Public/Containers/Array.h` | `Unreal:Core` |
| `MyProj/Source/MyGame/Private/MyActor.cpp` | `Unreal:MyGame` |
| `MyProj/Plugins/Foo/Source/FooEditor/Private/X.cpp` | `Unreal:FooEditor` |
| `MyProj/Intermediate/Build/Win64/UnrealEditor/Inc/MyGame/UHT/MyActor.gen.cpp` | `Unreal:MyGame` |
| `Engine/Source/ThirdParty/zlib/...` | Unresolved or External, no DLL match |
| Loaded `UnrealEditor-Engine.dll` | `Unreal:Engine` |
| Loaded `UnrealEditor-MyGame-Win64-DebugGame.dll` | `Unreal:MyGame` |
| Loaded `UnrealEditor-MyGame-0042.dll` | `Unreal:MyGame` |
| Loaded `api-ms-win-core-synch-l1-2-0.dll` | null |
| Loaded `kernel32.dll` | null |
| `Unreal:MyGame` candidates, Development editor, manifest present | `UnrealEditor-MyGame.dll` |
| `Unreal:MyGame` candidates, DebugGame editor | `UnrealEditor-MyGame-Win64-DebugGame.dll` (and the Development name if both manifests exist) |

---

## 8. Confirmed UBT findings

*(Empty. Fill in during Phase 2 with code references, for example `Engine/Source/Programs/UnrealBuildTool/Configuration/UEBuildTarget.cs: MakeBinaryFileName`.)*

