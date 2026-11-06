# Unreal Module Resolution

How a source file (for example the file a breakpoint is in) is mapped to an Unreal Engine module name, how a module name is mapped to candidate DLL file names, and how solutions are detected.

> [!NOTE]
> The Unreal naming rules were **confirmed in Phase 2** against Unreal Engine 5.8.4 at `D:\UnrealEngine`. See §7 for full code citations.

---

## 1. Engine Component: `UnrealModuleResolver` (Core library)

Located in `VSOnDemandSymbols.Core.Modules`:

```csharp
public sealed class UnrealModuleResolver
{
    // Resolves a C++ source file path (including intermediate UHT and unity files) to its owning module name
    public string? ResolveModuleFromSource(string sourceFilePath);

    // Returns candidate binary names (e.g. UnrealEditor-MyGame.dll, UnrealEditor-MyGame-Win64-DebugGame.dll, UnrealEditor-MyGame*.dll)
    public IReadOnlyList<string> GetCandidateBinaries(string moduleName);

    // Checks whether a directory is an Unreal Engine solution (contains *.uproject or Engine/Build/Build.version)
    public bool IsUnrealSolution(string solutionDir);
}
```

This resolver is thread-safe, caches directory walk-up results, and has zero Visual Studio dependencies.

---

## 2. Unreal: source file → module name

### 2.1 Generated and intermediate files

- **UHT Generated Code**: Resides under `<BaseDir>/Intermediate/[PlatformIntermediateFolderNoArch]/[AppName]/Inc/[BinariesSubFolder/][ModuleName]/UHT/` (e.g. `Intermediate/Build/Win64/UnrealEditor/Inc/Core/UHT/MyActor.gen.cpp` or `Intermediate/Build/Win64/UnrealEditor/Inc/SubFolder/Core/UHT/MyActor.gen.cpp`). The directory immediately preceding `UHT` (or following `Inc`) is the `<Module>` name.
- **Unity Build & Module Intermediate Files**: Reside under `<BaseDir>/Intermediate/[Platform]/[Arch/][Target]/[Config]/[ModuleName]/` and are named `Module.<ModuleName>.cpp` (single file), `Module.<ModuleName>.<N>.cpp` (partitioned unity, UE 5.6+), `Module.<ModuleName>.<N>_of_<Total>.cpp` (pre-UE 5.6), `PerModuleInline.gen.cpp`, or `Definitions.<ModuleName>.h`. Module name is extracted from the file name or parent `<ModuleName>` directory.

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
- Parsing the `.Build.cs` contents is **not** required to get the name: UBT requires the class name to equal the file name. `ShortName` in `ModuleRules` only affects intermediate directories (not the binary output DLL name). `BinariesSubFolder` places the output DLL into a subfolder of `Binaries/<Platform>/` (e.g. `Binaries/Win64/<SubFolder>/`), but the DLL name itself still adheres to the standard binary naming rule.
- Ignore `*.Target.cs` (those are targets, not modules).
- `ThirdParty` external modules (`Type = ModuleType.External`) have no DLL of their own and are inert.

### 2.3 Module owner (root) detection

For naming and binaries folders, also determine which root owns the module:

| Owner | How to detect (nearest ancestor) | Binaries folder |
|---|---|---|
| Engine plugin | `*.uplugin`, under the Engine root | `<PluginDir>/Binaries/Win64` |
| Project plugin | `*.uplugin`, under the project root | `<PluginDir>/Binaries/Win64` |
| Project | folder containing `*.uproject` | `<ProjectDir>/Binaries/Win64` |
| Engine | `Engine/` (has `Build/Build.version`) | `Engine/Binaries/Win64` |

Also handle `Engine/Platforms/<Platform>/...`, `Engine/Restricted/<Kind>/...` and `<Project>/Platforms/...` trees (discovered via `Unreal.GetExtensionDirs`). In shared builds, their modules output to the engine or project's main `Binaries/Win64/` (or `<PluginDir>/Binaries/Win64`).

---

## 3. Unreal: `.modules` manifests (preferred source of truth)

