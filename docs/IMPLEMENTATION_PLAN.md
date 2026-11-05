# Implementation Plan

Work through the phases in order. Each phase ends with its acceptance criteria met, the build green, and the docs updated. Tick the boxes as items are completed. Agents: keep this file current, because it is the shared progress tracker.

---

## Phase 0 — Spikes and baseline (de-risk the VS APIs)

Goal: answer the open questions in [VS_DEBUGGER_INTEGRATION.md](VS_DEBUGGER_INTEGRATION.md) §6 before building the real thing. Spike code is throwaway. Put it in `spikes/` or on a branch, not in `src/`.

- [x] S1 Symbol settings API round-trip (verified `IVsDebuggerSymbolSettingsManager120A` + `IVsDebuggerSymbolSettings170.AutomaticSymbolLoad` toggling manual-load mode without modifying `IncludeList`; documented in [VS_DEBUGGER_INTEGRATION.md](VS_DEBUGGER_INTEGRATION.md) §7.1; harness in [`spikes/Phase0SpikeRunner.cs`](../spikes/Phase0SpikeRunner.cs))
- [x] S2 Programmatic `LoadSymbols` + breakpoint binding + call stack refresh (verified Concord `DkmModuleInstance.TryLoadSymbols()` and `BMFileLineBreakpointCollection.OnModuleSymbolsLoaded` auto-binding in [VS_DEBUGGER_INTEGRATION.md](VS_DEBUGGER_INTEGRATION.md) §7.2)
- [x] S3 Module-load event timing & Concord `.vsdconfig` compilation (verified Concord `IDkmModuleInstanceLoadNotification` suspends the target process on the Concord engine thread at `ComponentLevel = 210000`, allowing synchronous `TryLoadSymbols()` before `DllMain`/`StartupModule()`; verified `.vsdconfigxml` → `.vsdconfig` compilation with `vsdconfigtool.exe` in [VS_DEBUGGER_INTEGRATION.md](VS_DEBUGGER_INTEGRATION.md) §7.3)
- [x] S4 Breakpoint change detection (verified Concord `IDkmBreakpointManagerNotification` with strongly typed `DkmPendingFileLineBreakpoint` / `DkmPendingFunctionBreakpoint` + Strategy B `CommandEvents.AfterExecute` in [VS_DEBUGGER_INTEGRATION.md](VS_DEBUGGER_INTEGRATION.md) §7.4)
- [x] S5 Before-launch hook (verified D-9/D-15 always-applied manual-load mode + `CommandEvents.BeforeExecute` on `StandardCommandSet97` and `guidVSDebugCommand` in [VS_DEBUGGER_INTEGRATION.md](VS_DEBUGGER_INTEGRATION.md) §7.5)
- [ ] S6 Baseline startup timings → [`docs/BENCHMARKS.md`](BENCHMARKS.md) (protocol and table ready; interactive UE5 Editor timing numbers pending user run on `D:\UnrealEngine`)
- [x] S7 Detecting user-initiated symbol loads (verified Concord `IDkmModuleSymbolsLoadedNotification` in [VS_DEBUGGER_INTEGRATION.md](VS_DEBUGGER_INTEGRATION.md) §7.7)
- [x] Record results in [VS_DEBUGGER_INTEGRATION.md](VS_DEBUGGER_INTEGRATION.md) §7. Update [SPEC.md](SPEC.md), [ARCHITECTURE.md](ARCHITECTURE.md), and [DECISIONS.md](DECISIONS.md) (D-12, D-13, D-15) where the results change the design.

**Accept when:** every spike has a documented outcome, and there is a confirmed mechanism for (a) limiting automatic loads, (b) loading on demand, and (c) detecting breakpoints.

## Phase 1 — Project structure and Core library

- [x] Move the VSIX project to `src/VSOnDemandSymbols/`. Update the `.slnx`. Fix the manifest metadata (display name "On-Demand Symbols", description, icon placeholder).
- [x] Create `src/VSOnDemandSymbols.Core` (netstandard2.0, nullable enabled) and `tests/VSOnDemandSymbols.Core.Tests` (xUnit).
- [x] Abstractions: `IClock`, `IFileSystem`, `ILogger`, `IProjectModelProvider`.
- [x] `ModuleKey`, `WildcardPattern` (including the `ue:` prefix), `TriggerReason`.
- [x] `MruStore` with the exact pruning semantics of SPEC §5.6, with tests for MRU-3 to MRU-7, including the boundary cases (5th vs 6th run, exactly 24 h).
- [x] `StateRepository` (JSON, atomic write, corrupt-file recovery, schema version).
- [x] `LoadPolicy` (SPEC §5.7) with tests.
- [x] ~~`RecentlyBuiltScanner`~~ *(removed per [DECISIONS.md](DECISIONS.md) D-16)*
- [x] Unreal resolver skeleton: Build.cs walk-up, Intermediate path special case, owner detection, manifest reader, DLL name parser, all using the *provisional* rules. Fixture trees under `tests/.../Fixtures/`.

