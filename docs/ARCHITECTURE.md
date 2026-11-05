# Architecture

## 1. Solution layout (target)

```
VSOnDemandSymbols/
├─ AGENTS.md
├─ VSOnDemandSymbols.slnx
├─ docs/                                  # spec and design docs (this folder)
├─ src/
│  ├─ VSOnDemandSymbols/                  # VSIX package + Concord IDE component (net472).
│  │  ├─ VSOnDemandSymbolsPackage.cs
│  │  ├─ VSOnDemandSymbols.vsdconfigxml   # Concord component configuration → compiled to .vsdconfig
│  │  ├─ source.extension.vsixmanifest    # Registers Package + DebuggerEngineExtension asset
│  │  ├─ VSOnDemandSymbols.vsct           # commands, menus, toolbar
│  │  ├─ Commands/                        # Toggle, Pin, LoadNow, ShowToolWindow
│  │  ├─ Debugger/                        # Concord component + SymbolSettingsController + DTE hooks (§3)
│  │  ├─ Projects/                        # IProjectModelProvider implementation (DTE/VCProjectEngine)
│  │  ├─ Options/                         # DialogPage
│  │  ├─ ToolWindow/                      # WPF tool window + view model
│  │  └─ Logging/                         # Output window pane logger
│  └─ VSOnDemandSymbols.Core/             # netstandard2.0. NO Visual Studio references.
│     ├─ Modules/                         # ModuleKey, resolvers, Unreal naming, manifests, DLL parser
│     ├─ Policy/                          # LoadPolicy, TriggerReason, WildcardPattern
│     ├─ Mru/                             # MruStore, pruning rules
│     ├─ State/                           # JSON state repository, schema, atomic writes
│     └─ Abstractions/                    # IClock, IFileSystem, ILogger, IProjectModelProvider
└─ tests/
   ├─ VSOnDemandSymbols.Core.Tests/       # xUnit (net8.0 + net472), fixture trees, golden tests
   └─ Samples/NativeSample/               # small exe + dll + static lib solution for manual testing
```

The existing blank project currently sits at the repo root. Phase 1 moves it to `src/VSOnDemandSymbols/` and updates the `.slnx`.

**Rule:** anything that can be unit-tested without VS goes in Core. The VSIX project is a thin adapter.

## 2. Component diagram

```mermaid
flowchart TD
    subgraph VSIX["VSIX + Concord Component (VS-specific)"]
        PKG["VSOnDemandSymbolsPackage"]
        SSC["SymbolSettingsController (manual-load flags only)"]
        CSM["ConcordSymbolMonitor (IDkmModuleInstanceLoad / SymbolsLoaded / BreakpointManager)"]
        ST["SessionTracker"]
        MR["LoadedModuleRegistry (DkmModuleInstance)"]
        SL["SymbolLoader (DkmModuleInstance.TryLoadSymbols)"]
        BW["BreakpointWatcher (CommandEvents + Concord unbound BP)"]
        CST["CallStackTrigger"]
        FAT["FrameActivationTrigger"]
        PMP["ProjectModelProvider"]
        UI["Commands / ToolWindow / Options / OutputLog"]
    end
    subgraph CORE["Core (pure .NET)"]
        RES["CompositeResolver (Unreal + MsBuild)"]
        NAM["UnrealBinaryNaming + ManifestReader + DllNameParser"]
        POL["LoadPolicy"]
        MRU["MruStore"]
        REPO["StateRepository"]
    end
    PKG --> SSC
    PKG --> ST
    CSM --> MR
    CSM --> BW
    CSM --> POL
    BW --> RES
    CST --> MR
    FAT --> MR
    BW --> POL
    CST --> POL
    FAT --> POL
    POL --> SL
    SL --> MR
    RES --> NAM
    RES --> PMP
    POL --> MRU
    MRU --> REPO
    ST --> MRU
    UI --> POL
    UI --> MRU
```

## 3. Components

