# Implementation Plan (Clean-Slate Minimal Architecture)

This document is the actionable, phased implementation plan for the clean-slate architecture specified in [**`docs/SPEC.md`**](SPEC.md).

---

## Progress Tracker

- [x] **Phase 1: Project & Build Simplification (Strip Concord & MSBuild)**
  - [x] 1.1 Update `VSOnDemandSymbols.csproj`: remove `.vsdconfig` build targets, remove `.vsdconfigxml`, remove `Microsoft.VisualStudio.Debugger.Engine` assembly reference.
  - [x] 1.2 Update `source.extension.vsixmanifest`: remove `DebuggerEngineExtension` asset.
  - [x] 1.3 Remove obsolete files from `src/VSOnDemandSymbols/` and `src/VSOnDemandSymbols.Core/`.
  - [x] 1.4 Verify clean build with MSBuild.
- [x] **Phase 2: Core Engine & Unit Tests**
  - [x] 2.1 Implement `UnrealModuleResolver` in `VSOnDemandSymbols.Core` (directory walk-up to `*.Build.cs`, candidate binary names, UHT/unity paths, Unreal solution detection).
  - [x] 2.2 Verify / adapt `SymbolSettingsBackupStore` and `WildcardPattern`.
  - [x] 2.3 Update unit tests in `VSOnDemandSymbols.Core.Tests` and verify all tests pass (`dotnet run --project tests/VSOnDemandSymbols.Core.Tests --no-build`).
- [x] **Phase 3: VSIX Debugger Integration**
  - [x] 3.1 Implement `SymbolSettingsManager` (reads/writes `IVsDebuggerSymbolSettings120A`, applies manual mode, merges `IncludeList`, crash recovery).
  - [x] 3.2 Implement `DebuggerSessionMonitor` (hooks F5 launch via `CommandEvents.BeforeExecute`, updates `IncludeList` from `DTE.Debugger.Breakpoints`, hooks AD7 `IDebugBreakpointErrorEvent2` for live mid-session breakpoint binding, hooks `DebuggerEvents.OnEnterBreakMode` to load missing call-stack symbols via `Module.LoadSymbols`).
  - [x] 3.3 Connect `OutputLog` for clean diagnostic logging to the "On-Demand Symbols" pane.
- [x] **Phase 4: Package Lifecycle & Commands**
  - [x] 4.1 Update `VSOnDemandSymbolsPackage`: background load, C++ solution UI context rule, solution open/close listeners, crash recovery check on startup.
  - [x] 4.2 Streamline UI: transparent background execution; remove redundant menu toggle (D-23).
  - [x] 4.3 Verify full solution build (`/p:UseSharedCompilation=false /nr:false`).
- [x] **Phase 5: Verification & Documentation**
  - [x] 5.1 Run Core test suite.
  - [x] 5.2 Validate against `NativeSample` and write verification instructions for Unreal Editor.
  - [x] 5.3 Commit clean implementation to git with descriptive commit message.
- [x] **Phase 6: Configurable Break-Mode Symbol Loading (Call Stack & Threads)**
  - [x] **Step 6.1: Local Symbol Resolution without Config (Hardcoded Server Masking)**
    - [x] 6.1.1 Update `SymbolSettingsBackupDto` and `SymbolSettingsBackupStore` to record `UseMSSymbolServers`, `UseNugetSymbolServers`, and remote `SymbolPaths` states.
    - [x] 6.1.2 Implement scoped symbol server masking in `SymbolSettingsManager`: `ExecuteWithMaskedSymbolServers()`.
    - [x] 6.1.3 Extend `DebuggerSessionMonitor.OnEnterBreakMode` to gather un-symbolized modules from `CurrentThread.StackFrames` and top frames of `proc.Threads`.
    - [x] 6.1.4 Hook `DebuggerEvents.OnContextChanged` to handle thread switching in the Threads window and frame activation in the Call Stack window.
    - [x] 6.1.5 Ensure candidate modules are added to `IncludeList` during the masked pass so `IsManualLoad = true` permits native loading.
    - [x] 6.1.6 Verify that all locally available symbols (adjacent, local paths, cache) load without network traffic, and right-click "Load Symbols" still queries internet servers.
  - [x] **Step 6.2: Introduce Simple Configuration Variable**
    - [x] 6.2.1 Define `BreakSymbolLoadMode` enum in `VSOnDemandSymbols.Core` (`Off = 0`, `LocalOnly = 1` [Default], `All = 2`).
    - [x] 6.2.2 Wire `BreakSymbolLoadMode` into `DebuggerSessionMonitor` to branch between:
      - `Off`: Keep current behavior (no break symbol loading).
      - `LocalOnly`: Load local/cached symbols with server masking.
      - `All`: Load symbols with no server masking (allows remote servers).
    - [x] 6.2.3 Add environment variable override (`ODS_BREAK_SYMBOL_LOAD_MODE`) for immediate testing and verification without UI.
    - [x] 6.2.4 Add unit tests for configuration parsing and enum validation in `VSOnDemandSymbols.Core.Tests`.
  - [x] **Step 6.3: Expose Configuration in Visual Studio Extension Settings**
    - [x] 6.3.1 Register settings via modern Unified Settings manifest `registration.json` nested under `Debugging -> Symbols -> On Demand Loading` (`debugging.symbols.onDemandLoading`).
    - [x] 6.3.2 Register `[ProvideSettingsManifest]` on `VSOnDemandSymbolsPackage` (omitting legacy `[ProvideOptionPage]` and `legacyOptionPageId`).
    - [x] 6.3.3 Connect settings retrieval via `ISettingsReader` to `DebuggerSessionMonitor`.