UBT writes a JSON manifest next to the binaries in each binaries folder (`Engine/Binaries/Win64`, `<PluginDir>/Binaries/Win64`, `<ProjectDir>/Binaries/Win64`).
Filename format (`ModuleManifest.GetStandardFileName`):
- `Development` configuration (and engine directory for `DebugGame`): `<AppName>.modules` (e.g. `UnrealEditor.modules`).
- Other configurations (or project directory for `DebugGame`): `<AppName>-<Platform>-<Configuration>.modules` (e.g. `UnrealEditor-Win64-DebugGame.modules`, `UnrealEditor-Win64-Debug.modules`).

JSON Schema:
```json
{
  "BuildId": "37670630",
  "Modules": {
    "MyGame": "UnrealEditor-MyGame.dll",
    "MyGameEditor": "UnrealEditor-MyGameEditor.dll"
  },
  "LibraryDependencies": {}
}
```

- When resolving names for module `M` owned by root `R`: read every `*.modules` file in known binaries folders (`<ProjectDir>/Binaries/Win64`, `<ProjectDir>/Plugins/**/Binaries/Win64`, `Engine/Binaries/Win64`), collect every file name mapped to `M` (there can be one per configuration), and union them with the standard UBT naming candidates (§4) so switching configurations (e.g. between `Development Editor` and `DebugGame Editor`) resolves `-Win64-DebugGame.dll` even before a new `.modules` file has been written.
- Cache each manifest keyed by path and last-write time.
- The runtime engine (`FModuleManager::FindModulePaths`) reads the main engine manifest via `FModuleManifest::TryRead` to establish the current session's `BuildId`, then scans all engine/game/plugin binary directories, rejecting manifests whose `BuildId` does not match (except Bridge plugin) before caching valid module DLL paths.
- If no manifest exists (module never built), fall back to the naming rules in §4.

---

## 4. Unreal: naming rules (fallback) — CONFIRMED (UE 5.8.4)

Pattern for **modular** (non-monolithic) targets on Windows:

```
{AppPrefix}-{ModuleName}[-{Platform}-{Configuration}][{Arch}].dll
```

