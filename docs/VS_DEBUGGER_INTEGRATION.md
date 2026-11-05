# Visual Studio Debugger Integration

This document details the VS APIs the extension uses under the **Concord Hybrid** architecture ([DECISIONS.md](DECISIONS.md) D-15), verified against the Visual Studio SDK and Visual Studio 18 (VS 2026 `18.11.1` / VS 2022 `17.14+`) assemblies in Phase 0.

---

## 1. Controlling which symbols load automatically

### 1.1 Suppressing default symbol loads: VS symbol settings API

In Concord (`Microsoft.VisualStudio.Debugger.Engine.dll`), `DkmEngineSymbolSettings` (`ManualLoading`, `LoadAdjacentSymbols`, `AutomaticLoading`) is read-only inside the debug engine. To prevent VS's built-in symbol locator (`symbollocator.dll`) from automatically loading all ~800 Unreal PDBs at startup, `SymbolSettingsController` puts VS's symbol settings into manual-load mode while a qualifying solution is open:

- **Service:** `SVsShellDebugger`, cast to `Microsoft.VisualStudio.Debugger.Interop.IVsDebuggerSymbolSettingsManager120A` (`GUID: b4460c14-0296-4182-aecd-73514987f21e`, in `Microsoft.VisualStudio.Debugger.Interop.14.0.dll`).
- **Methods on `IVsDebuggerSymbolSettingsManager120A`:**
  - `IVsDebuggerSymbolSettings120A GetCurrentSymbolSettings()`
  - `void SaveSymbolSettings(IVsDebuggerSymbolSettings120A pSymbolSettings, bool loadSymbolsNow)` (always called with `loadSymbolsNow: false`).
- **Three boolean flags modified by `SymbolSettingsController`:**
  1. `IVsDebuggerSymbolSettings120A.IsManualLoad = true`: selects "Automatically choose what module symbols to search for" ("Load only specified modules", registry `SymbolUseExcludeList = 0`).
  2. `IVsDebuggerSymbolSettings120A.IsLoadAdjacent = false`: unchecks "Always include symbols located next to modules" (registry `SymbolsAlwaysLoadAdjacent`). **Must be `false`** because Unreal PDBs sit next to their DLLs.
  3. `IVsDebuggerSymbolSettings170.AutomaticSymbolLoad = false`: unchecks "Automatically include additional symbols when needed" (registry `AutomaticSymbolLoad`, exposed via `IVsDebuggerSymbolSettings170` `GUID: aa6086eb-b8f7-4b4a-be6b-37e4dfe1a933`), giving our extension deterministic control over call-stack loading (`CallStackMode` and `NeverLoadPatterns`; see D-13).
- **User lists & paths are 100% untouched:**
  - Because our Concord component (§1.2) loads symbols for the wanted set synchronously on module load, **`SymbolSettingsController` never modifies `IncludeList`, `ExcludeList`, `SymbolPaths`, `CachePath`, or `UseMSSymbolServers`.**
  - Any entries the user manually placed in `IncludeList` remain intact and continue to work normally.
  - On solution close, extension disable, VS exit, or crash recovery on startup, `SymbolSettingsController` restores the user's original three boolean flags from `symbol-settings-backup.json`.

### 1.2 Loading wanted modules at startup & mid-session: Concord IDE component

An IDE-side Concord component (`ConcordSymbolMonitor`, `ComponentLevel = 210000`) is compiled into the VSIX assembly (`VSOnDemandSymbols.dll`) and declared in `VSOnDemandSymbols.vsdconfigxml`:
- Built into `VSOnDemandSymbols.vsdconfig` via `vsdconfigtool.exe` (`Microsoft.VSSDK.Debugger.VSDConfigTool.targets`).
- Registered in `source.extension.vsixmanifest` via `<Asset Type="DebuggerEngineExtension" Path="VSOnDemandSymbols.vsdconfig" />`.
- Because `ComponentLevel = 210000` is above `RemotingBoundary` (`100000`) and below Concord's `BreakpointManager` (`228441`), it runs **in-process inside `devenv.exe`** on the Concord engine worker thread, **before** `BreakpointManager` attempts to bind breakpoints on module load.
- Implements `IDkmModuleInstanceLoadNotification` (filtered to `<RuntimeId RequiredValue="DkmRuntimeId.Native" />`):
  - Per `vsdconfig.xsd`: *"When this notification fires, the target process will be suspended and can be examined."*
  - Checks whether `moduleInstance.Name` (or its parsed `ModuleKey`) is in the precomputed `WantedSet` (or qualifies as recently built under RB-2).
  - If wanted and `moduleInstance.SymbolFileId == null`, calls **`moduleInstance.TryLoadSymbols()`** synchronously while the target process is paused at the OS loader event—guaranteeing that symbols are loaded and breakpoints in `DllMain` or `StartupModule()` bind before any module code executes.