---

## Detailed Phase Breakdown

### Phase 1: Project & Build Simplification

**Goal:** Strip out all Concord tooling, unmanaged IDE assembly references, and obsolete files so the solution builds as a pure, standard VS SDK extension.

1. **`src/VSOnDemandSymbols/VSOnDemandSymbols.csproj`:**
   - Remove `<VsdConfigXmlFiles Include="VSOnDemandSymbols.vsdconfigxml" />`.
   - Remove `<Reference Include="Microsoft.VisualStudio.Debugger.Engine">`.
   - Remove `<Import Project="$(VsInstallRoot)\VSSDK\VisualStudioIntegration\Tools\Bin\Microsoft.VSSDK.Debugger.VSDConfigTool.targets" ... />`.
   - Remove the `IncludeVsdConfigInVsix` target.
   - Keep only standard SDK packages (`Microsoft.VisualStudio.SDK 17.14.40265`, `Microsoft.VSSDK.BuildTools 18.9.820`, `Newtonsoft.Json 13.0.3`).
2. **`src/VSOnDemandSymbols/source.extension.vsixmanifest`:**
   - Remove `<Asset Type="DebuggerEngineExtension" Path="VSOnDemandSymbols.vsdconfig" />`.
   - Retain `<Asset Type="Microsoft.VisualStudio.VsPackage" ... />`.
3. **Delete Obsolete Files:**
   - Delete `src/VSOnDemandSymbols/VSOnDemandSymbols.vsdconfigxml`.
   - Delete Concord classes: `ConcordSymbolMonitor.cs`, `ConcordSymbolBridge.cs`, `LoadedModuleRegistry.cs`, `LoadedModuleInfo.cs`, `WantedSetSnapshot.cs`.
   - Delete MSBuild classes: `MsBuildResolver.cs`, `MsBuildProjectGraph.cs`, `MsBuildProjectInfo.cs`, `MsBuildConfigurationType.cs`, `ProjectModelProvider.cs`.
   - Delete obsolete `CompositeResolver.cs`, `Ad7BreakpointEventListener.cs`, `SessionTracker.cs`, `SymbolLoader.cs`.
4. **Verification:**
   - Run MSBuild. Ensure solution compiles cleanly.

---

### Phase 2: Core Engine & Unit Tests

**Goal:** Ensure the pure .NET Core library handles Unreal source $\to$ module $\to$ binary resolution and backup persistence with 100% test coverage.

1. **`UnrealModuleResolver.cs` in `VSOnDemandSymbols.Core/Modules/`:**
   - Implement `ResolveModuleFromSource(string sourceFilePath)`:
     - Check regex / path substrings for `Intermediate/Build/.../Inc/{Module}/...` and unity files (`Module.{Module}.*.cpp`).
     - Walk up parent directories to locate `{Module}.Build.cs`. Stop at boundaries (`*.uproject`, `Engine/Source`, drive root). Cache results.
   - Implement `GetCandidateBinaries(string moduleName)`:
     - Returns candidate binary patterns: `UnrealEditor-{moduleName}.dll`, `UnrealEditor-{moduleName}-Win64-DebugGame.dll`, `UnrealEditor-{moduleName}-Win64-Development.dll`, `UnrealEditor-{moduleName}*.dll`.
   - Implement `IsUnrealSolution(string solutionDir)`:
     - Checks for `*.uproject` or `Engine/Build/Build.version`.