| Component | Responsibility | Thread |
|---|---|---|
| `VSOnDemandSymbolsPackage` | Async background load; builds the object graph; subscribes to solution events; crash-recovery restore at startup. | Background, then UI for service access |
| `SymbolSettingsController` | Back up, apply and restore VS symbol settings boolean flags (`IsManualLoad = true`, `IsLoadAdjacent = false`, `AutomaticSymbolLoad = false`; SPEC §7.4, §9.2). **Never modifies `IncludeList` or `ExcludeList`.** Single owner of all writes to symbol settings. Idempotent. | UI |
| `ConcordSymbolMonitor` | IDE-side Concord component (`ComponentLevel = 210000`, in-process in `devenv.exe`). Implements `IDkmModuleInstanceLoadNotification`, `IDkmModuleInstanceUnloadNotification`, `IDkmModuleSymbolsLoadedNotification`, `IDkmBreakpointManagerNotification`, and `IDkmProcessExitNotification`. Calls `moduleInstance.TryLoadSymbols()` synchronously on module load when the module matches the wanted set. | Concord engine worker thread (off UI thread) |
| `ConcordSymbolBridge` | Thread-safe in-process bridge holding the current immutable wanted-set snapshot and routing Concord notifications to `LoadedModuleRegistry`, `BreakpointWatcher`, `SymbolLoader`, and `OutputLog`. | Any (lock-free / concurrent) |
| `SessionTracker` | Session start/end, qualification (SPEC §7.3), run counting. | UI / Engine |
| `LoadedModuleRegistry` | For each process: loaded modules → (`DkmModuleInstance`, path, `ModuleKey?`, symbol state). Reverse-maps each module to a key once, at load. | Any (`ConcurrentDictionary`) |
| `SymbolLoader` | De-duplicating queue of load requests (key + reason). Calls `DkmModuleInstance.TryLoadSymbols()`, tracks the result, and logs. Never loads the same module twice per session. | Background / Concord worker thread |
| `BreakpointWatcher` | Detects breakpoint changes via `CommandEvents.AfterExecute` (design-time and run-time) plus Concord `IDkmBreakpointManagerNotification` (`DkmPendingFileLineBreakpoint` / `DkmPendingFunctionBreakpoint`), resolves files to keys, raises `Breakpoint` triggers. | UI for DTE, background for resolution |
| `CallStackTrigger` / `FrameActivationTrigger` | SPEC §5.2 and §5.3. Examines frames on break / frame activation and queues `SymbolLoader` for modules without symbols. | UI / background |
| `LoadPolicy` (Core) | Computes the wanted set (SPEC §5.7) and answers "should module X load?" for a reason; applies the always/never patterns. | Any (immutable snapshots) |
| `MruStore` (Core) | Hits, run increments, pruning, pin/forget. | Any (locked) |
| `StateRepository` (Core) | Load/save of `state.json` with atomic writes and schema versioning. | Background |
| `CompositeResolver` (Core) | Source file → keys (MODULE_RESOLUTION.md). Cached. | Background |
| `ProjectModelProvider` (VSIX) | Exposes solution/project info (TargetPath, project membership, static-lib references) to Core through `IProjectModelProvider`. Snapshots are built on the UI thread and then used from any thread. | UI to build, any to read |
| `OutputLog` | `ILogger` implementation that writes to the "On-Demand Symbols" output pane; level filter. | Any (`OutputStringThreadSafe`) |

## 4. Key sequences

### 4.1 Launch (F5)

```mermaid
sequenceDiagram
    participant U as User
    participant VS as VS Shell
    participant H as BeforeLaunch hook
    participant P as LoadPolicy
    participant B as ConcordSymbolBridge
    participant C as ConcordSymbolMonitor
    U->>VS: F5
    VS->>H: Debug.Start BeforeExecute
    H->>P: Prune MRU, resolve breakpoints
    P-->>H: Wanted set (keys → DLL names)
    H->>B: Publish immutable WantedSet snapshot
    VS->>C: Launch UnrealEditor.exe (manual-load mode active)
    loop For each of ~800 native DLLs (on Concord worker thread, target paused)
        C->>B: Is module in WantedSet?
        alt Wanted and no symbols loaded
            C->>C: moduleInstance.TryLoadSymbols() (before DllMain/StartupModule)
        end
    end
```

### 4.2 Breakpoint set while running

```mermaid
sequenceDiagram
    participant U as User
    participant C as ConcordSymbolMonitor / BreakpointWatcher
    participant R as Resolver
    participant P as LoadPolicy
    participant L as SymbolLoader
    participant M as LoadedModuleRegistry
    U->>C: Sets breakpoint in MyActor.cpp
    C->>R: Resolve file
    R-->>C: Unreal:MyGame
    C->>P: Trigger(MyGame, Breakpoint)
    P->>M: Is MyGame loaded?
    alt loaded without symbols
        P->>L: moduleInstance.TryLoadSymbols()
        L-->>U: Breakpoint binds automatically
    else not loaded yet
        P->>P: Add to WantedSet snapshot (loaded when DLL maps in OnModuleInstanceLoad)
    end
```

## 5. Cross-cutting rules

- **Threading:** follow the VS threading rules. Use `JoinableTaskFactory`, `ThreadHelper.ThrowIfNotOnUIThread()` in every method that touches COM/DTE, and no `.Result` / `.Wait()`. Concord callbacks (`ConcordSymbolMonitor`) run on Concord worker threads and must never block on the VS UI thread. Do file I/O on background threads through `TaskScheduler.Default`.
- **Fault isolation:** wrap every event handler and Concord notification in try/catch → log. A failure in a trigger must never stop other triggers or the debug session (NF-5).
- **Testability:** time through `IClock`, the file system through an `IFileSystem` abstraction, logging through `ILogger`. No static state in Core.
- **Dependencies:** prefer what VS already ships (`Newtonsoft.Json`, `Microsoft.VisualStudio.Threading`, `Microsoft.VisualStudio.Debugger.Engine`). Don't bundle assemblies VS provides (`<Private>false</Private>`). Core references only netstandard2.0-compatible packages.
