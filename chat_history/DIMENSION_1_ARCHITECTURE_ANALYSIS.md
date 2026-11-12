# Dimension 1 Analysis: Architectural Decisions, Pivots, and Dead Ends

> **Scope:** Forensic trace of architectural evolution, pivots, dead ends, and code churn across all **71 Git commits** and **51 extracted sessions** (`chat_history/`), cross-referenced against [docs/DECISIONS.md](file:///C:/Dev/VSOnDemandSymbols/docs/DECISIONS.md) and [docs/ARCHITECTURE.md](file:///C:/Dev/VSOnDemandSymbols/docs/ARCHITECTURE.md).  
> **Goal:** Quantify the token, dollar, and code-churn cost of speculative architecture vs. empirical simplicity, and establish architectural guardrails for future AI-assisted projects.

---

## 1. Executive Summary

The **VSOnDemandSymbols** repository tells a striking story of **over-engineering followed by aggressive, pilot-driven simplification**:

- **58.9% of all code ever written was deleted:** Across **71 Git commits**, the project recorded **26,251 insertions** and **15,470 deletions**, settling at **10,781 net lines**.
- **Over half the project's dollar cost went into dead ends or architectural reversals:**
  - **Concord Debug Engine + MSBuild/MRU V1 stack (`S02`, `S10`, `S11`):** **$41.46** (41.9% of the **$98.88** total project cost) was spent building and debugging a complex V1 architecture that was completely deleted in **Session 15** (`57e200a`: **+1,258 / -7,255 lines** across 59 files).
  - **Multi-Instance `HiveCoordinator` Phase 1 overshoot (`S51`):** **$17.64** in a single session—mostly burned before the pilot challenged the complexity (*"can you just check that this session manager stuff is the optimal way..."*), triggering a **+605 / -925 line** simplification (`06c4fa9`) to kernel file-handle locks (`HiveLock`).
- **The winning architecture was radically simpler than the initial spec:**
  - Every major breakthrough came from replacing custom in-process interception (Concord, MSBuild crawling, MRU caches, custom menus) with **native Visual Studio primitives** (`IncludeList`, `IVsDebugLaunchHook`, AD7 `EnumFrameInfo(FIF_MODULE)`, Unified Settings `registration.json`, and Win32 kernel file locks).
- **The pilot was the primary architectural simplifier:** Almost every pruning milestone (`S03` RecentlyBuilt, `S13` MRU/Pinning, `S14–S15` V2 Clean-Slate, `S18` Debug Menu, `S21` Unified Settings, `S28` `Polyfills.cs`, `S43` Dead Code, `S51` Option A) was initiated when the pilot questioned whether a mechanism was actually needed.

---

## 2. Quantitative Impact of Architectural Dead Ends & Churn

### 2.1 Cost of Major Architectural Phases & Reversals

| Architectural Phase / Pivot | Sessions | Primary Models | Turns | Tool Calls (Errors) | Total Cost | Share of $98.88 | Fate |
|---|---|---|---|---|---|---|---|
| **Spec & Initial V1 Design** | `S01` | Claude Opus 5.5 (High) | 22 | 31 (8) | **$11.79** | 11.9% | Initial spec included Concord, MSBuild, RecentlyBuilt, MRU |
| **Phase 0–1 + Concord Pivot (D-15)** | `S02–S03` | Argon Med, Flash Med | 372 | 385 (56) | **$10.96** | 11.1% | Switched to Concord Hybrid; `RecentlyBuilt` cut in `S03` |
| **Phase 2: UBT Ground-Truth Subagents** | `S04–S09` | Flash Med | 328 | 305 (83) | **$0.58** | 0.6% | **High-ROI:** Confirmed UE 5.8.4 UBT rules directly from `D:\UnrealEngine` |
| **Phase 3–4: V1 Concord + MSBuild + MRU** | `S10–S11` | Argon High/Med, Flash Med, Opus Med | 517 | 662 (124) | **$33.45** | 33.8% | **Dead End:** Deleted in `S15` V2 Clean-Slate (`-7,255` lines) |
| **V2 Clean-Slate Purge & Docs Cleanup** | `S13–S15, S18` | Flash High | 487 | 477 (94) | **$1.63** | 1.6% | **High-ROI:** Eliminated Concord, MSBuild, MRU, and menu UI |
| **Break-Mode, Masking & Unified Settings** | `S21` | Flash High | 475 | 456 (96) | **$1.77** | 1.8% | Implemented `ADR-02`, `ADR-05`, `ADR-06`, `ADR-13` |
| **Post-V2 Pruning & Hardening** | `S27–S28, S35, S43` | Flash High, Argon Med/High | 431 | 461 (96) | **$5.10** | 5.2% | Pruned `NativeSample`, dead tests, regex allocations, `OutputLog` cruft |
| **Out-of-Proc Integration & Lyra Profiling** | `S45–S47` | Argon Med | 228 | 302 (48) | **$10.10** | 10.2% | Built `MiniUnreal` + `--profile-lyra` (`ADR-12`, `ADR-14`) |
| **AD7 `FIF_MODULE` Stack-Walk Optimization** | `S49` | Argon Med | 53 | 50 (18) | **$2.16** | 2.2% | Cut break-mode stack walk from **401 ms** down (`ADR-02`) |
| **Multi-Instance Coordination (`HiveLock`)** | `S51` | Flash Med, Opus Med, Hydrogen Med | 120 | 134 (31) | **$17.64** | 17.8% | Overbuilt Phase 1, then simplified (`-925` lines) to `ADR-15`/`ADR-16` |

### 2.2 Top Deletion-Heavy & High-Churn Commits

| Commit | Insertions | Deletions | Files | Summary |
|---|---|---|---|---|
| `57e200a` | +1,258 | **-7,255** | 59 | `feat(v2): adopt V2 clean-slate minimal architecture on branch v2-minimal` |
| `d85cf65` | +490 | **-1,562** | 19 | `docs: clean up documentation and remove obsolete V1 cruft` |
| `38ace44` | +34 | **-1,080** | 12 | `refactor: remove MRU, pinning, and per-solution state.json per D-20` |
| `06c4fa9` | +605 | **-925** | 16 | `refactor(core): simplify multi-instance coordination to Option A with kernel file-handle locks` |
| `fc75578` | +91 | **-879** | 18 | `refactor(docs): prune obsolete spikes and trackers, consolidate decisions into clean ADRs` |
| `dd20609` | +444 | **-502** | 12 | `Revise spec, architecture, and Phase 0 spikes to use Concord Hybrid (D-15)` |
| `29b2f5a` | +23 | **-362** | 16 | `Remove RecentlyBuilt trigger and simplify load policy (D-16)` |
| `81a5ae6` | +273 | **-277** | 6 | `refactor(tests): simplify integration and Lyra profiling suites and make tests self-contained` |
| `91ce59f` | +2 | **-214** | 10 | `chore: remove unused NativeSample sample and update manual verification guide` |
| `5b4d076` | +21 | **-189** | 9 | `refactor: simplify OutputLog, BreakSymbolLoadMode, IFileSystem, and remove dead code` |
| `cb584e4` | +34 | **-166** | 10 | `Remove Debug > On-Demand Symbols > Enabled menu command (D-23)` |

---

## 3. Forensic Trace of the 5 Major Architectural Pivots

### Pivot 1: Concord Debug Engine (`DkmModuleInstance.TryLoadSymbols`) → Native VS `IncludeList` (`ADR-01`)

1. **Origin (`S02`, $8.01):** During Phase 0, the AI initially leaned away from Concord, prompting the pilot to ask: *"why the reluctance to use Concord? is the approach you're suggesting using undocumented APIs over a more microsoft documented approved approach?"* After weighing pros/cons, the pilot approved the **Concord Hybrid** architecture (`D-15`, commit `dd20609`, **+444 / -502**).
2. **Implementation & Failure (`S10`, $30.93 — 470 turns, 559 tool calls, 100 errors):**
   - Implementing Concord (`88210d7`: **+3,360 / -50 lines**) introduced massive friction: `.vsdconfig` XML registration, native debug engine COM plumbing, NUL-padded `DkmProcess.Path` quirks (`2004daa`), and a critical bug where intercepting `IDkmBreakpointManagerNotification` prevented Visual Studio's breakpoint glyphs from updating (`5a1be85`).
   - Worse, Concord's module-load callbacks ran **in the hot path** of ~1,000 UE5 DLL mappings and still required switching back to DTE `IncludeList` and `Module.LoadSymbols("")` for reliable symbol loading (`c51558a`).
3. **Resolution (`S14–S15`, $0.85 total):** In `S14`, the pilot asked whether to refactor or start clean from scratch on a new branch (`v2-minimal`). Commit `57e200a` (**+1,258 / -7,255 lines**) deleted the entire Concord engine component, `.vsdconfig`, and `ConcordSymbolMonitor`, replacing them with native VS `IncludeList` (`ADR-01`), which runs in VS's C++ debug engine at **zero managed overhead** on module map.

> **Lesson:** An "officially documented" extensibility layer (Concord) is the wrong abstraction if it forces you onto the hot path of 1,000+ events when a native declarative setting (`IncludeList`) already exists inside the host process.

---

### Pivot 2: MSBuild Project Crawling & Generic C++ → UE5 `*.Build.cs` Walk-Up & Dedicated Scope (`ADR-07`, `ADR-09`)

1. **Initial Assumption (`S01`, `S11`):** The original spec targeted generic C++ solutions and crawled MSBuild/VCProject (`VCConfiguration`, `VCLinkerTool`) outputs in Phase 4 (`0eb7aed`: **+3,217 / -107 lines**, **$2.52** in `S11`).
2. **Ground-Truth Verification (`S04–S09`, $0.58 total):** Delegating UnrealBuildTool inspection to 5 focused Flash subagents against `D:\UnrealEngine` (UE 5.8.4) proved that Unreal solutions do **not** expose their runtime DLL names reliably through VC++ project linker properties—UE5 uses UBT `*.modules` manifests, `UnrealEditor-{ModuleName}.dll` naming, and `*.Build.cs` folder boundaries.
3. **Resolution (`S15`, `S16`, `S43`):**
   - Dropped generic C++ support (`ADR-09`) and MSBuild crawling entirely in `v2-minimal` (`57e200a`).
   - Adopted wildcard `IncludeList` patterns (`UnrealEditor-{ModuleName}*.dll`, `ADR-07`, `ea303f6`) so hot-reload/patch DLLs match automatically without enumerating files on disk.
   - Later optimized `Build.cs` walk-up with leaf-directory caching and directory-name match preference (`6587668`).

> **Lesson:** Spending **$0.58** on read-only subagents to verify ground-truth rules against `D:\UnrealEngine` saved weeks of guesswork. Conversely, building generic MSBuild support before verifying the primary target (UE5) created 3,000+ lines of throwaway code.

---

### Pivot 3: Speculative Features (`RecentlyBuilt`, MRU/Pinning, Menu Toggle) → Zero-State Transparent Architecture (`ADR-08`, `ADR-10`)

The original brief (`S01`) included three speculative features that sounded helpful on paper but collapsed under real-world scrutiny:

1. **`RecentlyBuiltScanner` (`S03`):**
   - *Pilot challenge:* *"explain the rationale behind RecentlyBuiltScanner... perhaps the DLL being recently modified is not a good indicator..."*
   - *Why it died (`D-16`, `29b2f5a`, **-362 lines**):* A header edit or engine sync triggers a mass rebuild of hundreds of DLLs, which would defeat on-demand loading by preloading everything.
2. **MRU & Module Pinning + `state.json` (`S13`):**
   - *Pilot challenge:* *"we don't need to be able to pin specific modules to always load. we don't need the most recently used modules to always load, the whole MRU feature can be removed."*
   - *Why it died (`D-20`, `38ace44`, **-1,080 lines**):* Breakpoints + break-mode call-stack loading already covered 100% of active debugging needs without stale state files.
3. **`Debug > On-Demand Symbols > Enabled` Menu Command (`S18`):**
   - *Pilot challenge:* *"The 'Debug|On-Demand Symbols|Enabled' menu option provides little to no utility above just simply disabling or uninstalling the extension."*
   - *Why it died (`D-23`, `cb584e4`, **-166 lines**):* Removed VSCT command tables, magic `Std97CmdSetGuid` constants, and toggle state, establishing `ADR-10` (zero custom menu UI).

> **Lesson:** Every persistent state store (`state.json`), background filesystem scanner, and custom menu entry adds failure modes (stale state, UI clutter, command-table GUIDs). Always ask: *"Does the core loop already solve this without state?"*

---

### Pivot 4: Break-Mode DTE Stack Walk → Local PDB Guard, Server Masking, & AD7 `FIF_MODULE` (`ADR-02`, `ADR-05`, `ADR-13`)

1. **The Break-Mode Freeze (`S21`, $1.77 — 475 turns, 456 tool calls):**
   - When breaking into UE5, the Threads and Call Stack windows showed unresolved frames. Calling `Module.LoadSymbols("")` naively triggered two severe UX failures:
     1. Downloading huge PDBs from remote symbol servers mid-break (solved via scoped symbol server masking during break loads, `ADR-05`, `ae097ac`).
     2. Triggering Visual Studio's modal Win32 *"Browse for PDB"* dialog when a local PDB was missing—solved later by checking local PDB existence on disk before calling `LoadSymbols("")` (`ADR-13`, `14633aa`).
   - Also in `S21`, the pilot pushed back on legacy Options pages, forcing migration to VS Unified Settings (`registration.json`, `ADR-06`, `56b752f`, `2f1a3d6`).
2. **The 401 ms DTE Stack-Walk Bottleneck (`S49`, $2.16):**
   - Profiling in Lyra showed `BreakMode: total=470ms (stackWalk=401ms [threads=121, frames=479], moduleScan=2ms [modules=1263], symbolLoad=30ms [loaded=1/1], contextRefresh=36ms)`.
   - *Pilot steer:* *"let's try #2, using AD7 instead of DTE first and benchmark it. no point overcomplicating things with a 2 phase resolution if the AD7 fix makes it fast enough."*
   - Switching to AD7 `IEnumDebugFrameInfo2` with `FIF_MODULE` (`07b1a2a`, `ADR-02`) avoided DTE COM marshaling per frame/thread and skipped redundant context refreshes.

---

### Pivot 5: Out-of-Proc Testing (`ADR-12`, `ADR-14`) & Multi-Instance Coordination (`ADR-15`, `ADR-16`)

1. **Automated VSIX Testing (`S45–S47`, $10.10):**
   - Rather than relying on manual F5 Experimental-instance checks, the pilot asked (`S45`) if an agent could drive `devenv.exe /rootsuffix Exp` directly. The result was an out-of-proc ROT/DTE test harness with a synthetic `MiniUnreal` fixture (`b5d02ad`, **+1,503 lines**) and a zero-write headless `--profile-lyra` benchmark (`14633aa`, **+878 lines**) measuring Cold vs. Warm UE5 Lyra launches against ~1,062 DLLs, later simplified in `S47` (`81a5ae6`: **+273 / -277 lines**).
2. **Multi-Instance Race Condition (`S51`, $17.64):**
   - Opening a second VS instance while a first instance was debugging corrupted the user's baseline symbol settings backup.
   - **Mid-session pivot:** After docs (`be064e6`) and Phase 1 (`460bbb8`: **+1,347 lines**) were written, the pilot intervened: *"before we go further, can you just check that this session manager stuff is the optimal way to achieve our multi-instance safety. are there any simplifications we could make..."*
   - The AI pivoted to **Option A** (`06c4fa9`: **+605 / -925 lines**): just-in-time settings application via `IVsDebugLaunchHook` (`ADR-15`, superseding D-18 solution-open writes) paired with `HiveCoordinator` + Win32 kernel file-handle locks (`HiveLock`, `ADR-16`, `6b0d4ee`), which automatically release on process crash.

---

## 4. Architectural Rules for Future AI-Built Projects

1. **Verify host/engine ground truth before designing abstractions.** Run cheap, read-only subagent investigations (`S04–S09`, **$0.58**) on target SDKs/engines before writing Phase 1 specs. Never assume MSBuild or IDE extensibility layers expose what you need.
2. **Prefer host-native declarative mechanisms over in-process hooks.** `IncludeList` + `IVsDebugLaunchHook` beat Concord `.vsdconfig` interception on performance, reliability, and LOC by an order of magnitude.
3. **Challenge every stateful or UI-bearing requirement at spec time.** `RecentlyBuiltScanner`, MRU/pinning, `state.json`, and custom menu commands accounted for **>8,500 lines of later deletions**. Start with the stateless core loop and add state only when proven necessary.
4. **Enforce a "Design Review Gate" before multi-file implementation.** Both `S21` and `S51` showed that when an AI writes >1,000 lines before the pilot reviews the class count, it over-engineers. Require a short component/LOC budget in the plan first.
5. **Treat deletion commits as first-class milestones.** Dedicated rationalization passes (`S14–S15`, `S35`, `S43`, `S47`) kept the codebase at **10.8k net LOC** despite **26.2k insertions**.
6. **Build out-of-proc empirical benchmarks early for IDE/performance tools.** `MiniUnreal` + `--profile-lyra` (`ADR-12`, `ADR-14`) and `[Profile]` telemetry (`S49`) turned vague symptom reports into measurable engineering wins.
