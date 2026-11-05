# VS On-Demand Symbols — Functional Specification

| | |
|---|---|
| **Product** | Visual Studio extension (VSIX) "On-Demand Symbols" |
| **Status** | Draft v1.0, written before implementation |
| **Targets** | Visual Studio 2022 17.14+ and Visual Studio 2026 (x64), native C++ debugging |
| **Primary use case** | Debugging Unreal Engine 5.x editor builds (`UnrealEditor.exe` with about 800 `UnrealEditor-*.dll` modules) |
| **Related docs** | [ARCHITECTURE.md](ARCHITECTURE.md), [MODULE_RESOLUTION.md](MODULE_RESOLUTION.md), [VS_DEBUGGER_INTEGRATION.md](VS_DEBUGGER_INTEGRATION.md), [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md), [TESTING.md](TESTING.md), [DECISIONS.md](DECISIONS.md) |

---

## 1. Problem statement

The Unreal Editor loads about 800 DLLs at startup. Loading debug symbols (PDBs) for all of them can roughly double the editor's boot time under the debugger. Usually only a handful of modules need symbols: the game modules being worked on, the module where a breakpoint is set, and the modules on the call stack when execution stops.

Visual Studio can already "load only specified modules", but the user has to keep that include list up to date by hand. In practice, nobody does.

## 2. Goal

Automatically keep symbol loading limited to the modules that are likely to be needed. Then load symbols for any other module **immediately and transparently** when it becomes needed. The user should never have to open the Modules window to fix a breakpoint that won't bind.

### 2.1 Success criteria

1. **Startup time:** editor startup under the debugger with the extension enabled is within 10% of startup with symbol loading fully disabled, plus the cost of the modules that actually needed symbols.
2. **Breakpoints:** a breakpoint set in any C++ file binds and is hit, whether it was set before launch or while the process is running. No manual symbol loading is needed.
3. **Call stacks:** when execution breaks, the call stack shows function names and source lines for every frame without manual action (subject to the configured call-stack mode).
4. **No damage:** the user's own Visual Studio symbol settings are always restored. This includes after a VS crash.
5. **Explainability:** every symbol load the extension causes is logged with its reason.

### 2.2 Non-goals (v1)

- Managed (.NET) and mixed-mode symbol management. The extension only acts on native modules.
- Monolithic Unreal targets (packaged games, Shipping/Test builds where all code is in one EXE). The extension detects these and does nothing beyond the always-load list.
- CMake / Open Folder projects, and Unreal projects opened through `.uproject` workspace mode (no `.sln`). These are possible future work.
- Remote debugging, and debugging non-Windows platforms (consoles, Android, Linux).
- Managing symbol *servers* or symbol *paths*. The extension never modifies these.

---

## 3. Glossary

| Term | Meaning |
|---|---|
| **Module** | A DLL or EXE loaded into the debuggee. |
| **UBT module** | An Unreal Build Tool module, defined by `<Name>.Build.cs`. In modular editor builds, one UBT module = one DLL. |
| **Module key** | The extension's stable identity for a module (see §6.1). For Unreal this is the UBT module name, for example `MyGame` or `Engine`. For non-Unreal projects it is the output file name, for example `MyLib.dll`. |
| **Session / run** | One debug session (launch or attach) of a qualifying process, from start until the debugger returns to design mode. |
| **Trigger** | An event that means a module's symbols are needed (§5). |
| **Wanted set** | The set of module keys whose symbols should load as soon as their module loads. |
| **MRU** | The most-recently-used list of module keys that triggered recently. It is persisted per solution. |
| **Pinned** | A module key the user explicitly marked "Always load" for the solution. Pinned keys are never pruned. |
| **Qualifying session** | A debug session the extension acts on, according to the Scope setting (§7.1). |

---

## 4. How it works (overview)