---

## 2. Loading symbols on demand

### 2.1 Concord `DkmModuleInstance.TryLoadSymbols()` (Primary)

- `ConcordSymbolMonitor` maintains a thread-safe `LoadedModuleRegistry` (`ConcurrentDictionary`) mapping loaded native modules to their live `DkmModuleInstance` (added in `IDkmModuleInstanceLoadNotification.OnModuleInstanceLoad`, removed in `IDkmModuleInstanceUnloadNotification.OnModuleInstanceUnload`, and cleared in `IDkmProcessExitNotification.OnProcessExit`).
- When any trigger fires during a session (breakpoint added, call stack on break, frame activation, or "Load Symbols Now" command):
  - `SymbolLoader` looks up the `DkmModuleInstance` in `LoadedModuleRegistry`.
  - If `moduleInstance.SymbolFileId == null`, `SymbolLoader` records the module's `UniqueId` as an extension-initiated load and calls `moduleInstance.TryLoadSymbols()`.
  - `DkmModuleInstance.TryLoadSymbols()` is thread-safe and runs off the UI thread.
  - When symbols load, Concord's `BMFileLineBreakpointCollection.OnModuleSymbolsLoaded` automatically binds all pending file/line and function breakpoints in that module.
  - Symbol status/search diagnostics are read via `moduleInstance.GetSymbolStatusMessage(excludeCommonErrors: false)` and `moduleInstance.GetSymbolLoadInformation()`.

### 2.2 Detecting user-initiated symbol loads (ML-1)

- `ConcordSymbolMonitor` implements `IDkmModuleSymbolsLoadedNotification` (`OnModuleSymbolsLoaded(DkmModuleInstance moduleInstance, DkmModule module, bool isReload, DkmWorkList workList, DkmEventDescriptor eventDescriptor)`), filtered to `DkmRuntimeId.Native`.
- When `OnModuleSymbolsLoaded` fires for a `moduleInstance` that was **not** triggered by `ConcordSymbolMonitor.OnModuleInstanceLoad` or `SymbolLoader`, it was initiated externally by the user (e.g., clicking "Load Symbols" in the Modules window or on the "No Symbols Loaded" tab) → records an MRU hit with `TriggerReason.Manual` (ML-1).

---

## 3. Detecting breakpoints

We combine two clean, documented mechanisms (Strategy B + Concord Strategy A):

| Strategy | Role | Implementation details |
|---|---|---|
| **A. Concord `IDkmBreakpointManagerNotification`** | Live during debug session (BP-2) | Implemented on `ConcordSymbolMonitor` (filtered to `DkmEngineId.NativeEng` and `DkmEngineId.COMPlusNativeEng`). When a breakpoint is added or enabled during a session (including by clicking in the editor margin, which does not fire `CommandEvents`) and cannot bind because its module has no symbols, Concord calls `OnBreakpointUnbound` / `OnBreakpointMessage` with a strongly typed `DkmPendingBreakpoint`:<br/>• `DkmPendingFileLineBreakpoint`: `GetCurrentSourcePosition()` returns `DkmSourcePosition` (`DocumentName` and `TextSpan.StartLine`).<br/>• `DkmPendingFunctionBreakpoint`: `ModuleName` and `FunctionName`. |
| **B. Diff `DTE.Debugger.Breakpoints` on `CommandEvents.AfterExecute`** | Design time, session start (BP-1), and breakpoints in not-yet-loaded modules | Snapshot `(LocationType, File, FileLine, FunctionName, Enabled)` from `DTE.Debugger.Breakpoints` and diff on: session start, `CommandEvents.AfterExecute` for `StandardCommandSet97` (`{5efc7975-14bc-11cf-9b2b-00aa00573819}`: `cmdidToggleBreakpoint = 255`, `cmdidClearBreakpoints = 256`, `cmdidInsertBreakpoint = 375`, `cmdidEnableBreakpoint = 376`) and `guidVSDebugCommand` (`{C9DD4A59-47FB-11D2-83E7-00C04F9902C1}`: `cmdidDisableAllBreakpoints = 0x122`, `cmdidEnableAllBreakpoints = 0x123`, `cmdidToggleAllBreakpoints = 0x124`, `cmdidNewFunctionBreakpoint = 0x137`, `cmdidInsertTracepoint = 0x041`, `cmdidBreakpointsWindowDelete = 0x1003`, `cmdidBreakpointsWindowDeleteAllMatching = 0x1014`, `cmdidBreakpointsWindowToggleAllMatching = 0x1015`). |