**Accept when:** Core builds, all unit tests pass, the VSIX still builds and loads in the Experimental instance.

## Phase 2 — UBT analysis (needs `D:\UnrealEngine`)

> **AI Quota Optimization Plan:** Follow [`docs/PHASE2_FLASH_DELEGATION_PLAN.md`](PHASE2_FLASH_DELEGATION_PLAN.md) to delegate all `D:\UnrealEngine` file discovery, UBT code extraction, and binary directory scans to **Gemini Flash** (`flash_lite` / `flash`) subagents (`Tasks F1–F4`), keeping raw engine files out of the primary context window.

- [ ] Work through the checklist in [MODULE_RESOLUTION.md](MODULE_RESOLUTION.md) §4 (via Flash subagent Tasks F1–F3 in [`PHASE2_FLASH_DELEGATION_PLAN.md`](PHASE2_FLASH_DELEGATION_PLAN.md)).
- [ ] Record findings, with source references, in MODULE_RESOLUTION.md §8. Update §4 and §5 to "confirmed".
- [ ] Update the naming and parsing code and the golden tests.
- [ ] Validate against a real build: list every DLL in `Engine/Binaries/Win64` and all plugin binaries folders, and check that the parser maps 100% of `UnrealEditor-*.dll` to a module that has a matching `.Build.cs`. Write this as an opt-in integration test, skipped when the engine path isn't present (env var `ODS_UE_ROOT`).

**Accept when:** the reverse map and forward map round-trip for every module in a real engine build.

## Phase 3 — Debugger integration core (Concord + Manual-Load Settings)

- [ ] `SymbolSettingsController`: backup, apply (`IsManualLoad = true`, `IsLoadAdjacent = false`, `AutomaticSymbolLoad = false`), restore, crash recovery, leaving the user's `IncludeList` and `ExcludeList` untouched (D-15).
- [ ] Solution event handling: Unreal detection (SPEC §7.2), apply/restore on open/close.
- [ ] Concord component (`VSOnDemandSymbols.vsdconfigxml` → `.vsdconfig` asset in `.vsixmanifest`): `ConcordSymbolMonitor`, `ConcordSymbolBridge`, `SessionTracker`, `LoadedModuleRegistry` (`DkmModuleInstance`).
- [ ] `SymbolLoader` (`DkmModuleInstance.TryLoadSymbols()`) with de-duplicating queue and logging.
- [ ] Before-launch hook (per S5): prune the MRU, compute the wanted set, publish immutable `WantedSet` snapshot to `ConcordSymbolBridge`.
- [ ] Output pane logger.

**Accept when:** launching the UE editor with an MRU containing `MyGame` loads symbols only for the wanted set via `ConcordSymbolMonitor.OnModuleInstanceLoad`. Session start and end are logged. Settings are restored when the solution closes, and after killing devenv.exe and restarting.

## Phase 4 — Triggers

- [ ] Breakpoints: before launch (BP-1), live (BP-2), disabled (BP-3), qualified function breakpoints (BP-4), unresolved warnings (BP-6).
- [ ] Call stack modes (CS-1 to CS-6).
- [ ] Frame activation (FA-1, FA-2).
- [ ] Manual-load detection (ML-1).
- [ ] MRU hit recording for all triggers; run bookkeeping at session end.
- [ ] MSBuild resolver through `ProjectModelProvider` (needed for `AllNative` scope), tested on `tests/Samples/NativeSample`.

**Accept when:** every scenario in [TESTING.md](TESTING.md) §3 passes manually.

## Phase 5 — UI

- [ ] `.vsct`: Debug > On-Demand Symbols submenu, toolbar, toggle command with checked state.
- [ ] Options `DialogPage` with every setting in SPEC §8.5, wired to live behaviour.
- [ ] Context menu commands (SPEC §8.4) with dynamic text.
- [ ] Tool window (SPEC §8.3): WPF, MVVM, VS theme colours (`EnvironmentColors`), works in design and debug mode.

**Accept when:** every UI surface works in both light and dark themes, and in VS 2022 17.14 and VS 2026.

## Phase 6 — Hardening and release

- [ ] Performance pass against NF-1 and NF-2, using the Diagnostic log timings. Record the final numbers in `docs/BENCHMARKS.md`.
- [ ] Fault-injection review: every handler is wrapped; an exception never leaves the user's settings modified.
- [ ] README.md for users (install, how it works, limitations from SPEC §11).
- [ ] VSIX manifest: version, tags, icon, license, release notes. Confirm install on VS 2022 17.14 and VS 2026.
- [ ] CHANGELOG.md.