1. While a qualifying solution is open (and the extension is enabled), the extension puts the Visual Studio symbol settings into **"Load only specified modules"** ("Automatically choose what module symbols to search for") mode (`IsManualLoad = true`). It also turns off **"Always load symbols located next to modules"** (`IsLoadAdjacent = false`, because Unreal PDBs live next to their DLLs) and **"Automatically include additional symbols when needed"** (`AutomaticSymbolLoad = false`, so call-stack symbol loading obeys the extension's `CallStackMode` and never-load rules; see D-13). The user's own include and exclude lists are never modified (see D-15).
2. Before each launch or attach, the extension computes the **wanted set** and publishes it to its in-process **Concord debugger component** (`ConcordSymbolMonitor`). As each native DLL loads (`IDkmModuleInstanceLoadNotification`), the target process is paused inside the loader notification; if the DLL matches the wanted set, the Concord component calls `DkmModuleInstance.TryLoadSymbols()` synchronously right there. That happens off the VS UI thread (NF-1) and before `DllMain` or `StartupModule()` runs.
3. While the session runs, the Concord component and IDE event hooks listen for triggers. When a trigger fires for a module that is already loaded without symbols, the extension calls `DkmModuleInstance.TryLoadSymbols()` right away. If the module is not loaded yet, its key is added to the live wanted set so `OnModuleInstanceLoad` loads its symbols the moment the DLL maps into the process.
4. When the solution closes, the extension is disabled, or VS shuts down, the user's original symbol settings flags are restored.

```mermaid
flowchart LR
    subgraph Inputs
        BP["Breakpoints"]
        CS["Call stacks on break"]
        RB["Recently built binaries"]
        MRU["MRU and pinned list"]
        AL["Always-load patterns"]
    end
    BP --> POL["Load policy"]
    CS --> POL
    RB --> POL
    MRU --> POL
    AL --> POL
    NL["Never-load patterns"] --> POL
    POL -->|"at module load (Concord)"| OML["OnModuleInstanceLoad → TryLoadSymbols()"]
    POL -->|"during session"| LS["TryLoadSymbols() now"]
    POL --> LOG["Output pane log"]
    POL --> STATE["Per-solution state (.vs)"]
```

---

## 5. Triggers: when symbols are loaded

Each trigger has a **reason code**. The reason is logged and stored in the MRU entry. A trigger always (a) loads the module's symbols if the module is loaded and has none, (b) adds the module key to the wanted set for the rest of the session, and (c) records an MRU hit (§5.6), unless the key matches a never-load pattern.

### 5.1 Breakpoints — reason `Breakpoint`

| ID | Requirement |
|---|---|
| BP-1 | At session start, every **enabled** file/line breakpoint (including tracepoints) in `DTE.Debugger.Breakpoints` is resolved to module key(s), and those keys join the wanted set. |
| BP-2 | A breakpoint added or enabled **while the process is running or in break mode** triggers immediately. If the module is loaded without symbols, its symbols load right away and the breakpoint binds. If the module isn't loaded yet, it joins the wanted set so symbols load when the module loads. |
| BP-3 | Disabled breakpoints are ignored by default (setting `IncludeDisabledBreakpoints`). |
| BP-4 | Function breakpoints that are module-qualified (`UnrealEditor-MyGame.dll!AMyActor::Tick` or `{,,UnrealEditor-MyGame.dll}AMyActor::Tick`) trigger for that module. Unqualified function breakpoints can't be resolved in v1; they are logged at Verbose level. |
| BP-5 | Breakpoints in header files resolve to the module that owns the header's folder (§6). Inlined copies in other modules are not covered; this is a documented limitation. |
| BP-6 | Breakpoints whose file can't be resolved to a module produce one warning in the log per file per session. |
| BP-7 | Data breakpoints and address breakpoints are ignored. |

### 5.2 Call stack on break — reason `CallStack`

| ID | Requirement |
|---|---|
| CS-1 | When the debugger enters break mode (breakpoint hit, step complete, user pause, exception, `__debugbreak`/`check`/`ensure`), the frames selected by `CallStackMode` are examined. Every frame whose module has no symbols triggers a load. |
| CS-2 | `CallStackMode` values: `None`; `CurrentThread`; `CurrentThreadAllThreadsOnException` (**default**); `AllThreads`. With the default, all threads are examined only when the stop reason is an exception, an unhandled exception/crash, or a hard-coded break (`__debugbreak`, which is how UE's `check`/`ensure` failures stop). |
| CS-3 | `CallStackMaxFrames` (default 128) limits how many frames per thread are examined. |
| CS-4 | Frames in modules outside the solution (Windows, CRT, drivers, third-party) also load symbols by default. This can use the user's configured symbol servers. Setting: `CallStackIncludeExternalModules` (default `true`). The never-load list can exclude specific ones. |
| CS-5 | After symbols load, the Call Stack window must show the resolved frames without the user having to step. If VS doesn't refresh on its own, the extension forces a refresh (see [VS_DEBUGGER_INTEGRATION.md](VS_DEBUGGER_INTEGRATION.md) §5). |
| CS-6 | Call-stack loads happen asynchronously, so break mode is entered without waiting. The UI may show unresolved frames briefly. |

### 5.3 Frame activation — reason `FrameActivated`

| ID | Requirement |
|---|---|
| FA-1 | When the user activates a stack frame (double-clicks it in the Call Stack, Threads or Parallel Stacks window, or switches the current thread) and that frame's module has no symbols, the extension loads them. Setting: `LoadOnFrameActivation` (default `true`). |
| FA-2 | When FA-1 fires for a thread whose stack hasn't been examined, the whole stack of that thread is examined, as in CS-1 with `CurrentThread` semantics. |

### 5.4 Recently built — reason `RecentlyBuilt`

| ID | Requirement |
|---|---|
| RB-1 | A module is "recently built" if its DLL **or** its PDB has a last-write time later than the start time of the previous qualifying session for this solution (`LastSessionStartUtc`). Only binary timestamps are used. Source file edits are deliberately not considered (see DECISIONS.md D-2). |
| RB-2 | Recently built modules are computed **before launch**, by scanning the known binaries folders (§6.4), and added to the wanted set. As a safety net, a module that loads during the session is also checked at its Concord module-load event (`OnModuleInstanceLoad`). |
| RB-3 | **First session** (no `LastSessionStartUtc` yet): binaries modified within `FirstSessionRecentWindowMinutes` (default 60) count as recently built. |
| RB-4 | **Mass-rebuild guard:** if more than `RecentlyBuiltMaxModules` (default 40) modules qualify in one session, the rule is suspended for that session and a warning is logged. This happens after an engine rebuild or a header change in Core. Modules already in the MRU or pinned list are unaffected. |
| RB-5 | Unreal hot reload (`UnrealEditor-X-1234.dll`) and Live Coding patch modules are always new files, so they qualify under RB-1. The extension must make sure their symbols load, because breakpoints in patched code depend on them. Live Coding patches are exempt from the RB-4 guard. |

### 5.5 Manual loads — reason `Manual`

| ID | Requirement |
|---|---|
| ML-1 | If symbols for a module are loaded by something other than the extension (for example the user clicks "Load Symbols" in the Modules window or on the "No symbols loaded" page), the extension records an MRU hit with reason `Manual`. |
| ML-2 | The "Load Symbols for This Module Now" command (§8.4) also records `Manual`. |

### 5.6 MRU list

| ID | Requirement |
|---|---|
| MRU-1 | Every trigger in §5.1–§5.5 records a hit for the module key. A hit sets `LastTriggerUtc = now`, `RunsSinceTrigger = 0`, and `LastReason` / `LastReasonDetail` (for example `Breakpoint: MyActor.cpp(42)`). It also increments `TriggerCount`. |
| MRU-2 | Being loaded **because** the module is in the MRU list does not count as a hit. Neither do the pinned list and always-load patterns. |
| MRU-3 | At the **end** of each qualifying session, every MRU entry that got no hit during that session has `RunsSinceTrigger` incremented. |
| MRU-4 | At the **start** of each qualifying session, before computing the wanted set, unpinned entries are pruned if **either** `RunsSinceTrigger >= MruMaxRuns` (default 5) **or** `now - LastTriggerUtc >= MruMaxAge` (default 24 hours). Both thresholds are configurable. |
| MRU-5 | So a module that triggered in run N is still loaded at the start of runs N+1 through N+5 (provided it's less than 24 hours since the trigger), and pruned at the start of run N+6. |
| MRU-6 | A breakpoint that stays set keeps re-triggering (BP-1) every run, so its module is never pruned while the breakpoint exists. |
| MRU-7 | `MruMaxEntries` (default 200) caps the list; the least recently triggered unpinned entries are evicted first. |
| MRU-8 | The user can pin, unpin and forget entries (§8.3, §8.5). |

### 5.7 Load policy (how the wanted set is computed)

```
Wanted = AlwaysLoad patterns
       ∪ Pinned keys
       ∪ MRU keys (after pruning)
       ∪ Breakpoint keys
       ∪ RecentlyBuilt keys (unless the mass-rebuild guard tripped)
       ∪ keys triggered earlier in this session
       − NeverLoad patterns
```

- Never-load always wins over automatic triggers. It does **not** block explicit user actions (Modules window, "Load Symbols Now" command).
- The wanted set is computed before launch, and recomputed incrementally when triggers fire during a session.

---

## 6. Resolving source files to modules

Full detail is in [MODULE_RESOLUTION.md](MODULE_RESOLUTION.md). Summary:

### 6.1 Module keys

- **Unreal:** the UBT module name, which is unique within a target (for example `Engine`, `MyGame`, `MyPluginEditor`). One key may match several DLL file names (configuration suffixes, hot-reload copies, Live Coding patches).
- **MSBuild / vcxproj:** the output file name of the project's active configuration, for example `MyLib.dll`.
- Keys are case-insensitive.

### 6.2 Unreal resolution

1. Special-case generated files under `Intermediate/Build/<Platform>/<Target>/Inc/<Module>/...` (UHT `.gen.cpp` files, unity files): the module name comes from the path.
2. Otherwise, walk up from the file's folder until a folder containing `*.Build.cs` is found. The module name is the file name without `.Build.cs`. Stop at the root of the drive, or at a folder containing `*.uproject`, `*.uplugin`, or the Engine root.
3. Map the module name to DLL file name(s), using UBT's `.modules` manifests next to the binaries when they exist. Otherwise use UBT's naming rules (derived from the UBT source; **pending analysis**, see MODULE_RESOLUTION.md §4).
4. At runtime, map loaded DLL names back to module names by parsing the file name (MODULE_RESOLUTION.md §5). This is how loaded modules are matched against keys.

### 6.3 Non-Unreal (vcxproj) resolution

1. Find the project(s) in the solution that contain the file.
2. Read the active configuration's `TargetPath`.
3. If the project builds a static library, follow reverse project references to the DLL/EXE projects that link it. Every such output becomes a key.
4. Files that aren't in any project: try the nearest project folder above the file. Otherwise the file is unresolved (BP-6).

### 6.4 Binaries folders (used for the recently-built scan)

- **Unreal:** `Engine/Binaries/Win64`, `<Project>/Binaries/Win64`, and `Binaries/Win64` under every plugin folder of the engine and the project. The plugin folder list is cached at solution open and refreshed lazily.
- **MSBuild:** the output directories of the solution's C++ projects for the active configuration.

---

## 7. Scope and lifecycle

### 7.1 Scope setting

`Scope` (default `UnrealOnly`):

| Value | Behaviour |
|---|---|
| `Off` | The extension never changes symbol settings. Same effect as the toolbar toggle being off. |
| `UnrealOnly` | Acts only when the open solution is an Unreal solution (§7.2). |
| `AllNative` | Acts for any solution that contains C++ projects. Unreal logic switches on automatically for Unreal solutions. |

The **Enabled** toggle (§8.1) is a separate master switch, so the user can pause the extension without losing the Scope choice.

### 7.2 Unreal solution detection

A solution counts as Unreal if any of the following is true:
- The solution folder, or the folder of any project in it, contains a `*.uproject`.
- It contains a project whose file lives under `**/Intermediate/ProjectFiles/` and an ancestor folder contains `Engine/Build/Build.version`.
- The user has forced it with the per-solution override (`ForceUnrealMode` in the per-solution state).

The engine root is the folder containing `Engine/Build/Build.version` found this way. The project root is the folder containing the `.uproject`. A solution can be engine-only (no project, for example `UE5.sln`).

### 7.3 Session qualification

A debug session qualifies when the extension is enabled, the Scope allows the solution, and the session debugs native code. In `UnrealOnly` mode, a process whose image name doesn't match `UnrealProcessPatterns` (default `UnrealEditor*.exe;UE4Editor*.exe;<ProjectName>*.exe`) is still debugged with the manual-load setting (because VS symbol settings are global), so for non-matching processes the Concord component can either leave them in manual mode or load their symbols if desired; triggers and MRU bookkeeping are skipped for non-qualifying processes, and they don't count as a run.

### 7.4 Lifecycle of the VS symbol settings

| Event | Action |
|---|---|
| Qualifying solution opened, extension enabled | Back up the user's symbol settings flags to disk (§9.2). Apply manual-load mode: `IsManualLoad = true`, `IsLoadAdjacent = false`, `AutomaticSymbolLoad = false`. The user's `IncludeList` is left untouched (D-15). |
| Before launch/attach (Debug.Start, Step Into to start, Attach to Process, Reattach) | Recompute the wanted set: prune the MRU, run the recently-built scan, resolve breakpoints. Publish the wanted set snapshot to the Concord component (`ConcordSymbolMonitor`). |
| Module loads during a session (`IDkmModuleInstanceLoadNotification`) | Concord component checks the wanted set (and RB-2 safety net) while the target process is paused at the DLL load event; if wanted, calls `DkmModuleInstance.TryLoadSymbols()` synchronously before `DllMain` / `StartupModule()`. |
| Trigger during a session | Call `DkmModuleInstance.TryLoadSymbols()` now if the module is loaded. Otherwise add it to the live wanted set snapshot so `OnModuleInstanceLoad` loads it when mapped. |
| Session ends | MRU bookkeeping (MRU-3). Save state. Store `LastSessionStartUtc` (start time of the session that just ended). |
| Solution closed / extension disabled / Scope changed to exclude / VS shutdown | Restore the user's settings from the backup. Delete the backup file. |
| VS starts and a backup file exists (previous crash) | Restore from the backup before doing anything else. Log it. |

Only these three boolean flags are ever modified: **manual-load mode** (`IsManualLoad`), **load-adjacent** (`IsLoadAdjacent`), and **automatic additional symbol load** (`IVsDebuggerSymbolSettings170.AutomaticSymbolLoad`). Symbol paths, cache folder, symbol servers, the **include list** (`IncludeList`), and the **exclude list** (`ExcludeList`) are never touched (D-15).

> Disabling the extension during a session takes effect at the next session. The log says so. The user can still use VS's built-in "Load All Symbols".

---

## 8. User interface

### 8.1 Enable toggle

- Command **Debug > On-Demand Symbols > Enabled** (checkable), also on a small **On-Demand Symbols** toolbar.
- Its state is global (per VS instance) and persisted.
- The tooltip shows the current state and Scope, for example "On-demand symbol loading: On (Unreal solutions only)".

### 8.2 Output window pane — "On-Demand Symbols"

At `Info` level, it logs:
- Session start: `Session #42 — UnrealEditor.exe — wanted 17 modules (pinned 2, MRU 9, breakpoints 3, recently built 2, always 1); pruned 4 from MRU.`
- Each load: `[12:01:03.512] Loading symbols: UnrealEditor-MyGame.dll — Breakpoint: MyActor.cpp(42)`
- Failures: `Symbols not found for UnrealEditor-Foo.dll (searched: ...)`
- Session end: `Session ended — 812 modules loaded, symbols for 23 (17 at load, 6 on demand). MRU: 21 entries.`
- Warnings: mass-rebuild guard tripped, unresolved breakpoint files, settings restored after a crash.

`Verbose` adds resolution details and skipped triggers. `Diagnostic` adds raw debugger events and timing.

### 8.3 Tool window — "On-Demand Symbols"

Opened from **Debug > Windows > On-Demand Symbols** and **View > Other Windows**. It shows a sortable grid with one row per tracked module key:

| Column | Content |
|---|---|
| Module | Module key (UBT module name or DLL name) |
| Binary | Resolved DLL file name(s) |
| Status | In the current session: Not loaded / Loaded (no symbols) / Symbols loaded / Symbols not found |
| Source | Pinned / MRU / Always / Session-only |
| Last reason | For example `Breakpoint: MyActor.cpp(42)` |
| Last triggered | Relative time |
| Runs since trigger | Number (shows when it will be pruned) |

- **Row actions:** Pin/Unpin, Forget, Load Symbols Now (during a session), Open Containing Folder, Copy Name.
- **Toolbar:** Enabled toggle, Clear MRU (with confirmation), Refresh, Open Log, Open Options.
- **Header strip:** live session counters (modules loaded / symbols loaded / pending wanted).
- The window must work in design mode (shows persisted state) and in debug mode (adds live status).

### 8.4 Context menu commands

On a C/C++ file in Solution Explorer, a document tab, or the code editor context menu:
- **Always Load Symbols for '{Module}'**: checkable, pins/unpins the resolved module key for this solution. The text shows the resolved module name. The command is hidden or disabled if the file can't be resolved.
- **Load Symbols for '{Module}' Now**: visible only while debugging. Loads immediately and records reason `Manual`.

### 8.5 Options page — Tools > Options > Debugging > On-Demand Symbols

| Category | Setting | Default |
|---|---|---|
| General | `Scope` (Off / UnrealOnly / AllNative) | UnrealOnly |
| General | `UnrealProcessPatterns` | `UnrealEditor*.exe;UE4Editor*.exe` (plus the project name, added automatically) |
| Breakpoints | `LoadOnBreakpoints` | true |
| Breakpoints | `IncludeDisabledBreakpoints` | false |
| Call stack | `CallStackMode` | CurrentThreadAllThreadsOnException |
| Call stack | `CallStackMaxFrames` | 128 |
| Call stack | `CallStackIncludeExternalModules` | true |
| Call stack | `LoadOnFrameActivation` | true |
| Recently built | `LoadRecentlyBuilt` | true |
| Recently built | `RecentlyBuiltMaxModules` | 40 |
| Recently built | `FirstSessionRecentWindowMinutes` | 60 |
| MRU | `MruEnabled` | true |
| MRU | `MruMaxRuns` | 5 |
| MRU | `MruMaxAgeHours` | 24 |
| MRU | `MruMaxEntries` | 200 |
| Lists | `AlwaysLoadPatterns` (`;`-separated wildcards on DLL/EXE file name) | `UnrealEditor.exe;UnrealEditor-Win64-*.exe` |
| Lists | `NeverLoadPatterns` | *(empty)* |
| Logging | `LogLevel` (Off / Info / Verbose / Diagnostic) | Info |

Pattern syntax: case-insensitive `*` and `?` wildcards, matched against the module **file name** (not the path). A pattern that starts with `ue:` matches a UBT module name instead (for example `ue:Engine`).

The options page uses a `DialogPage`, which works in both VS 2022 and VS 2026.

---

## 9. Persistence

### 9.1 Per-solution state

- Path: `<SolutionDir>/.vs/<SolutionName>/OnDemandSymbols/state.json`.
- Debug sessions started without a solution use `%LOCALAPPDATA%/OnDemandSymbols/nosolution/<hash of exe path>/state.json`.
- Writes are atomic (write a temp file, then rename). A corrupt file is renamed to `state.json.bad` and replaced with fresh state, with a warning in the log.

```json
{
  "schemaVersion": 1,
  "lastSessionStartUtc": "2026-11-05T20:00:00Z",
  "sessionCount": 42,
  "forceUnrealMode": null,
  "modules": {
    "MyGame": {
      "kind": "Unreal",
      "binaries": ["UnrealEditor-MyGame.dll"],
      "pinned": false,
      "lastTriggerUtc": "2026-11-05T19:58:12Z",
      "runsSinceTrigger": 0,
      "lastReason": "Breakpoint",
      "lastReasonDetail": "MyActor.cpp(42)",
      "triggerCount": 17
    }
  }
}
```

### 9.2 Symbol settings backup (global)

- Path: `%LOCALAPPDATA%/OnDemandSymbols/<VS instance id>[_<root suffix>]/symbol-settings-backup.json`. The instance ID separates VS 2022, VS 2026 and the Experimental instance.
- Contents: the user's original manual-load flag (`IsManualLoad`), load-adjacent flag (`IsLoadAdjacent`), automatic-symbol-load flag (`AutomaticSymbolLoad`), a timestamp, and the PID of the VS process that applied it.
- On startup, if a backup exists and its PID isn't a live process of this VS instance, restore it (crash recovery).

### 9.3 Extension options

Stored through `DialogPage` in the VS settings store (global per VS instance).

---

## 10. Non-functional requirements

| ID | Requirement |
|---|---|
| NF-1 | Handling a module-load event (`IDkmModuleInstanceLoadNotification`) for a non-wanted module must take under 0.1 ms on average on the Concord worker thread (and 0 ms on the VS UI thread): a hash lookup, no file I/O, no allocation-heavy work. |
| NF-2 | Wanted-set computation before launch must take under 300 ms with a warm cache, and under 2 s cold for a full engine solution. It runs off the UI thread; launch waits for it, with a timeout of 2 s, after which the last known wanted set is used. |
| NF-3 | No blocking file I/O on the UI thread. Follow the VS threading rules (`JoinableTaskFactory`). |
| NF-4 | The package loads in the background (`AllowsBackgroundLoading`), and auto-loads only when a solution with C++ projects is open (UI context rule). |
| NF-5 | Any exception in the extension is caught, logged, and must never break the debug session. If the extension fails, symbol settings fall back to the user's originals. |
| NF-6 | No network access by the extension itself. Symbol downloads only happen through VS's own symbol loading. |
| NF-7 | No telemetry. |
| NF-8 | Compatibility: VS 2022 17.14+ and VS 2026. Only APIs available in 17.14 may be used unconditionally. |

---

## 11. Known limitations (to document for users)

1. Code from headers inlined into other modules: breakpoints in it only bind in modules whose symbols are loaded (BP-5).
2. Unqualified function breakpoints don't trigger loads (BP-4).
3. Monolithic builds get no benefit (§2.2).
4. While on-demand mode is active, the VS symbol options page shows manual-load mode enabled (`IsManualLoad = true`, `IsLoadAdjacent = false`, `AutomaticSymbolLoad = false`). The extension adds a note to its log and tool window explaining this.

---

## 12. Open questions

These are answered with the defaults above. Revisit them during implementation.

1. Should an attach session count as a "run" for MRU pruning? **Default: yes.**
2. Should the main editor EXE plus a small core set (`Core`, `CoreUObject`, `Engine`) be always-loaded by default to make early crash stacks nicer? **Default: EXE only.** The call-stack trigger covers the rest.
3. When several Unreal processes are debugged in one session (for example editor plus a PIE client process), are their triggers shared? **Default: yes**, keys are per solution.
4. If VS already loads symbols for call-stack modules on its own in "only specified modules" mode, should the call-stack trigger (CS-1) defer to it and only record MRU hits? **Resolved (D-13):** VS 17.0+ exposes `IVsDebuggerSymbolSettings170.AutomaticSymbolLoad`, which the extension sets to `false` while active so CS-1 (`CallStackMode`) and `NeverLoadPatterns` have deterministic control.
