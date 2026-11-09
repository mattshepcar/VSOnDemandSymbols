# Architecture Decision Records (ADR) — On-Demand Symbols

This document records the active architectural decisions governing the **On-Demand Symbols** Visual Studio extension, followed by a summary of retired approaches explored during development.

---

## Active Architectural Decisions

### ADR-01: Native VS IncludeList for Startup Symbol Loading
* **Context:** The Unreal Editor loads ballpark ~1,000 DLLs at startup (e.g. 933 Unreal modules and 950+ total DLLs in Lyra). Loading symbols for all of them doubles debugger startup time.
* **Decision:** Put Visual Studio into manual-load mode (`IsManualLoad = true`, `IsLoadAdjacent = false`, `AutomaticSymbolLoad = false`) and populate `IVsDebuggerSymbolSettings120A.IncludeList` before launch with modules containing breakpoints (plus `UnrealEditor*.exe`).
* **Rationale:** Visual Studio's native debug engine (`CppDebug.dll`) checks `IncludeList` directly at DLL mapping time. If a binary matches, symbols load synchronously before `StartupModule()`. This achieves zero module-load overhead—no extension code, file I/O, or LINQ allocations run on the module mapping path.

### ADR-02: Standard DTE Programmatic Load for Mid-Session Break Mode
* **Context:** When execution stops (breakpoint hit, crash, assertion, or step), call stacks may contain frames for modules that were not preloaded at startup.
* **Decision:** Subscribe to `DTE.Events.DebuggerEvents.OnEnterBreakMode` and `OnContextChanged`. For any un-symbolized stack frame or thread location, invoke `EnvDTE90.Module.LoadSymbols("")` via DTE.
* **Rationale:** DTE's `LoadSymbols` immediately resolves symbols for loaded modules within ~100 ms, updating the Call Stack and Threads windows without requiring custom debugger transport hooks. To keep single-stepping (F10/F11) responsive, background thread stacks are inspected only during non-step breaks (breakpoint hits, exceptions, manual breaks), while single-stepping inspects only `CurrentThread`.

### ADR-03: Preloading Active and Disabled Breakpoints
* **Context:** Users frequently disable breakpoints during development and re-enable them while the debuggee is running.
* **Decision:** Include candidate binaries for modules containing *both* enabled and disabled breakpoints in `IncludeList` prior to launch.
* **Rationale:** Visual Studio does not consistently raise runtime bind events when re-enabling a previously disabled breakpoint. By preloading symbols for disabled breakpoint modules at startup, enabling a breakpoint while running binds immediately without delays or misses.

### ADR-04: Passive AD7 Breakpoint Bind Error Callback for Mid-Session Additions
* **Context:** Adding a new breakpoint while the debuggee is actively running fails to bind if the module's PDB is not loaded.
* **Decision:** Register a passive `IDebugEventCallback2` with `IVsDebugger.AdviseDebugEventCallback`. On `IDebugBreakpointErrorEvent2`, switch to the UI thread, update `IncludeList`, and invoke `mod.LoadSymbols("")` on matching modules in `DTE.Debugger.DebuggedProcesses`.
* **Rationale:** Allows breakpoints added while running to bind on-the-fly and turn solid red without restarting the debug session, using only public AD7 APIs.

### ADR-05: Scoped Remote Symbol Server Masking During Break Mode
* **Context:** When execution breaks, resolving symbols for OS and runtime DLLs on background threads can trigger slow internet queries to Microsoft Symbol Servers.
* **Decision:** During break-mode symbol loading (under the default `LocalOnly` mode), temporarily mask remote symbol servers (`UseMSSymbolServers = false`, `UseNugetSymbolServers = false`, and remote HTTP/HTTPS URLs in `SymbolPaths`). Once local resolution completes, unmask symbol servers immediately in a `finally` block.
* **Rationale:** Resolves all locally available PDBs (adjacent, local paths, local cache) instantly without background network latency, while preserving the user's ability to right-click and manually query remote servers.

### ADR-06: Modern Unified Settings Integration
* **Context:** Visual Studio 2022 17.10+ and Visual Studio 2026 introduced modern Unified Settings. Legacy `DialogPage` options display migration warning banners.
* **Decision:** Manage extension configuration purely via `registration.json` with `[ProvideSettingsManifest]`, nested under **Debugging -> Symbols -> On Demand Loading** (`debugging.symbols.onDemandLoading`). Read settings via `ISettingsReader`.
* **Rationale:** Eliminates legacy WinForms `DialogPage` overhead, avoids deprecation warnings, and places configuration directly where developers expect it alongside native symbol settings.