| Part | Confirmed rule | UBT Source |
|---|---|---|
| `AppPrefix` | `UnrealEditor` for editor targets; `UnrealGame` for modular games; `UnrealClient` for client; `UnrealServer` for server (`UE4Editor` in UE4). With a **unique build environment** (`BuildEnvironment = TargetBuildEnvironment.Unique`), `AppName` defaults to `TargetName` (e.g. `MyGame`), and binaries output to `<ProjectDirectory>\Binaries\<Platform>\`. | `UEBuildTarget.cs: 1912-1923, 2039-2054, 5328-5336` |
| `ModuleName` | The UBT module name, unchanged. | `UEBuildTarget.cs: 5400, 5473` |
| Config suffix | Omitted when the binary's configuration equals `UndecoratedConfiguration` (defaults to `Development`). Otherwise `-Win64-<Config>`. In **DebugGame** editor builds: engine modules are built in `Development` (`bCanBuildDebugGame` is false) and omit the suffix (`UnrealEditor-Core.dll`), while project and local plugin modules retain `DebugGame` and receive `-Win64-DebugGame` (`UnrealEditor-MyGame-Win64-DebugGame.dll`). | `TargetRules.cs: 810-813`, `UEBuildTarget.cs: 5392-5397, 5491-5497`, `RulesCompiler.cs: 313-314, 454-456` |
| Architecture suffix | None for x64 on Windows (`RequiresArchitectureFilenames` returns false). Arm64 / Arm64ec append `arm64` or `arm64ec` (`UnrealEditor-MyGame-arm64.dll` or `UnrealEditor-MyGame-Win64-Development-arm64.dll`). | `UEBuildWindows.cs: 1227-1245`, `UEBuildTarget.cs: 5499-5503` |
| Hot reload | Formatted as `"{0}-{1:D4}{2}"` where `{1:D4}` is a 4-digit sequence counter inserted immediately after `{ModuleName}` (e.g. `UnrealEditor-Core-0001.dll` or `UnrealEditor-MyGame-0001-Win64-DebugGame.dll`). Runtime generates identical format via `FModuleManager::MakeUniqueModuleFilename`. | `HotReload.cs: 819-849`, `ModuleManager.cpp: 2066-2092` |
| Live Coding patches | Live++ creates patch executables/DLLs and PDBs: `<Module>.patch_N.exe` and `<Module>.patch_N.pdb` (e.g. `UnrealEditor-MyGame.patch_0.exe`), located next to original binary in `Binaries/Win64`. Loaded into process via standard Win32 `::LoadLibraryW`, triggering standard module load debug events (`LOAD_DLL_DEBUG_EVENT`). `FLiveCodingModule::IsPatchDll` filters them from engine module registry. | `LC_LiveModule.cpp: 2994-3017`, `LC_ClientCommandActions.cpp: 128-154`, `LiveCodingModule.cpp: 1617-1658` |
| Monolithic | Monolithic targets link all modules into `{TargetName}[-Win64-{Config}].exe` (or `.dll` when `bShouldCompileAsDLL` is true). | `TargetRules.cs: 918-922, 2686-2690`, `UEBuildTarget.cs: 5277-5291` |
| Program targets | Monolithic executable under `Engine/Binaries/Win64/<Program>.exe`. | `UEBuildTarget.cs: 1917` |

Phase 2 checklist (confirmed against `D:\UnrealEngine`):
- [x] Read `Engine/Build/Build.version` and record the engine version (UE 5.8.4).
- [x] Confirm `MakeBinaryFileName` logic and quote the relevant code in §8.
- [x] Confirm how DebugGame decides per module between decorated and undecorated name.
- [x] Confirm unique build environment naming and output location.
- [x] Confirm `.modules` manifest file naming per configuration and its JSON schema.
- [x] Confirm the hot reload suffix format (`-{NNNN}` 4-digit zero-padded).
- [x] Confirm Live Coding patch file naming (`.patch_N.exe`) and PDB naming (`.patch_N.pdb`).
- [x] Check whether any `ModuleRules` field changes binary name or folder (`BinariesSubFolder` sets subfolder; `ShortName` is intermediate only).
- [x] Confirm the Intermediate folder layouts used in §2.1 (UHT subfolder and Unity naming variants).
- [x] Add golden test cases (§7) for every confirmed rule.

---

## 5. Unreal: loaded DLL → module key (reverse mapping)

Module names are C# identifiers, so they never contain `-`. That makes splitting on `-` safe.

Confirmed regex / parser logic, matched against the file name (case-insensitive):

```
^(?<app>[A-Za-z0-9_]+)-(?<module>[A-Za-z0-9_]+)
 (?:-(?<hotreload1>\d{4}))?
 (?:-(?<platform>Win64)-(?<config>Debug|DebugGame|Development|Test|Shipping))?
 (?:-(?<hotreload2>\d{4}))?
 (?:-(?<arch>arm64|arm64ec))?
 \.dll$
