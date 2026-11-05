# Phase 0 Spike Harness (`spikes/`)

This folder holds throwaway spike verification code for Phase 0 ([docs/VS_DEBUGGER_INTEGRATION.md](../docs/VS_DEBUGGER_INTEGRATION.md) §6 & §7), verifying the **Option 2 (Concord Hybrid)** design ([docs/DECISIONS.md](../docs/DECISIONS.md) D-15).
Per `AGENTS.md`, spike code must never be committed into `src/`.

## Contents

- [`VSOnDemandSymbols.Spike.vsdconfigxml`](VSOnDemandSymbols.Spike.vsdconfigxml) — Concord IDE component manifest (`ComponentLevel = 210000`) declaring `ConcordSpikeMonitor` for `IDkmModuleInstanceLoadNotification`, `IDkmModuleInstanceUnloadNotification`, `IDkmModuleSymbolsLoadedNotification`, `IDkmBreakpointManagerNotification`, and `IDkmProcessExitNotification`.
- [`Phase0SpikeRunner.cs`](Phase0SpikeRunner.cs) — Self-contained VSIX + Concord helper that exercises:
  - **S1:** `IVsDebuggerSymbolSettingsManager120A` + `IVsDebuggerSymbolSettings170` toggling manual-load mode (`IsManualLoad = true`, `IsLoadAdjacent = false`, `AutomaticSymbolLoad = false`) **without touching `IncludeList`**, and restoring original user settings immediately.
  - **S2:** `DkmModuleInstance.TryLoadSymbols()` on-demand execution and timing.
  - **S3:** `IDkmModuleInstanceLoadNotification.OnModuleInstanceLoad` synchronous symbol loading while the target process is paused at DLL map time (before `DllMain`/`StartupModule()`).
  - **S4:** Concord `IDkmBreakpointManagerNotification` (`DkmPendingFileLineBreakpoint` / `DkmPendingFunctionBreakpoint`) + Strategy B (`CommandEvents.AfterExecute` + `DTE.Debugger.Breakpoints` diffing).
  - **S5:** `CommandEvents.BeforeExecute` for `StandardCommandSet97` and `guidVSDebugCommand` launch commands.
  - **S7:** Concord `IDkmModuleSymbolsLoadedNotification.OnModuleSymbolsLoaded` to distinguish user-initiated symbol loads (`ML-1`) from extension-initiated loads.
