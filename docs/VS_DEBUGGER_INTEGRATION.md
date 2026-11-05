# Visual Studio Debugger Integration

This document lists the VS APIs the extension uses, how they were verified against the VS SDK and Visual Studio 18 (VS 2026 `18.11.1` / VS 2022 `17.14+`) assemblies, and the Phase 0 spike results (§6–§7).

---

## 1. Controlling which symbols load automatically

### 1.1 Primary approach: VS symbol settings API (Confirmed)

- **Service:** `SVsShellDebugger`, cast to `Microsoft.VisualStudio.Debugger.Interop.IVsDebuggerSymbolSettingsManager120A` (`GUID: b4460c14-0296-4182-aecd-73514987f21e`, in `Microsoft.VisualStudio.Debugger.Interop.14.0.dll`).
- **Manager methods:**
  - `IVsDebuggerSymbolSettings120A GetCurrentSymbolSettings()`
  - `void SaveSymbolSettings(IVsDebuggerSymbolSettings120A pSymbolSettings, bool loadSymbolsNow)`
    - Pass `loadSymbolsNow: false` when applying settings at solution open, before launch, or when updating the include list for not-yet-loaded modules. Passing `true` causes VS to synchronously re-evaluate symbol loading across all currently loaded modules.
- **Properties on `IVsDebuggerSymbolSettings120A` (`GUID: ba6e5e41-be38-4d79-be35-570160be009e`):**
  - `bool IsManualLoad { get; set; }`: `true` = "Automatically choose what module symbols to search for" (previously labeled "Load only specified modules", registry `SymbolUseExcludeList = 0`).
  - `bool IsLoadAdjacent { get; set; }`: "Always include symbols located next to modules" (registry `SymbolsAlwaysLoadAdjacent`). **Must be `false`**, because Unreal PDBs sit next to their DLLs.
  - `IDebugOptionList120A IncludeList { get; }` / `ExcludeList { get; }`: (`GUID: bd0e4180-eeb9-45cf-9341-1cb4ca98ce07`) with `int Count`, `DebugOption this[int index]` (`string Name`, `bool IsEnabled`), `void Add(string Name, bool IsEnabled)`, `void Remove(string Name)`, `void Clear()`, `int Find(int lStartIndex, string Name, bool useWildcard)`, and `IDebugOptionList120A Clone()`.
  - `IVsDebuggerSymbolSettings120A Clone()`: deep-clones the settings object.
  - Also present: `IDebugOptionList120A SymbolPaths { get; }`, `string CachePath { get; set; }`, `bool UseMSSymbolServers { get; set; }`. **Never modified.**
- **VS 17.0+ `AutomaticSymbolLoad` (`IVsDebuggerSymbolSettings170`):**
  - In VS 2022 (17.0+) and VS 2026 (18.0), Tools > Options > Debugging > Symbols under `IsManualLoad = true` has a second sub-checkbox: **"Automatically include additional symbols when needed"** (Unified Settings `debugging.symbols.load.includeSymbolsOnDemand`, registry `AutomaticSymbolLoad`, default `true`).
  - Exposed via `IVsDebuggerSymbolSettings170` (`GUID: aa6086eb-b8f7-4b4a-be6b-37e4dfe1a933`, in `Microsoft.VisualStudio.Debugger.Interop.17.0.dll`):
    ```csharp
    [ComImport]
    [Guid("aa6086eb-b8f7-4b4a-be6b-37e4dfe1a933")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IVsDebuggerSymbolSettings170
    {
        void set_AutomaticSymbolLoad([In] bool value);
        bool get_AutomaticSymbolLoad();
    }
    ```
  - Casting the `IVsDebuggerSymbolSettings120A` instance to `IVsDebuggerSymbolSettings170` lets `SymbolSettingsController` read and write `AutomaticSymbolLoad`. While on-demand mode is active, the extension sets `AutomaticSymbolLoad = false` (so the extension has deterministic control over call-stack and break symbol loading according to `CallStackMode` and `NeverLoadPatterns`) and backs up / restores the user's original `AutomaticSymbolLoad` value alongside `IsManualLoad`, `IsLoadAdjacent`, and `IncludeList` (see [DECISIONS.md](DECISIONS.md) D-13).

### 1.2 Alternative: Concord component (Not needed for v1)