```

- `app` must be one of the known prefixes: `UnrealEditor`, `UE4Editor`, `UnrealGame`, `UnrealClient`, `UnrealServer`, or a custom target name from `*.Target.cs` in unique build environments. Third-party DLLs such as `api-ms-win-core-...dll` are rejected on fast-path.
- Live Coding patch binaries match:
```
^(?<app>[A-Za-z0-9_]+)-(?<module>[A-Za-z0-9_]+).*\.patch_\d+\.(?:exe|dll)$
```
- The result maps to `ModuleKey(ModuleKind.Unreal, module)`.

- `app` must be one of the known prefixes for this solution: `UnrealEditor`, `UE4Editor`, `UnrealGame`, or a target name found in `*.Target.cs` files of the project. This avoids misreading third-party DLLs such as `api-ms-win-core-...dll`.
- The result is `ModuleKey(Unreal, module)`.
- Live Coding patch names get their own pattern once confirmed.
- Unit-test this exhaustively. It runs for every one of the ~800 module loads, so it must be fast: precompiled `Regex` or a hand-written parser, plus an allocation-free fast path that rejects names without a known prefix.

---

## 6. Golden test cases (confirmed in Phase 2)

| Input | Expected |
|---|---|
| `Engine/Source/Runtime/Engine/Private/Actor.cpp` | `Unreal:Engine` |
| `Engine/Source/Runtime/Core/Public/Containers/Array.h` | `Unreal:Core` |
| `MyProj/Source/MyGame/Private/MyActor.cpp` | `Unreal:MyGame` |
| `MyProj/Plugins/Foo/Source/FooEditor/Private/X.cpp` | `Unreal:FooEditor` |
| `MyProj/Intermediate/Build/Win64/UnrealEditor/Inc/MyGame/UHT/MyActor.gen.cpp` | `Unreal:MyGame` |
| `MyProj/Intermediate/Build/Win64/UnrealEditor/Inc/SubFolder/MyGame/UHT/MyActor.gen.cpp` | `Unreal:MyGame` |
| `MyProj/Intermediate/Build/Win64/UnrealEditor/Development/MyGame/Module.MyGame.1_of_4.cpp` | `Unreal:MyGame` |
| `MyProj/Intermediate/Build/Win64/UnrealEditor/Development/MyGame/Module.MyGame.2.cpp` | `Unreal:MyGame` |
| `Engine/Source/ThirdParty/zlib/...` | Unresolved or External, no DLL match |
| Loaded `UnrealEditor-Engine.dll` | `Unreal:Engine` |
| Loaded `UnrealEditor-MyGame-Win64-DebugGame.dll` | `Unreal:MyGame` |
| Loaded `UnrealEditor-MyGame-0042.dll` | `Unreal:MyGame` |
| Loaded `UnrealEditor-MyGame-0001-Win64-DebugGame.dll` | `Unreal:MyGame` |
| Loaded `UnrealEditor-MyGame-Win64-Development-arm64.dll` | `Unreal:MyGame` |
| Loaded `UnrealEditor-MyGame.patch_0.exe` | `Unreal:MyGame` |
| Loaded `UnrealClient-MyGame.dll` | `Unreal:MyGame` |
| Loaded `UnrealServer-MyGame.dll` | `Unreal:MyGame` |
| Loaded `api-ms-win-core-synch-l1-2-0.dll` | null |
| Loaded `kernel32.dll` | null |
| `Unreal:MyGame` candidates, Development editor, manifest present | `UnrealEditor-MyGame.dll` |
| `Unreal:MyGame` candidates, DebugGame editor | `UnrealEditor-MyGame-Win64-DebugGame.dll` (and the Development name if both manifests exist) |

---

## 7. Confirmed UBT findings

Verified against Unreal Engine 5.8.4 (`D:\UnrealEngine\Engine\Build\Build.version`: `MajorVersion: 5`, `MinorVersion: 8`, `PatchVersion: 4`, `CompatibleChangelist: 55116800`, `BranchName: "UE5"`).

### 8.1 `MakeBinaryFileName` & Output Binary Construction
- **Files**: `Engine/Source/Programs/UnrealBuildTool/Configuration/UEBuildTarget.cs` (lines 5473–5508) and caller in `CreateDynamicLibraryForModule` (line 5400).
- **Rule**: Binary name is built by prefixing optional `"lib"` (Unix/Apple/Android only), appending `BinaryName` (`"{AppName}{Separator}{ModuleName}"` where `Separator = "-"`), appending `"-{Platform}-{Configuration}"` if `Configuration != UndecoratedConfiguration`, appending architecture if required by `ArchitectureConfig`, and appending `.dll` on Windows.
- **Code Reference**:
```csharp
// UEBuildTarget.cs: 5473-5508
public static string MakeBinaryFileName(string BinaryName, string Separator, UnrealTargetPlatform Platform, UnrealTargetConfiguration Configuration, UnrealArchitectures Architectures, UnrealTargetConfiguration UndecoratedConfiguration, UEBuildBinaryType BinaryType)
{
	StringBuilder Result = new StringBuilder();

	bool bBinaryIsLibrary = BinaryType is UEBuildBinaryType.DynamicLinkLibrary or UEBuildBinaryType.StaticLibrary;
	bool bRequiresLibraryFilenamePrefix = UEBuildPlatform.IsPlatformInGroup(Platform, UnrealPlatformGroup.Android) ||
	                                      UEBuildPlatform.IsPlatformInGroup(Platform, UnrealPlatformGroup.Apple) ||
	                                      UEBuildPlatform.IsPlatformInGroup(Platform, UnrealPlatformGroup.Linux);
	if (bBinaryIsLibrary && bRequiresLibraryFilenamePrefix)
	{
		Result.Append("lib");
	}

	Result.Append(BinaryName);

	if (Configuration != UndecoratedConfiguration)
	{
		Result.Append(Separator);
		Result.Append(Platform);
		Result.Append(Separator);
		Result.Append(Configuration);
	}

	UEBuildPlatform BuildPlatform = UEBuildPlatform.GetBuildPlatform(Platform);
	if (BuildPlatform.ArchitectureConfig.RequiresArchitectureFilenames(Architectures))
	{
		Result.Append(Architectures.ToString());
	}

	Result.Append(BuildPlatform.GetBinaryExtension(BinaryType));

	return Result.ToString();
}
```

### 8.2 AppPrefix Selection & Unique Build Environment
- **Files**: `Engine/Source/Programs/UnrealBuildTool/Configuration/UEBuildTarget.cs` (lines 1912–1923, 2039–2054, 5328–5336).
- **Rule**: In shared builds (`bUseSharedBuildEnvironment = true`), `AppName` is mapped from target type: `Editor -> UnrealEditor`, `Game -> UnrealGame`, `Client -> UnrealClient`, `Server -> UnrealServer`, `Program -> TargetName`. In unique builds (`BuildEnvironment == TargetBuildEnvironment.Unique`), `AppName` defaults to `TargetName` (e.g. `MyProject`), and `BaseOutputDirectory` falls back to `ProjectDirectory`, placing modular DLLs under `<ProjectDirectory>\Binaries\<Platform>\`.
- **Code Reference**:
```csharp
// UEBuildTarget.cs: 1912-1923
bUseSharedBuildEnvironment = (Rules.BuildEnvironment == TargetBuildEnvironment.Shared);
if (bUseSharedBuildEnvironment)
{
	if (Rules.Type == TargetType.Program)
	{
		AppName = TargetName;
	}
	else
	{
		AppName = GetAppNameForTargetType(Rules.Type);
	}
}