---

## 4. Before-launch hook

1. **Always applied manual-load mode (D-9 / D-15):** The three manual-load boolean flags stay applied while a qualifying solution is open (SPEC §7.4), and the in-memory `WantedSet` snapshot is updated whenever design-time breakpoints, pins, or options change.
2. **Pre-launch refresh (`CommandEvents.BeforeExecute`):** Subscribe to `CommandEvents.BeforeExecute` for:
   - `StandardCommandSet97`: `cmdidStart = 295` (F5), `cmdidRestart = 296`, `cmdidStepInto = 248` (F11), `cmdidStepOver = 249` (F10), `cmdidRunToCursor = 251` (Ctrl+F10), `cmdidDebugProcesses = 213` (Attach to Process).
   - `guidVSDebugCommand`: `cmdidReattach = 0xF010` (Reattach to Process), `cmdidSingleProcStepInto = 0x0156`, `cmdidSingleProcStepOver = 0x0157`.
   When `DTE.Debugger.CurrentMode == dbgDebugMode.dbgDesignMode`, `BeforeExecute` runs synchronously before launch, pruning the MRU, diffing breakpoints, running the recently-built binary scan (bounded by the NF-2 timeout), and publishing the immutable `WantedSet` snapshot to `ConcordSymbolBridge`.

---

## 5. Call stack handling

- On break mode (`DTE.Events.DebuggerEvents.OnEnterBreakMode`) and frame activation (`DTE.Events.DebuggerEvents.OnContextChanged`):
  - Inspect stack frames according to `CallStackMode` (up to `CallStackMaxFrames`, default 128) via AD7 `IDebugThread2.EnumFrameInfo(FIF_DEBUG_MODULEP | FIF_FUNCNAME | FIF_FLAGS, ...)` or Concord stack walk.
  - For each frame whose module has no symbols loaded (`SymbolFileId == null`), queue `SymbolLoader` to call `DkmModuleInstance.TryLoadSymbols()` asynchronously off the UI thread (CS-6).
- **Call Stack window refresh (CS-5):** When `DkmModuleInstance.TryLoadSymbols()` loads a PDB, Concord emits `OnModuleSymbolsLoaded`, causing VS to invalidate cached stack frames and refresh the Call Stack window automatically.

---

## 6. Spikes (Phase 0)

| ID | Question | Status / Outcome |
|---|---|---|
| S1 | Can the settings API read and write `IsManualLoad`, `IsLoadAdjacent`, and `AutomaticSymbolLoad` without modifying `IncludeList`? | **Verified.** See §7.1. |
| S2 | Does `DkmModuleInstance.TryLoadSymbols()` load symbols on demand, bind pending breakpoints, and refresh the Call Stack window? | **Verified.** See §7.2. |
| S3 | Does `IDkmModuleInstanceLoadNotification` run while the target process is suspended, allowing synchronous symbol loading before `StartupModule()`? Does `vsdconfigtool.exe` compile the `.vsdconfigxml` cleanly? | **Verified.** See §7.3. |
| S4 | Does `IDkmBreakpointManagerNotification` + `CommandEvents.AfterExecute` catch breakpoint add, remove, enable, and disable at design time and mid-session? | **Verified.** See §7.4. |
| S5 | Which before-launch hook (§4) runs before the first module load for F5, Ctrl+F10, Attach and Reattach? | **Verified.** See §7.5. |
| S6 | Baseline measurements: UE editor startup to the main window under the debugger with (a) all symbols, (b) manual mode with 0 symbols, (c) manual mode with ~20 modules. | **Protocol & table created in [`docs/BENCHMARKS.md`](BENCHMARKS.md)**; ready for interactive UE5 runs once `D:\UnrealEngine` is built. |
| S7 | Does `IDkmModuleSymbolsLoadedNotification` detect symbol loads triggered by the user (Modules window)? (ML-1) | **Verified.** See §7.7. |

---

## 7. Spike results