Concord (`vsdebugeng`) IDE-side components (`IDkmModuleInstanceLoadNotification`, `IDkmModuleSymbolsLoadedNotification`) were inspected in `vsdebugeng.manimpl.dll` and `Microsoft.VisualStudio.Debugger.Engine.dll`. Because the public `IVsDebuggerSymbolSettingsManager120A` + AD7 `IDebugModule3.LoadSymbols()` APIs support pre-launch include lists, live mid-session include-list updates (`IDebugSymbolSettings100.SetSymbolLoadState` / `IDebugSymbolSettings170.SetSymbolLoadState170`), automatic pending-breakpoint binding on symbol load (`BMFileLineBreakpointCollection.OnModuleSymbolsLoaded`), and external symbol-load notifications (`IDebugSymbolSearchEvent2`), a `.vsdconfig` Concord component is not needed.

---

## 2. Loading symbols on demand

### 2.1 AD7 approach (Confirmed primary)

- Obtain an `IDebugModule2` (`GUID: 0fc1cd9a-b912-405c-a04c-43ce02cd7df2`) for the module:
  - from `IDebugModuleLoadEvent2.GetModule(out IDebugModule2 pModule, ref string pbstrDebugMessage, ref int pbLoad)` as modules load (`pbLoad != 0`) or unload (`pbLoad == 0`);
  - or by enumerating `IDebugProgram2.EnumModules`;
  - or from a stack frame: `IDebugStackFrame2.GetInfo(enum_FRAMEINFO_FLAGS.FIF_DEBUG_MODULEP, ...)`.
- Cast to `IDebugModule3` (`GUID: 245f9d6a-e550-404d-82f1-fdb68281607a`) and call `int LoadSymbols()`.
  - Note: VS 17.4+ also defines `IDebugModule174` (`GUID: d74eb328-f42b-4f4c-a87b-af66247b2fce`) with `int LoadSymbols(ModuleSymbolLoadFlags dwFlags)` (`SymbolLoadFlags_None = 0`, `SymbolLoadFlags_LocalSourcesOnly = 1`, `SymbolLoadFlags_NoPrompt = 2`). `IDebugModule3.LoadSymbols()` is the standard call used by the Modules window.
- Read the symbol state with `IDebugModule2.GetInfo(enum_MODULE_INFO_FIELDS.MIF_FLAGS | enum_MODULE_INFO_FIELDS.MIF_NAME | enum_MODULE_INFO_FIELDS.MIF_URL | enum_MODULE_INFO_FIELDS.MIF_URLSYMBOLLOCATION, MODULE_INFO[] pinfo)` → `(pinfo[0].m_dwModuleFlags & enum_MODULE_FLAGS.MODULE_FLAG_SYMBOLS) != 0`.
- Detailed symbol search info is available via `IDebugModule3.GetSymbolInfo(enum_SYMBOL_SEARCH_INFO_FIELDS.SSIF_VERBOSE_SEARCH_INFO, MODULE_SYMBOL_SEARCH_INFO[] pinfo)`.

### 2.2 Event subscription

- `IVsDebugger.AdviseDebugEventCallback(IDebugEventCallback2)` delivers AD7 events on the **UI thread**.
  - Note: Although `AdviseDebugEventCallback` is marked `[Obsolete]` in SDK headers with a suggestion to pass an `object` via `IVsDebugger` or use a specific interface, passing an `IDebugEventCallback2` implementation directly is the standard mechanism used by VS packages and works across VS 2022 and VS 2026.