2. **`SymbolSettingsBackupStore.cs` & `WildcardPattern.cs`:**
   - Verify implementations in `VSOnDemandSymbols.Core`. Retain JSON serialization and PID crash recovery logic.
3. **Tests in `tests/VSOnDemandSymbols.Core.Tests/`:**
   - Keep and adapt tests for `UnrealModuleResolver`, `SymbolSettingsBackupStore`, and `WildcardPattern`.
   - Run tests: `dotnet run --project tests/VSOnDemandSymbols.Core.Tests --no-build`.

---

### Phase 3: VSIX Debugger Integration

**Goal:** Implement the lean debugger integration components using standard Visual Studio SDK APIs.

1. **`SymbolSettingsManager.cs` in `VSOnDemandSymbols/Debugger/`:**
   - Accepts `IVsDebuggerSymbolSettingsManager120A`.
   - `ApplyManualMode(IEnumerable<string> wantedBinaries)`:
     - Saves disk backup if none exists.
     - Sets `IsManualLoad = true`, `IsLoadAdjacent = false`, `AutomaticSymbolLoad = false`.
     - Preserves user's original `IncludeList` and appends `wantedBinaries`.
     - Calls `SaveSymbolSettings(settings, loadSymbolsNow: false)`.
   - `RestoreOriginalSettings()`:
     - Restores original flags and `IncludeList` from backup.
     - Deletes backup file from disk.
   - `RecoverFromCrashIfNeeded()`:
     - Invokes `SymbolSettingsBackupStore.CheckCrashRecovery()`. If found, restores settings and logs.
2. **`DebuggerSessionMonitor.cs` in `VSOnDemandSymbols/Debugger/`:**
   - Constructor subscribes to:
     - `DTE.Events.CommandEvents["{5efc7975-14bc-11cf-9b2b-00aa00573819}", 295]` (F5 Launch).
     - `DTE.Events.DebuggerEvents.OnEnterBreakMode`.
   - On before launch:
     - Queries `DTE.Debugger.Breakpoints`.
     - Resolves each enabled breakpoint's `.File` via `UnrealModuleResolver`.
     - Collects `UnrealEditor.exe`, `UnrealEditor-Win64-*.exe`, and candidate DLLs.
     - Calls `SymbolSettingsManager.ApplyManualMode(wantedBinaries)`.
   - On enter break mode:
     - Inspects `DTE.Debugger.CurrentThread.StackFrames`.
     - For frames without symbols (`.ModuleName` ends in `.dll`), iterates `DTE.Debugger.DebuggedProcesses` and calls `EnvDTE90.Module.LoadSymbols("")`.
     - Logs symbol load to `OutputLog`.
3. **`OutputLog.cs`:**
   - Writes to the "On-Demand Symbols" Output pane using `IVsOutputWindowPane`.

---

### Phase 4: Package Lifecycle & Commands

**Goal:** Package initialization, auto-load rules, and transparent lifecycle management.

1. **`VSOnDemandSymbolsPackage.cs`:**
   - Register package with background loading and C++ solution UI context rule (`ProvideUIContextRule`).
   - In `InitializeAsync`:
     - Query debugger services (`IVsDebuggerSymbolSettingsManager120A`, `DTE2`, `IVsSolution`).
     - Check and run `SymbolSettingsManager.RecoverFromCrashIfNeeded()`.
     - Instantiate `UnrealModuleResolver`, `SymbolSettingsManager`, `DebuggerSessionMonitor`.
     - Subscribe to `IVsSolutionEvents` (`OnAfterOpenSolution`, `OnBeforeCloseSolution`).
   - Solution open: check if solution contains `*.uproject` or `Engine/Build/Build.version`. If so, activate session monitor and configure `IncludeList`.
   - Solution close / Package dispose: call `SymbolSettingsManager.RestoreOriginalSettings()`.
2. **Transparent UI (D-23):**
   - Zero UI clutter: the redundant menu toggle (`VSCommandTable.vsct`) is removed. The extension automatically activates for Unreal solutions and restores original settings when the solution is closed. Diagnostics are reported to the "On-Demand Symbols" Output pane.

---

### Phase 5: Verification & Done Criteria

1. **Build:** Run MSBuild from repo root. Must produce `VSOnDemandSymbols.vsix` with 0 warnings and 0 errors.
2. **Unit Tests:** Run `dotnet run --project tests/VSOnDemandSymbols.Core.Tests --no-build`. Must pass 100%.
3. **Safety Guarantee:** Confirm that closing a solution or crashing cleanly restores the user's symbol settings.

---

### Phase 6: Configurable Break-Mode Symbol Loading