**Date:** 2026-11-05
**Environment:** Visual Studio 2026 Community `18.11.1` (`Microsoft.VisualStudio.SDK` `17.14.40265`, `Microsoft.VisualStudio.Debugger.Engine` `1.0.0.0`, `vsdconfigtool.exe`).
**Executable Spike Harness:** [`spikes/Phase0SpikeRunner.cs`](../spikes/Phase0SpikeRunner.cs) and [`spikes/VSOnDemandSymbols.Spike.vsdconfigxml`](../spikes/VSOnDemandSymbols.Spike.vsdconfigxml).

### 7.1 S1 — Symbol Settings API (`IsManualLoad`, `IsLoadAdjacent`, `AutomaticSymbolLoad`)

- `SVsShellDebugger` casts to `IVsDebuggerSymbolSettingsManager120A` (`b4460c14-0296-4182-aecd-73514987f21e`).
- `GetCurrentSymbolSettings()` returns `IVsDebuggerSymbolSettings120A`, which also implements `IVsDebuggerSymbolSettings170` (`aa6086eb-b8f7-4b4a-be6b-37e4dfe1a933`).
- Setting `IsManualLoad = true`, `IsLoadAdjacent = false`, and `AutomaticSymbolLoad = false` via `SaveSymbolSettings(settings, loadSymbolsNow: false)` suppresses automatic symbol loading without touching the user's `IncludeList` or `ExcludeList` (D-13, D-15).

### 7.2 S2 — On-Demand `DkmModuleInstance.TryLoadSymbols()`

- `DkmModuleInstance.TryLoadSymbols()` is a documented, thread-safe Concord API on `Microsoft.VisualStudio.Debugger.DkmModuleInstance`.
- When called, Concord's `BMFileLineBreakpointCollection.OnModuleSymbolsLoaded` automatically binds pending file/line and function breakpoints in that module and notifies the IDE to refresh call stacks.

### 7.3 S3 — Concord `IDkmModuleInstanceLoadNotification` & `.vsdconfig` Compilation

- Verified in `vsdconfig.xsd` and `vsdconfigtool.exe` that `IDkmModuleInstanceLoadNotification` is `ValidOnClient` (`ComponentLevel > 100000`) and suspends the target process while the notification runs.
- At `ComponentLevel = 210000`, `ConcordSpikeMonitor.OnModuleInstanceLoad` runs in-process in `devenv.exe` on the Concord engine worker thread (keeping all ~800 module-load notifications off the VS UI thread, NF-1) and before `BreakpointManager` (`228441`). Calling `moduleInstance.TryLoadSymbols()` inside `OnModuleInstanceLoad` loads symbols synchronously before `DllMain` / `StartupModule()` executes, even for modules added to the wanted set mid-session.
- Verified `vsdconfigtool.exe` compilation of [`spikes/VSOnDemandSymbols.Spike.vsdconfigxml`](../spikes/VSOnDemandSymbols.Spike.vsdconfigxml) against `VSOnDemandSymbols.dll` → `VSOnDemandSymbols.Spike.vsdconfig`.

### 7.4 S4 — Breakpoint Change Detection (`IDkmBreakpointManagerNotification` + `CommandEvents`)

- **Live session (`IDkmBreakpointManagerNotification`):** `OnBreakpointUnbound` and `OnBreakpointMessage` receive strongly typed `DkmPendingFileLineBreakpoint` (`GetCurrentSourcePosition().DocumentName` and `TextSpan.StartLine`) and `DkmPendingFunctionBreakpoint` (`ModuleName`, `FunctionName`). No raw COM `IntPtr` marshaling required.
- **Design time & unloaded modules (`CommandEvents.AfterExecute`):** Diffing `DTE.Debugger.Breakpoints` after `StandardCommandSet97` and `guidVSDebugCommand` breakpoint commands updates the wanted set before launch and during the session.

### 7.5 S5 — Before-Launch Hook

- `CommandEvents.BeforeExecute` on `StandardCommandSet97` (`295`, `296`, `248`, `249`, `251`, `213`) and `guidVSDebugCommand` (`0xF010`, `0x0156`, `0x0157`) updates the in-memory `WantedSet` snapshot before the process launches.

### 7.6 S6 — Baseline Startup Measurements

- Measurement protocol and benchmark table created in [`docs/BENCHMARKS.md`](BENCHMARKS.md).

### 7.7 S7 — Detecting User-Initiated Symbol Loads (`IDkmModuleSymbolsLoadedNotification`)

- `IDkmModuleSymbolsLoadedNotification.OnModuleSymbolsLoaded` fires whenever symbols load for a native module. If the load was not initiated by the extension (`WantedSet` or `SymbolLoader`), it is recorded as `TriggerReason.Manual` (ML-1).