// UEBuildTarget.cs: 2039-2054
public static string GetAppNameForTargetType(TargetType Type)
{
	switch (Type)
	{
		case TargetType.Game:
			return "UnrealGame";
		case TargetType.Client:
			return "UnrealClient";
		case TargetType.Server:
			return "UnrealServer";
		case TargetType.Editor:
			return "UnrealEditor";
		default:
			throw new BuildException("Invalid target type ({0})", (int)Type);
	}
}
```

### 8.3 UndecoratedConfiguration & DebugGame Per-Module Suffix Logic
- **Files**: `TargetRules.cs` (lines 810–813), `UEBuildTarget.cs` (lines 5392–5397, 5491–5497), `RulesCompiler.cs` (lines 313–314, 454–456).
- **Rule**: `TargetRules.UndecoratedConfiguration` defaults to `Development`. If `Configuration == UndecoratedConfiguration`, the `"-Platform-Configuration"` suffix is omitted. In `DebugGame`, project modules and local plugins have `Context.bCanBuildDebugGame = true` and receive `-Win64-DebugGame`, while engine modules (`!bCanBuildDebugGame`) are compiled in `Development` and omit the suffix entirely.
- **Code Reference**:
```csharp
// UEBuildTarget.cs: 5392-5397
// Get the configuration that this module will be built in. Engine modules compiled in DebugGame will use Development.
UnrealTargetConfiguration ModuleConfiguration = Configuration;
if (Configuration == UnrealTargetConfiguration.DebugGame && !Module.Rules.Context.bCanBuildDebugGame)
{
	ModuleConfiguration = UnrealTargetConfiguration.Development;
}
```

### 8.4 Architecture Suffixes
- **Files**: `Engine/Source/Programs/UnrealBuildTool/Platform/Windows/UEBuildWindows.cs` (lines 1227–1245), `UEBuildTarget.cs` (lines 5499–5503).
- **Rule**: On Windows, `WindowsArchitectureConfig.RequiresArchitectureFilenames` returns `false` for x64 (no architecture suffix is appended), but `true` for `Arm64` and `Arm64ec`, which append `"arm64"` or `"arm64ec"`.

### 8.5 Module Manifests (`.modules`) & Runtime Loading
- **Files**: `Engine/Source/Programs/UnrealBuildTool/System/ModuleManifest.cs` (lines 77–89, 153–182), `Engine/Source/Runtime/Core/Private/Modules/ModuleManager.cpp` (lines 1937–1955).
- **Rule**: Manifest name is `<AppName>.modules` for `Development` (and `DebugGame` engine directory); for other configurations it is `<AppName>-<Platform>-<Configuration>.modules`. The JSON contains `"BuildId"` and `"Modules"` `{ "<ModuleName>": "<DllFileName>" }`. At runtime, `FModuleManager` validates `Manifest.BuildId == BuildId.GetValue()` before adding module paths.

### 8.6 Hot Reload Suffix Format
- **Files**: `Engine/Source/Programs/UnrealBuildTool/System/HotReload.cs` (lines 819–849), `Engine/Source/Runtime/Core/Private/Modules/ModuleManager.cpp` (lines 2066–2092).
- **Rule**: Hot-reload binaries insert a 4-digit zero-padded sequence counter immediately after the module name: `"{0}-{1:D4}{2}"`, producing `UnrealEditor-<ModuleName>-0001.dll` or `UnrealEditor-<ModuleName>-0001-Win64-DebugGame.dll`.

### 8.7 Live Coding Patches
- **Files**: `Engine/Source/Developer/Windows/LiveCodingServer/Private/External/LC_LiveModule.cpp` (lines 2994–3017), `Engine/Source/Developer/Windows/LiveCoding/Private/External/LC_ClientCommandActions.cpp` (lines 128–154), `Engine/Source/Developer/Windows/LiveCoding/Private/LiveCodingModule.cpp` (lines 1617–1658).
- **Rule**: Live++ creates patches formatted as `<Module>.patch_N.exe` and `<Module>.patch_N.pdb` in the same binaries folder as the original DLL. Loaded via standard Win32 `::LoadLibraryW(command->path)`, producing normal OS module load debug events (`LOAD_DLL_DEBUG_EVENT`). `FLiveCodingModule::IsPatchDll` filters `.patch_N` binaries from engine module registration.

### 8.8 Intermediate Directory Layouts
- **Files**: `UEBuildTarget.cs` (lines 6894–6905), `UEBuildModuleCPP.cs` (lines 330–334), `Unity.cs` (lines 302–315).
- **Rule**: UHT generated headers/translation units reside at `<BaseDir>/Intermediate/[Platform]/[AppName]/Inc/[BinariesSubFolder/][ModuleName]/UHT/`. Unity source files reside under the module's intermediate folder and are named `Module.[BaseName].cpp` or `Module.[BaseName].[N].cpp` (and historically `Module.[BaseName].[N]_of_[Total].cpp`).

### 8.9 Real Engine Build Inventory
- **Location**: `D:\UnrealEngine\Engine\Binaries\Win64` and `D:\UnrealEngine\Engine\Plugins\**\Binaries\Win64`
- **Manifests**: `UnrealEditor.modules` present with `BuildId: "be577d2f-927c-412d-9635-eb39abf985d7"`, plus `ShaderCompileWorker.modules`.
- **Binaries**: Exactly 2,284 `UnrealEditor-*.dll` files (563 in `Engine\Binaries\Win64` and 1,721 in `Engine\Plugins\**\Binaries\Win64`).
- **Hyphen Check**: 0 DLLs contain more than one hyphen after `UnrealEditor-`. 100% of DLL names map cleanly to standard `{AppPrefix}-{ModuleName}.dll`.