**Goal:** Provide reliable call stack and thread view symbol resolution on debugger break without unrequested internet symbol server downloads, backed by a 3-way configuration (`Off`, `LocalOnly`, `All`).

#### Step 6.1: Local Symbol Resolution without Config (Hardcoded Server Masking)
1. **Settings Backup Extension:**
   - Update `SymbolSettingsBackupDto` and `SymbolSettingsBackupStore` to persist:
     - `UseMSSymbolServers` (`bool`)
     - `UseNugetSymbolServers` (`bool`)
     - `RemoteSymbolPaths` (`List<SymbolOptionBackupDto>` capturing remote URLs in `SymbolPaths`).
   - Ensure crash recovery restores these settings if VS terminates unexpectedly mid-session.
2. **Scoped Server Masking:**
   - In `SymbolSettingsManager`, implement a scoped masking helper:
     - Saves current remote server states.
     - Sets `UseMSSymbolServers = false`, `UseNugetSymbolServers = false`, and remote `SymbolPaths` to `IsEnabled = false`.
     - Merges wanted callstack/thread candidate module patterns into `IncludeList`.
     - Calls `SaveSymbolSettings(masked, loadSymbolsNow: false)`.
     - Executes the load action via `try / finally`.
     - In `finally`, restores the original symbol server states and calls `SaveSymbolSettings(original, loadSymbolsNow: false)`.
3. **Thread and Call Stack Enumeration:**
   - In `DebuggerSessionMonitor`:
     - Inspect `CurrentThread.StackFrames` (all frames).
     - Inspect top frames from all threads in `_dte.Debugger.CurrentProcess.Threads`.
     - Fix module extraction: strip `.dll`/`.exe`, remove address suffixes (`!0x...`), and match against `proc.Modules`.
     - Invoke `mod.LoadSymbols("")` inside the scoped masked pass.
4. **Context Switching:**
   - Subscribe to `_dte.Events.DebuggerEvents.OnContextChanged`.
   - When the user selects another thread in the Threads window or a frame in the Call Stack window, run the masked load pass for the newly activated thread's stack.
5. **Verification:**
   - Breaking into the process resolves local and cached symbols across threads and call stack.
   - Zero HTTP requests to internet symbol servers occur.
   - Right-clicking an unresolved module and selecting "Load Symbols" successfully downloads from internet symbol servers.

#### Step 6.2: Introduce Simple Configuration Variable
1. **Core Enum:**
   - Define `BreakSymbolLoadMode` in `VSOnDemandSymbols.Core`:
     - `Off = 0`: Disables break-mode symbol loading (current behavior).
     - `LocalOnly = 1` (Default): Loads local and cached symbols using temporary symbol server masking.
     - `All = 2`: Loads symbols without masking (queries configured internet symbol servers).
2. **Session Monitor Plumbing:**
   - Expose `BreakSymbolLoadMode` property on `DebuggerSessionMonitor`.
   - Branch in `OnEnterBreakMode` and `OnContextChanged`:
     - If `Off`: Return early.
     - If `LocalOnly`: Execute masked local load pass.
     - If `All`: Execute load pass without masking servers.
3. **Immediate Testability:**
   - Read `Environment.GetEnvironmentVariable("ODS_BREAK_SYMBOL_LOAD_MODE")` as fallback to allow instant manual testing and automated unit testing before UI settings are implemented.
4. **Unit Tests:**
   - Add unit tests verifying enum parsing, defaults, and backup store serialization in `VSOnDemandSymbols.Core.Tests`.

#### Step 6.3: Expose Configuration in Visual Studio Extension Settings
1. **Unified Settings Manifest:**
   - Define settings declaratively in `UnifiedSettings/registration.json` with a single top-level category `"On-Demand Symbols"`.
   - Expose property `onDemandSymbols.breakSymbolLoadMode` as an enum (`LocalOnly`, `Off`, `All`) with user-friendly item labels.
   - Omit `legacyOptionPageId` so Visual Studio renders modern native UI controls directly without legacy migration warnings.
2. **Package Registration:**
   - Decorate `VSOnDemandSymbolsPackage` with `[ProvideSettingsManifest(PackageRelativeManifestFile = @"UnifiedSettings\registration.json")]`.
3. **Settings Synchronization:**
   - On package initialization, acquire `ISettingsReader` from `Microsoft.VisualStudio.Utilities.UnifiedSettings`.
   - Read initial setting and subscribe to changes via `SubscribeToChanges`, updating `DebuggerSessionMonitor.BreakSymbolLoadMode` dynamically.