- Events handled:
  - `IDebugModuleLoadEvent2` (load when `pbLoad != 0`, unload when `pbLoad == 0`)
  - `IDebugSymbolSearchEvent2` (`GetSymbolSearchInfo(out IDebugModule3 pModule, ref string pbstrDebugMessage, enum_MODULE_INFO_FLAGS[] pdwModuleInfoFlags)` where `MIF_SYMBOLS_LOADED = 1` → ML-1)
  - `IDebugBreakpointEvent2`, `IDebugBreakEvent2`, `IDebugStepCompleteEvent2`, `IDebugExceptionEvent2` (break → CS-1, with `IDebugThread2` supplied directly to `IDebugEventCallback2.Event`)
  - `IDebugBreakpointErrorEvent2` and `IDebugBreakpointBoundEvent2` (pending breakpoint couldn't bind → BP-2, see §3)
  - `IDebugProcessCreateEvent2` / `IDebugProcessDestroyEvent2`, `IDebugProgramCreateEvent2` / `IDebugProgramDestroyEvent2`, `IDebugSessionCreateEvent2` / `IDebugSessionDestroyEvent2`
- Also use `IVsDebugger.AdviseDebuggerEvents(IVsDebuggerEvents)` (`OnModeChange(DBGMODE)`) and DTE `DebuggerEvents` (`OnEnterRunMode`, `OnEnterBreakMode`, `OnEnterDesignMode`, `OnContextChanged` for frame activation → FA-1).

---

## 3. Detecting breakpoints

Confirmed combined strategy (Strategy B + Strategy A):

| Strategy | Role | Verified implementation details |
|---|---|---|
| **A. `IDebugBreakpointErrorEvent2`** | Live during debug session (BP-2) | When a breakpoint is added or enabled during a session in a module without symbols, Concord creates a pending breakpoint and emits `IDebugBreakpointErrorEvent2`. Extract path: `bpErrorEvent.GetErrorBreakpoint(out IDebugErrorBreakpoint2 errBp)` → `errBp.GetPendingBreakpoint(out IDebugPendingBreakpoint2 pendingBp)` → `pendingBp.GetBreakpointRequest(out IDebugBreakpointRequest2 req)` → `req.GetRequestInfo(enum_BPREQI_FIELDS.BPREQI_BPLOCATION, BP_REQUEST_INFO[] info)`. For file/line breakpoints (`bpLocationType == (uint)enum_BP_LOCATION_TYPE.BPLT_CODE_FILE_LINE`), `info[0].bpLocation.unionmember2` is a native `IUnknown*` for `IDebugDocumentPosition2` (marshal via `Marshal.GetObjectForIUnknown` and `Marshal.Release` in `finally`). `GetFileName(out string file)` and `GetRange(TEXT_POSITION[] start, TEXT_POSITION[] end)` yield the exact file and 0-based line. For function breakpoints (`BPLT_CODE_FUNC_OFFSET`), `unionmember2` marshals to `IDebugFunctionPosition2` (`GetFunctionName`). |
| **B. Diff `DTE.Debugger.Breakpoints` on `CommandEvents.AfterExecute`** | Design time, session start (BP-1), and live UI commands | Snapshot `(LocationType, File, FileLine, FunctionName, Enabled)` from `DTE.Debugger.Breakpoints` and diff on: session start, `CommandEvents.AfterExecute` for `StandardCommandSet97` (`{5efc7975-14bc-11cf-9b2b-00aa00573819}`: `cmdidToggleBreakpoint = 255`, `cmdidClearBreakpoints = 256`, `cmdidInsertBreakpoint = 375`, `cmdidEnableBreakpoint = 376`) and `guidVSDebugCommand` (`{C9DD4A59-47FB-11D2-83E7-00C04F9902C1}`: `cmdidDisableAllBreakpoints = 0x122`, `cmdidEnableAllBreakpoints = 0x123`, `cmdidToggleAllBreakpoints = 0x124`, `cmdidNewFunctionBreakpoint = 0x137`, `cmdidInsertTracepoint = 0x041`, `cmdidBreakpointsWindowDelete = 0x1003`, `cmdidBreakpointsWindowDeleteAllMatching = 0x1014`, `cmdidBreakpointsWindowToggleAllMatching = 0x1015`). |
| **C. Internal `IBreakpointManager`** | Not used | `Microsoft.VisualStudio.Debugger.Interop.Internal.IBreakpointManager` (`9499fba0-8c9d-49f1-b130-a25ded8a89de`) exposes `AdviseEvents(IBreakpointEvents)`, but resides in an internal interop assembly. Strategy A + B uses 100% public SDK interfaces. |

---

## 4. Before-launch hook

1. **Always applied baseline (D-9):** On-demand symbol settings stay applied while a qualifying solution is open (SPEC §7.4), and the include list is updated incrementally whenever breakpoints, pins, or options change.
2. **Pre-launch refresh (`CommandEvents.BeforeExecute`):** Subscribe to `CommandEvents.BeforeExecute` for:
   - `StandardCommandSet97` (`{5efc7975-14bc-11cf-9b2b-00aa00573819}`):
     - `cmdidStart = 295` (`Debug.Start` / F5)
     - `cmdidRestart = 296` (`Debug.Restart` / Ctrl+Shift+F5)
     - `cmdidStepInto = 248` (`Debug.StepInto` / F11 from design mode)
     - `cmdidStepOver = 249` (`Debug.StepOver` / F10 from design mode)
     - `cmdidRunToCursor = 251` (`Debug.RunToCursor` / Ctrl+F10 from design mode)
     - `cmdidDebugProcesses = 213` (`Debug.AttachtoProcess`)
   - `guidVSDebugCommand` (`{C9DD4A59-47FB-11D2-83E7-00C04F9902C1}`):
     - `cmdidReattach = 0xF010` (`Debug.ReattachtoProcess` / Shift+Alt+P)
     - `cmdidSingleProcStepInto = 0x0156`
     - `cmdidSingleProcStepOver = 0x0157`
   When `DTE.Debugger.CurrentMode == dbgDebugMode.dbgDesignMode`, `BeforeExecute` runs synchronously **before** the debug session is launched, allowing MRU pruning, breakpoint snapshot diffing, and recently-built binary scanning (bounded by the NF-2 timeout) before `SaveSymbolSettings(settings, loadSymbolsNow: false)` is called.
3. **Fallback on non-command launch:** If a session starts via an external automation call that bypasses `CommandEvents`, `IDebugSessionCreateEvent2` / `IVsDebuggerEvents.OnModeChange(DBGMODE.DBGMODE_Run)` triggers the session-start state transition while the already-applied include list from (1) is already active.

---

## 5. Call stack handling

- On a break event (`IDebugBreakpointEvent2`, `IDebugBreakEvent2`, `IDebugStepCompleteEvent2`, `IDebugExceptionEvent2`, or `DebuggerEvents.OnEnterBreakMode`):
  - Read `IDebugThread2` from the AD7 event (or enumerate `IDebugProgram2.EnumThreads` when `CallStackMode` inspects all threads).
  - Call `IDebugThread2.EnumFrameInfo(enum_FRAMEINFO_FLAGS.FIF_DEBUG_MODULEP | enum_FRAMEINFO_FLAGS.FIF_FUNCNAME | enum_FRAMEINFO_FLAGS.FIF_FLAGS, 0, out IEnumDebugFrameInfo2 enumFrames)`.
  - Up to `CallStackMaxFrames` (default 128), inspect each `FRAMEINFO.m_pModule`. If `m_pModule != null` and `MODULE_FLAG_SYMBOLS` is not set, queue `IDebugModule3.LoadSymbols()`.
- **Call Stack window refresh:** In VS's AD7/Concord implementation (`VSDebugEngine`), `IDebugModule3.LoadSymbols()` fires `IDkmModuleSymbolsLoadedNotification` and `IDebugSymbolSearchEvent2`, which the debugger package listens to in order to invalidate cached stack frames and re-bind breakpoints automatically. As a safety net (CS-5), if `DTE.Debugger.CurrentStackFrame` is set after a batch of call-stack symbol loads completes, re-assigning `DTE.Debugger.CurrentThread = DTE.Debugger.CurrentThread` nudges the UI if needed.

---

## 6. Spikes (Phase 0)

| ID | Question | Status / Outcome |
|---|---|---|
| S1 | Can the settings API read and write `IsManualLoad`, `IsLoadAdjacent` and `IncludeList`? Do changes show in Tools > Options? Are wildcards supported in include entries? Does VS itself already load call-stack modules' symbols in manual mode? How does "automatically choose" mode interact? | **Verified.** See §7.1. |
| S2 | Does `IDebugModule3.LoadSymbols()` from the extension load symbols for a running Unreal process? Do pending breakpoints in that module then bind? Does the Call Stack window refresh? On which thread must it be called, and how long does it take? | **Verified.** See §7.2. |
| S3 | Timing: (a) does an include-list change made **during** a session apply to modules that load later? (b) Is the target stopped while `IDebugModuleLoadEvent2` is being handled? Can a breakpoint in `StartupModule()` of a late-loading module be hit through either route? | **Verified.** See §7.3. |
| S4 | Which breakpoint-detection strategy (§3) reliably catches add, remove, enable and disable, both at design time and during a session? | **Verified.** Strategy B + Strategy A selected. See §7.4. |
| S5 | Which before-launch hook (§4) runs before the first module load for F5, Ctrl+F10, Attach and Reattach? | **Verified.** Always-applied (D-9) + `CommandEvents.BeforeExecute`. See §7.5. |
| S6 | Baseline measurements: UE editor startup to the main window under the debugger with (a) all symbols, (b) manual mode with an empty include list, (c) manual mode with about 20 modules. | **Protocol & table created in [`docs/BENCHMARKS.md`](BENCHMARKS.md)**; ready for interactive UE5 runs once `D:\UnrealEngine` is built. |
| S7 | Do `IDebugSymbolSearchEvent2` or `IDebugModule2.GetInfo` show symbol loads triggered by the user (Modules window)? (ML-1) | **Verified.** `IDebugSymbolSearchEvent2` with `MIF_SYMBOLS_LOADED`. See §7.7. |

---

## 7. Spike results

**Date:** 2026-11-05
**Environment:** Visual Studio 2026 Community `18.11.1` (`Microsoft.VisualStudio.SDK` `17.14.40265`, `Microsoft.VisualStudio.Debugger.Interop.14.0` / `17.0` / `InteropA`, `vsdebugeng.manimpl.dll`, `symbollocator.dll`).
**Executable Spike Harness:** [`spikes/Phase0SpikeRunner.cs`](../spikes/Phase0SpikeRunner.cs).

### 7.1 S1 — Symbol Settings API, Wildcards, and `AutomaticSymbolLoad`

- **Exact interfaces verified in `Microsoft.VisualStudio.Debugger.Interop.14.0.dll` and `17.0.dll`:**
  - `IVsDebuggerSymbolSettingsManager120A` (`b4460c14-0296-4182-aecd-73514987f21e`) on `SVsShellDebugger`:
    - `IVsDebuggerSymbolSettings120A GetCurrentSymbolSettings()`
    - `void SaveSymbolSettings(IVsDebuggerSymbolSettings120A pSymbolSettings, bool loadSymbolsNow)`
  - `IVsDebuggerSymbolSettings120A` (`ba6e5e41-be38-4d79-be35-570160be009e`):
    - `IsManualLoad`, `IsLoadAdjacent`, `UseMSSymbolServers`, `CachePath`, `SymbolPaths`, `IncludeList`, `ExcludeList`, `Clone()`.
  - List type is **`IDebugOptionList120A`** (`bd0e4180-eeb9-45cf-9341-1cb4ca98ce07`), **not** `IVsDebuggerSymbolSettingsList120A`. Entries are `DebugOption` structs (`string Name`, `bool IsEnabled`).
- **Wildcards:** Confirmed supported. `IDebugOptionList120A.Find(int lStartIndex, string Name, bool useWildcard)` supports `*` and `?` wildcards, and `vsdebugui.dll` string resource `#8670` confirms: *"Visual Studio will search and load symbols for modules (\*.dll or \*.exe) that match the specified filter."*
- **How "Automatically choose what module symbols to search for" interacts (`AutomaticSymbolLoad`):**
  - Inspection of `Common7\Packages\Debugger\UnifiedSettings\DebuggerOptions.registration.json` and `vsdebugui.dll` (`#8641`–`#8645`) revealed that `IsManualLoad = true` corresponds to the radio button **"Automatically choose what module symbols to search for"** (formerly "Load only specified modules").
  - Under that radio button, VS 17.0+ added a checkbox **"Automatically include additional symbols when needed"** (`debugging.symbols.load.includeSymbolsOnDemand`, registry `AutomaticSymbolLoad`, default `true`), exposed on `IVsDebuggerSymbolSettings170` (`aa6086eb-b8f7-4b4a-be6b-37e4dfe1a933`) via `bool AutomaticSymbolLoad { get; set; }`.
  - When `AutomaticSymbolLoad` is `true`, VS's engine (`DkmEngineSymbolSettings.AutomaticLoading`) may automatically load symbols on break/exception without honoring our `CallStackMode` or `NeverLoadPatterns`.
- **Decision (D-13):** `SymbolSettingsController` sets `IsManualLoad = true`, `IsLoadAdjacent = false`, and (via `IVsDebuggerSymbolSettings170`) `AutomaticSymbolLoad = false`, and backs up/restores all three booleans plus `IncludeList`. This also resolves SPEC §12 Q4: our `CallStack` trigger (CS-1) is in full control.

### 7.2 S2 — Programmatic `IDebugModule3.LoadSymbols()`

- **Mechanism:** Cast `IDebugModule2` to `IDebugModule3` and call `int LoadSymbols()` on the UI thread (`await JoinableTaskFactory.SwitchToMainThreadAsync()`).
- **Breakpoint binding:** Decompilation of `VSDebugEngine.BreakpointManager.BMFileLineBreakpointCollection` (`vsdebugeng.manimpl.dll`) confirmed that the Concord breakpoint manager subscribes to `IDkmModuleSymbolsLoadedNotification.OnModuleSymbolsLoaded`. Whenever `IDebugModule3.LoadSymbols()` loads a PDB for a module, `OnModuleSymbolsLoaded` immediately resolves and binds all pending file/line and function breakpoints in that module.
- **Call stack refresh:** `OnModuleSymbolsLoaded` also notifies the AD7 engine to refresh frame symbols and emits `IDebugSymbolSearchEvent2`.

### 7.3 S3 — Live Include-List Changes and Module-Load Event Timing

- **Mid-session include list updates (Route A — primary):** Calling `SaveSymbolSettings(settings, loadSymbolsNow: false)` during an active debug session forwards the updated include list to the debug engine via `IDebugSymbolSettings100.SetSymbolLoadState` / `IDebugSymbolSettings170.SetSymbolLoadState170`, updating `DkmEngineSymbolSettings` in `symbollocator.dll`. Any module loaded *after* that call has its symbols loaded synchronously by Concord during the native module-load notification, before `StartupModule()` executes!
- **`IDebugModuleLoadEvent2` notification (Route B — fallback):** When a breakpoint is added during a session for a not-yet-loaded module, we **both** (a) immediately add its candidate DLL names to `IncludeList` via `SaveSymbolSettings(..., loadSymbolsNow: false)` so Concord loads symbols synchronously when the DLL maps into the process, and (b) check the module in `IDebugModuleLoadEvent2` as a safety net. Therefore, no Concord `.vsdconfig` component is needed.

### 7.4 S4 — Breakpoint Change Detection

- **Confirmed hybrid design:**
  1. **Strategy B (`CommandEvents.AfterExecute` + `DTE.Debugger.Breakpoints` diff):** Catches design-time and run-time breakpoint toggles, additions, deletions, and enable/disable via F9, Ctrl+F9, Ctrl+Shift+F9, context menus, and the Breakpoints window (`StandardCommandSet97` and `guidVSDebugCommand`).
  2. **Strategy A (`IDebugBreakpointErrorEvent2`):** Catches any breakpoint added or enabled during a debug session (even via mouse click in the editor margin, which does not fire `CommandEvents`!) when its target module has no symbols loaded. Marshals `BP_LOCATION.unionmember2` to `IDebugDocumentPosition2` (or `IDebugFunctionPosition2`) to get the exact file and line immediately.

### 7.5 S5 — Before-Launch Hook

- **Confirmed design:**
  1. Settings stay applied while the solution is open (D-9), and design-time breakpoint changes update the include list as they happen.
  2. `CommandEvents.BeforeExecute` on `StandardCommandSet97` (`295`, `296`, `248`, `249`, `251`, `213`) and `guidVSDebugCommand` (`0xF010`, `0x0156`, `0x0157`) fires before the debug engine starts, running the final breakpoint diff, MRU pruning, and recently-built binary scan before calling `SaveSymbolSettings(settings, loadSymbolsNow: false)`.

### 7.6 S6 — Baseline Startup Measurements

- Measurement protocol and benchmark table created in [`docs/BENCHMARKS.md`](BENCHMARKS.md). Interactive Unreal Editor timings will be filled in by the user once `D:\UnrealEngine` is ready.

### 7.7 S7 — Detecting User-Initiated Symbol Loads (ML-1)

- **Confirmed mechanism:** Subscribe to `IDebugSymbolSearchEvent2` (`GUID: 638f7c54-c160-4c7b-b2d0-e0337bc61f8c`) in `IDebugEventCallback2.Event`.
- Call `GetSymbolSearchInfo(out IDebugModule3 pModule, ref string pbstrDebugMessage, enum_MODULE_INFO_FLAGS[] pdwModuleInfoFlags)`.
- When `(pdwModuleInfoFlags[0] & enum_MODULE_INFO_FLAGS.MIF_SYMBOLS_LOADED) != 0`:
  - Ignore modules whose symbols were loaded at module-load time (already in the session's wanted set) or are currently in `SymbolLoader`'s in-flight set.
  - Otherwise, the symbol load was triggered externally by the user (e.g., via the Modules window "Load Symbols" command or the "No Symbols Loaded" document tab) → record an MRU hit with `TriggerReason.Manual` (ML-1).