### ADR-07: Wildcard Binary Patterns
* **Context:** Unreal Engine compiles binaries across multiple configurations (Development, DebugGame, Debug, Shipping), architectures, and hot reload iterations (`-0001`).
* **Decision:** Format candidate entries in `IncludeList` using wildcard patterns (`UnrealEditor-{ModuleName}*.dll` and `UnrealEditor*.exe`).
* **Rationale:** Visual Studio's `IncludeList` natively supports `*` wildcards. A single clean entry per module matches all configurations and hot reloads, eliminating dozens of redundant explicit entries.

### ADR-08: Zero Persistent Session State / No MRU
* **Context:** Initial prototypes stored previously loaded modules in `.vs/<Solution>/state.json` and maintained an MRU (Most Recently Used) list across debugging sessions.
* **Decision:** Strip MRU lists, pinning, and per-solution state files. Each session starts clean with symbols loaded only for modules containing breakpoints.
* **Rationale:** On-demand symbol loading during break mode is fast (~100 ms). Carrying forward module history across sessions caused `IncludeList` to bloat over time, steadily degrading the startup speedup that the extension exists to deliver.

### ADR-09: Dedicated Unreal Engine 5 Scope
* **Context:** Non-Unreal C++ solutions typically contain only a few DLLs and do not suffer from the ~1,000-DLL startup problem.
* **Decision:** Target Unreal Engine 5 solutions exclusively. The extension checks for `*.uproject` or `Engine/Build/Build.version`. If neither is found, the extension remains completely inert.
* **Rationale:** Avoids brittle MSBuild project dependency crawling and eliminates unnecessary settings interception on standard projects.

### ADR-10: Transparent Background Operation
* **Context:** The extension should accelerate debugging without cluttering the IDE UI.
* **Decision:** Zero custom menu commands, tool windows, or status bar indicators. The extension activates automatically for Unreal solutions and restores original settings on solution close. Status and diagnostics are reported to the "On-Demand Symbols" Output pane.
* **Rationale:** Reduces UI maintenance overhead and provides a seamless, invisible user experience.

### ADR-11: Breaking Frame Context & Source Navigation Refresh
* **Context:** When execution breaks (e.g. crash, `__debugbreak()`, or assertion) inside a module whose symbols were skipped at startup, Visual Studio evaluates initial stack frame selection *before* `DTE.DebuggerEvents.OnEnterBreakMode` completes. As a result, Visual Studio falls back to the nearest caller on the stack that already had symbols loaded (such as `main` in `UnrealEditor.exe`) and opens that caller's source file instead of the crashing module's source file.
* **Decision:** After loading symbols on demand in `DebuggerSessionMonitor.LoadSymbolsForThreads`, inspect `CurrentThread.StackFrames` from top to bottom for the first frame with an existing source file (`EnvDTE90a.StackFrame2.FileName`). If that top source-resolvable frame belongs to a module whose symbols were just loaded on demand, set `DTE.Debugger.CurrentStackFrame` to that frame and open/activate its source file at `StackFrame2.LineNumber`.
* **Rationale:** Ensures that breaking or crashing into an un-symbolized module behaves identically to having full symbols preloaded—automatically focusing the crashing stack frame and opening the exact source file and line in the editor.

### ADR-12: Out-of-Proc DTE/ROT Automated Integration Testing with MiniUnreal Fixture
* **Context:** End-to-end verification of Visual Studio native debugger integration (`CppDebug.dll`, `IVsDebuggerSymbolSettings120A`, `IDebugBreakpointErrorEvent2`, `OnEnterBreakMode`) requires a real `devenv.exe /rootsuffix Exp` instance, while launching the full Unreal Engine editor is too slow and heavy for default inner-loop tests.
* **Decision:** Provide a self-contained native C++ fixture (`tests/Fixtures/MiniUnreal`) that mimics an Unreal solution (`MiniUnreal.uproject`, `{Module}.Build.cs`, `UnrealEditor.exe`, `UnrealEditor-ModuleA.dll`, `UnrealEditor-ModuleB.dll`) alongside an out-of-process STA COM test runner (`tests/VSOnDemandSymbols.IntegrationTests`) that boots `devenv.exe /rootsuffix Exp` and attaches via the Windows Running Object Table (`!VisualStudio.DTE.*:<PID>`).
* **Rationale:** Compiles in ~2 seconds and executes the entire 5-scenario end-to-end debugger test suite against a real Visual Studio Experimental instance in ~13 seconds without brittle mouse/keyboard UI automation.

