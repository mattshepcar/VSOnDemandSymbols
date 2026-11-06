# Testing Strategy

## 1. Unit tests (Core) — `tests/VSOnDemandSymbols.Core.Tests`

- Framework: xUnit. Target `net8.0` for fast local runs, and also `net472` to match the VS runtime.
- No VS dependencies, no real clock (`FakeClock`), and either a fake file system or temporary fixture folders.
- Required coverage:
  - **LoadPolicy:** set algebra of SPEC §5.7 (IncludeList from breakpoint keys + AlwaysLoad − NeverLoad), never-load wins over triggers but not over manual loads, pattern matching including `ue:` patterns.
  - **Unreal resolver:** every golden case in [MODULE_RESOLUTION.md](MODULE_RESOLUTION.md) §7, plus multiple-Build.cs folders, deep nesting, plugin/project/engine owners, Intermediate paths, unresolved paths, cache invalidation.
  - **DLL name parser:** positive and negative cases, including Windows API-set DLLs, third-party DLLs with hyphens, and hot reload / config suffixes.
  - *(MruStore and MRU StateRepository tests removed per D-20)*
- Fixture trees: put minimal fake engine/project trees under `tests/VSOnDemandSymbols.Core.Tests/Fixtures/` (empty `.Build.cs`, `.uplugin`, `.uproject`, `Build.version`, and sample `.modules` JSON).

## 2. Integration tests (opt-in)

- `UnrealEngineIntegrationTests`: skipped unless the environment variable `ODS_UE_ROOT` points at an engine tree (for example `D:\UnrealEngine`). Checks that forward and reverse naming round-trip for all real modules (Phase 2).
- Optionally `ODS_UE_PROJECT` for a project with plugins.

## 3. Manual test scenarios (Experimental instance)

Run these against a real UE 5.x project (C++ game module plus one project plugin) **and**, where relevant, against `tests/Samples/NativeSample` with Scope = AllNative. Record pass/fail in the PR or commit description.

| # | Scenario | Expected |
|---|---|---|
| M1 | Fresh state, no breakpoints, F5 | Only the always-load list gets symbols. The log shows the session summary. Startup is close to the S6 "empty include list" baseline. |
| M2 | Breakpoint in a game module `.cpp` set **before** F5 | The module's symbols load at module load; the breakpoint is hit. Reason `Breakpoint`. |
| M3 | Breakpoint set in an engine module (`Engine/Source/Runtime/Engine/...`) **while running** | Symbols load immediately; the breakpoint turns solid and is hit. |
| M4 | Breakpoint in a plugin module that loads late (LoadingPhase PostEngineInit or on demand), set while running before it loads | Symbols load synchronously in Concord `OnModuleInstanceLoad` when the module loads; the breakpoint in `StartupModule()` binds and is hit. |
| M5 | Pause (Break All) during gameplay | Current thread stack fully symbolized (default mode). |
| M6 | Trigger an `ensure()` / `check()` failure | All threads' stacks symbolized. |
| M7 | Double-click a frame on another thread in the Threads window | That thread's frames are symbolized (FA-1/FA-2). |
| M8 | *(Removed per D-16)* | — |
| M9 | *(Removed per D-16)* | — |
| M10 | Live Coding patch (Ctrl+Alt+F11) with a breakpoint in the changed function | The patch's symbols load; the breakpoint in the patched code is hit. |
| M11 | *(Removed per D-20: MRU removed)* | — |
| M12 | *(Removed per D-20: MRU removed)* | — |
| M13 | *(Removed per D-20: Pinning removed)* | — |
| M14 | Never-load `UnrealEditor-Foo.dll`, set a breakpoint in Foo | No automatic load; the log explains why; the Modules window "Load Symbols" still works. |
| M15 | Load symbols manually through the Modules window for module Y | Event logged with reason `Manual`. |
| M16 | Close the solution | Tools > Options > Debugging > Symbols shows exactly the user's original settings. |
| M17 | Kill devenv.exe mid-session, restart VS | The settings are restored at startup, and the log says so. |
| M18 | Toggle Enabled off, F5 | Normal VS symbol behaviour (all symbols). |
| M19 | Attach to a running editor | The wanted set gets symbols at attach; triggers work. |
| M20 | Scope = UnrealOnly, open a non-Unreal C++ solution | The extension is inert; settings untouched. |
| M21 | Scope = AllNative, NativeSample: breakpoint in a static-lib source file | The DLL/EXE that links the lib gets symbols. |
| M22 | DebugGame Editor configuration | The game modules (`-Win64-DebugGame`) are matched correctly. |

## 4. Performance checks

- Use the Diagnostic log level to time: breakpoint module resolution, IncludeList update, and each `LoadSymbols` call.
- Compare editor startup time (launch to main window) for: VS default (all symbols), extension with empty IncludeList (only always-load list), and extension with breakpoint modules in IncludeList. Record the results in `docs/BENCHMARKS.md`.

