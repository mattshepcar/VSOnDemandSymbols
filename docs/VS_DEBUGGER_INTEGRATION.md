# Visual Studio Debugger Integration

This document lists the VS APIs the extension expects to use, the open technical risks, and the **spikes** (small throwaway experiments) that must be run in Phase 0 before the design is final.

> [!WARNING]
> Several API details below come from memory and web summaries. They have **not been verified** against the VS SDK assemblies yet. Treat every item marked *(verify)* as a hypothesis. Spike results go into §7 of this file, and if a spike invalidates part of the spec, update SPEC.md and DECISIONS.md.

---

## 1. Controlling which symbols load automatically

### 1.1 Primary approach: VS symbol settings API

- Service: `SVsShellDebugger`, cast to `Microsoft.VisualStudio.Debugger.Interop.IVsDebuggerSymbolSettingsManager120A` *(verify)*.
- `GetCurrentSymbolSettings()` → `IVsDebuggerSymbolSettings120A`, and `SaveSymbolSettings(settings)` *(verify the method names)*.
- Expected properties on the settings object *(verify)*:
  - `IsManualLoad`: true = "Load only specified modules".
  - `IsLoadAdjacent`: "Always load symbols located next to modules". **Must be false**, because Unreal PDBs sit next to their DLLs.
  - `IncludeList` / `ExcludeList`: `IVsDebuggerSymbolSettingsList120A` with add/remove/count/item.
  - Also present: symbol paths, cache path, MS symbol server flag. **Never modified.**
- Newer VS versions also have an "Automatically choose what module symbols to search for" mode. Find out whether it is exposed through this API and how it interacts with `IsManualLoad` (S1).

### 1.2 Alternative: Concord component

If the settings API is missing, unusable, or doesn't apply changes live, a Concord (`vsdebugeng`) IDE-side component can be used. It would be declared in a `.vsdconfigxml`, compiled to `.vsdconfig`, and included in the VSIX. Candidate interfaces *(verify)*:
- `IDkmModuleInstanceLoadNotification`: runs on module load, possibly while the target is stopped, which would allow synchronous symbol loading.
- `DkmModuleInstance.TryLoadSymbols(...)`: overloads differ between VS versions.
- `IDkmModuleSymbolsLoadedNotification`: detect symbol loads (useful for ML-1).

Concord adds packaging complexity. Only use it if S1–S3 show the AD7/settings approach is insufficient.

---

## 2. Loading symbols on demand

### 2.1 AD7 approach (expected primary)

- Obtain an `IDebugModule2` for the module:
  - from `IDebugModuleLoadEvent2.GetModule(out module, out message, ref fLoad)` as modules load (keep a registry keyed by process and module path, and remove entries on unload);
  - or by enumerating `IDebugProgram2.EnumModules`;
  - or from a stack frame: `IDebugStackFrame2.GetInfo(FIF_DEBUG_MODULEP)`.
- Cast to `IDebugModule3` and call `LoadSymbols()`. This is what the Modules window's "Load Symbols" uses *(verify)*.
- Read the symbol state with `IDebugModule2.GetInfo(MIF_FLAGS | MIF_URL | MIF_URLSYMBOLLOCATION)` → `MODULE_FLAGS.MODULE_FLAG_SYMBOLS`.
- Symbol-load results: `IDebugSymbolSearchEvent2` *(verify)* or re-query the module info.

### 2.2 Event subscription