### ADR-13: Local PDB Existence Guard Before DTE `Module.LoadSymbols("")`
* **Context:** During real-world debugging of Unreal Engine 5 + Lyra (~1,062 mapped DLLs and ~80 threads), thread call stacks routinely include Windows OS libraries (`user32.dll`, `win32u.dll`, `combase.dll`, `ws2_32.dll`) whose PDBs are not yet in the local `SymbolCache`, as well as stripped third-party shipping DLLs (`EOSSDK-Win64-Shipping.dll`) that have no `.pdb` on disk. Calling `EnvDTE90.Module.LoadSymbols("")` on a module when no local `.pdb` exists and remote symbol servers are masked (`LocalOnly`) or disabled causes Visual Studio's native debug engine (`CDMModule::LoadSymbols`) to open a modal Win32 "Browse for PDB" file dialog (`GetOpenFileNameW`) on the UI thread, freezing the IDE.
* **Decision:** Before calling `mod.LoadSymbols("")` in `DebuggerSessionMonitor` (when symbol servers are masked or disabled), query `SymbolSettingsManager.HasLocalSymbolFile(moduleFileName, fullModulePath)` to verify that a `.pdb` exists either adjacent to the module on disk or inside Visual Studio's configured `CachePath` / local `SymbolPaths` (supporting flat, two-tier, and three-tier symstore directory layouts). If no local `.pdb` exists, record the module in `_loadedModulesThisSession` and skip calling `mod.LoadSymbols("")`.
* **Rationale:** Completely eliminates modal file-picker dialogs during break-mode and breakpoint-bind symbol loading while adding less than 5 ms across 1,062 modules.

### ADR-14: Zero-Write Headless Lyra Profiling Suite (`--profile-lyra`)
* **Context:** In addition to the fast `MiniUnreal` functional tests, we need automated performance and scale profiling against a real Unreal Engine 5 + Lyra Starter Game instance (~1,062 DLLs, 26+ GB of PDBs) that compares Cold Boot vs. Warm Boot and respects the read-only UE root constraint.
* **Decision:** Add an opt-in `--profile-lyra` suite (`LyraProfilingTests`) in `tests/VSOnDemandSymbols.IntegrationTests` that resolves the Unreal Engine root via `UnrealEnvironmentConfig` (`--ue-root`, `VSONDEMANDSYMBOLS_UE_ROOT`, `testsettings.local.json`, or default `D:\UnrealEngine`), generates a zero-write harness solution in `%TEMP%\VSOnDemandSymbols_LyraHarness` (redirecting `-UserDir` and `-abslog` to `%TEMP%`), launches `UnrealEditor-Win64-DebugGame-Cmd.exe` headlessly (`-nullrhi -unattended`), and parses structured `[Profile]` telemetry across Cold Boot, In-Session Warm Break, Mid-Session Breakpoint Binding, and Warm Boot.
* **Rationale:** Measures real-world symbol loading latency at 1,000+ module scale in ~32 seconds without writing a single byte to the Unreal Engine directory or requiring Docker containers.

---

## Historical Explorations & Retired Approaches

During early development (Phases 0–4), several complex alternatives were prototyped, measured, and systematically retired:

| Subsystem / Feature | Approach Explored | Why It Was Retired |
|---|---|---|
| **Concord Engine Component** | Prototyped using `Microsoft.VisualStudio.Debugger.Engine` and `.vsdconfigxml` to intercept DLL loads via `IDkmModuleInstanceLoadNotification` and call `DkmModuleInstance.TryLoadSymbols()`. | `TryLoadSymbols()` throws `NotImplementedException` (`0x80004001` / `E_NOTIMPL`) for native C++ modules in Visual Studio. Implementing Concord's `IDkmBreakpointManagerNotification` also corrupted editor breakpoint glyphs. Stripping Concord removed custom build tooling (`vsdconfigtool.exe`) and private IDE DLL dependencies. |
| **MSBuild Project Crawling** | Evaluated `MsBuildResolver` and `VCProjectEngine` to crawl project dependency trees for generic non-Unreal C++ solutions. | Introduced ~700 lines of complex COM interop, potential UI-thread deadlocks, and evaluation delays. Standard C++ solutions do not have the ~1,000-DLL problem, making this unnecessary. |
| **Recently-Built Scanners** | Scanned DLL and PDB write timestamps to pre-load symbols for recently compiled modules. | Binary modification timestamps are a poor proxy for debugging intent. Syncing source control or switching branches recompiled DLLs the user was not debugging. Intent is better captured by breakpoints. |
| **MRU & Module Pinning** | Maintained an MRU cache and module pinning list in `.vs/state.json` persisted across VS runs. | Bloated `IncludeList` over time, eroding startup performance. On-demand break resolution is fast enough (~100 ms) that session history caching was unnecessary. |
| **Debug Menu Toggle** | Implemented `Debug > On-Demand Symbols > Enabled` menu command via `VSCommandTable.vsct`. | Provided negligible utility over Visual Studio's Extension Manager. Removed to eliminate `.vsct` compilation and keep menus clean. |
