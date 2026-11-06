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
  - [x] 3.2 Implement `DebuggerSessionMonitor` (hooks F5 launch via `CommandEvents.BeforeExecute`, updates `IncludeList` from `DTE.Debugger.Breakpoints`, hooks `DebuggerEvents.OnEnterBreakMode` to load missing call-stack symbols via `Module.LoadSymbols`).
  - [x] 3.3 Connect `OutputLog` for clean diagnostic logging to the "On-Demand Symbols" pane.
- [x] **Phase 4: Package Lifecycle & Commands**
  - [x] 4.1 Update `VSOnDemandSymbolsPackage`: background load, C++ solution UI context rule, solution open/close listeners, crash recovery check on startup.
  - [x] 4.2 Streamline UI: transparent background execution; remove redundant menu toggle (D-23).
  - [x] 4.3 Verify full solution build (`/p:UseSharedCompilation=false /nr:false`).
- [x] **Phase 5: Verification & Documentation**
  - [x] 5.1 Run Core test suite.
  - [x] 5.2 Validate against `NativeSample` and write verification instructions for Unreal Editor.
  - [x] 5.3 Commit clean implementation to git with descriptive commit message.

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