- `IVsDebugger.AdviseDebugEventCallback(IDebugEventCallback2)` delivers AD7 events. Identify the event by its IID (`riidEvent`), or by `QueryInterface` on the `IDebugEvent2`.
- Events of interest:
  - `IDebugModuleLoadEvent2` (load and unload)
  - `IDebugSymbolSearchEvent2` (symbols loaded/failed, including loads by others → ML-1)
  - `IDebugBreakpointEvent2`, `IDebugBreakEvent2`, `IDebugStepCompleteEvent2`, `IDebugExceptionEvent2` (break → CS-1, with the thread available directly)
  - `IDebugBreakpointErrorEvent2` and `IDebugBreakpointBoundEvent2` (pending breakpoint couldn't bind → BP-2 candidate, see §3)
  - `IDebugProcessCreateEvent2` / `IDebugProcessDestroyEvent2`, `IDebugProgramCreateEvent2` / `IDebugProgramDestroyEvent2`, `IDebugSessionCreateEvent2` / `IDebugSessionDestroyEvent2`
- Also use the DTE `DebuggerEvents`: `OnEnterRunMode`, `OnEnterBreakMode(dbgEventReason, ...)`, `OnEnterDesignMode`, `OnContextChanged` (frame activation → FA-1).
- Threading: find out which thread `IDebugEventCallback2.Event` is called on. Expected: the UI thread. Handlers must return quickly (NF-1) and post anything heavier to a queue.

---

## 3. Detecting breakpoints

There is no "breakpoint added" event in DTE. Candidate strategies; S4 picks one or a combination:

| Strategy | Notes |
|---|---|
| A. `IDebugBreakpointErrorEvent2` | When a pending breakpoint can't bind (no symbols), the engine reports an error/warning. Path: `IDebugErrorBreakpoint2.GetPendingBreakpoint` → `GetBreakpointRequest` → `IDebugBreakpointRequest2.GetRequestInfo(BPREQI_BPLOCATION)` → `BPLT_CODE_FILE_LINE` → `IDebugDocumentPosition2.GetFileName` and `GetRange`. Event-driven and exact, but only during a session. |
| B. Diff `DTE.Debugger.Breakpoints` | Snapshot the (file, line, enabled) set and diff it. Do it on: session start, `CommandEvents.AfterExecute` for breakpoint commands (`Debug.ToggleBreakpoint`, `Debug.InsertBreakpoint`, `Debug.EnableBreakpoint`, `Debug.DisableBreakpoint`, `Debug.BreakatFunction`, Breakpoints window commands), and a low-frequency timer (for example 1 s) while debugging as a safety net. |
| C. `IVsDebugger` breakpoint manager / pending breakpoint enumeration | Check whether VS 17.14 exposes a breakpoint-changed notification (`IDebugBreakpointManager`? `IVsDebuggerEvents`?) *(investigate)*. |

Recommended starting point: B for design time and session start, plus A for live events. Remove the timer if A proves complete.

---

## 4. Before-launch hook

The include list must be correct **before** the debugger starts loading modules. Options:

1. **Always applied** (chosen baseline): the on-demand settings stay applied for the whole time a qualifying solution is open (SPEC §7.4). The include list is refreshed whenever its inputs change. The before-launch refresh is then just an optimisation.
2. `CommandEvents.BeforeExecute` for `Debug.Start`, `Debug.StepInto`, `Debug.StepOver`, `Debug.RunToCursor`, `Debug.AttachtoProcess`, `Debug.ReattachtoProcess`, `Debug.StartDebuggingAllProjects`? Use these to run the recently-built scan and MRU pruning synchronously, with a timeout (NF-2).
3. `IVsDebugLaunchHook110` / `IVsDebugger4` launch hooks *(investigate whether they're available and suitable)*.
4. The `IDebugSessionCreateEvent2` / `IDebugProcessCreateEvent2` event as a last-chance update. It may be too late for early modules.

---

## 5. Call stack handling

- On a break event: take the thread from the AD7 event (or from `DTE.Debugger.CurrentThread`), then call `IDebugThread2.EnumFrameInfo(FIF_DEBUG_MODULEP | FIF_FUNCNAME | FIF_FLAGS, ...)`. For each frame, get its module and queue `LoadSymbols` if it has no symbols.
- For all threads: `IDebugProgram2.EnumThreads`. Unreal has more than 100 threads; this has to stay asynchronous (CS-6).
- On x64, stack walking without symbols is still accurate (unwind data is in the PE `.pdata`), so one pass is enough. Loading symbols may reveal inline frames, but never new modules.
- Refreshing the Call Stack window after loading: confirm whether it refreshes on its own (S2). If not, possible nudges are re-setting the current stack frame through `DTE.Debugger.CurrentStackFrame`, or using an `IVsDebugger` refresh. *(investigate)*

---

## 6. Spikes (Phase 0)

Run each spike in the Experimental instance (`/rootsuffix Exp`), against a small native test app **and** against the Unreal Editor. Write the results into §7.

| ID | Question | Pass criteria |
|---|---|---|
| S1 | Can the settings API read and write `IsManualLoad`, `IsLoadAdjacent` and `IncludeList`? Do changes show in Tools > Options? Are wildcards supported in include entries? Does VS itself already load call-stack modules' symbols in manual mode? How does "automatically choose" mode interact? | Settings round-trip; behaviour documented. |
| S2 | Does `IDebugModule3.LoadSymbols()` from the extension load symbols for a running Unreal process? Do pending breakpoints in that module then bind? Does the Call Stack window refresh? On which thread must it be called, and how long does it take? | A breakpoint binds and is hit after a programmatic load. |
| S3 | Timing: (a) does an include-list change made **during** a session apply to modules that load later? (b) Is the target stopped while `IDebugModuleLoadEvent2` is being handled? Can a breakpoint in `StartupModule()` of a late-loading module be hit through either route? | Documented. If neither works, evaluate the Concord route (§1.2). |
| S4 | Which breakpoint-detection strategy (§3) reliably catches add, remove, enable and disable, both at design time and during a session? | Chosen strategy shown to work for all 4 operations. |
| S5 | Which before-launch hook (§4) runs before the first module load for F5, Ctrl+F10, Attach and Reattach? | Documented. |
| S6 | Baseline measurements: UE editor startup to the main window under the debugger with (a) all symbols, (b) manual mode with an empty include list, (c) manual mode with about 20 modules. | Numbers recorded in `docs/BENCHMARKS.md`. |
| S7 | Do `IDebugSymbolSearchEvent2` or `IDebugModule2.GetInfo` show symbol loads triggered by the user (Modules window)? (ML-1) | Documented. |

---

## 7. Spike results

*(Empty. For each spike, record: date, VS version, outcome, code snippet, and decision.)*

