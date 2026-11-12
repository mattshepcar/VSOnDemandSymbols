# Dimension 3 Analysis: Agent Stalling, Confusion, and Tool Loops

> **Scope:** Quantitative analysis of tool failures, sandbox hangs, COM/threading cascades, redundant file reads, compaction thrashing, and thinking-trace bloat across all **51 sessions** (**4,553 tool calls**, **915 errors**) in `chat_history/`.  
> **Goal:** Identify where the AI burned cycles, why it got stuck in loops, and what concrete guardrails prevent those failure modes in future projects.

---

## 1. Executive Summary

Across **4,553 total tool calls**, **915 ended in error (20.1% overall error rate)**. Tool failures were not evenly distributed—they clustered tightly around five structural friction points:

1. **Sandbox & Build Infrastructure Hangs (`S03`, `S04`, `S10`, `S46`):** MSBuild's `VBCSCompiler.exe` and worker node reuse (`/nr:true`) rely on named pipes that hang indefinitely inside the Windows sandbox. It took **until Session 10** (after 5 pilot *"has the build hung?"* interventions across `S03`, `S04`, and `S10`) to institutionalize `/p:UseSharedCompilation=false /nr:false` in [AGENTS.md](file:///C:/Dev/VSOnDemandSymbols/AGENTS.md) (`fb519e7`).
2. **Compaction-Induced Thrashing (`S02`, `S10`, `S21`, `S43`, `S46`):** Only **5 of 51 sessions** crossed the context compaction threshold, yet those 5 sessions accounted for **51.5% of total project spend (\$52.23 / \$98.88)** and **34.5% of all tool errors (316 / 915)**.
3. **Massive Redundant File Reads (`S10`, `S13`, `S21`):** In long sessions, the AI repeatedly re-read the exact same files—peaking at **37 reads of `SessionTracker.cs`** in `S13`, **36 reads of `SessionTracker.cs`** in `S10`, and **22 reads of `VSOnDemandSymbolsPackage.cs`** in `S21`.
4. **COM, Threading, and VS SDK Friction (`S10`, `S21`, `S43`, `S49`):** `VSTHRD` threading analyzer errors, `ThreadHelper.ThrowIfNotOnUIThread()` omissions, `COMException`s, and opaque Unified Settings schemas drove hundreds of edit-compile-fail cycles.
5. **Runaway Thinking Traces (`>10,000` chars):** Single thinking blocks ballooning past **10k–40k characters** (`S11`: 40,693 chars; `S10`: 28,150 chars; `S43`: 20,616 chars; `S51`: 14,338 chars) consistently correlated with over-engineering or circular debugging rather than higher code quality.

---

## 2. Quantitative Overview: Tool Calls & Error Hotspots

### 2.1 Overall Tool Usage Distribution (All 51 Sessions)

| Tool | Total Calls | Share of Calls | Primary Role |
|---|---:|---:|---|
| `run_command` | 2,054 | 45.1% | MSBuild builds, `dotnet` test runs, `git`, inspection scripts |
| `read_file` (+ `view_file`) | 1,235 | 27.1% | Reading source, docs, and build logs |
| `edit_file` | 659 | 14.5% | Incremental code/doc patches |
| `write_file` | 338 | 7.4% | Creating new files, reports, and scratch scripts |
| `search_web` | 168 | 3.7% | VS SDK / Unified Settings / AD7 API lookups |
| `manage_task` | 72 | 1.6% | Checking/killing hung background builds |
| Other (`invoke_subagent`, `send_message`, `ask_question`, etc.) | 27 | 0.6% | Subagent coordination and pilot clarification |
| **Total** | **4,553** | **100.0%** | **915 tool errors (20.1% failure rate)** |

### 2.2 Top Error-Generating Sessions

| Session | Model | Tool Errors / Calls | Error Rate | Compactions | Cost | Primary Loop / Failure Cause |
|---|---|---:|---:|---:|---:|---|
| `S10` | Argon High, Flash Med, Opus Med | 100 / 559 | 17.9% | 2 | \$30.93 | Concord `.vsdconfig` COM plumbing + MSBuild sandbox hangs |
| `S21` | Flash High | 96 / 456 | 21.1% | 2 | \$1.77 | Break-mode symbol loading + Unified Settings (`registration.json`) |
| `S02` | Argon Med | 49 / 222 | 22.1% | 1 | \$8.01 | Phase 0 NuGet/VSIX SDK discovery & Concord spike |
| `S43` | Argon Med/High, Flash Med | 38 / 151 | 25.2% | 1 | \$4.04 | Broad whole-repo refactor hitting `VSTHRD` / build regressions |
| `S13` | Flash High | 37 / 167 | 22.2% | 0 | \$0.67 | Removing MRU/pinning across 12 files (`37` reads of `SessionTracker.cs`) |
| `S19` | Flash High | 37 / 124 | 29.8% | 0 | \$0.44 | AD7 breakpoint error event wiring & mid-session bind fixes |
| `S37` | Flash High | 33 / 143 | 23.1% | 0 | \$0.44 | Adding PDB skip-count stats into `DebuggerSessionMonitor` |
| `S46` | Argon Med | 33 / 188 | 17.6% | 1 | \$7.48 | Out-of-proc `devenv.exe` Lyra profiling & sandbox boundary retries |
| `S39` | Flash High | 32 / 163 | 19.6% | 0 | \$0.57 | Debugging stats not updating during Lyra launch |
| `S51` | Flash Med, Opus Med, Hydrogen Med | 31 / 134 | 23.1% | 0 | \$17.64 | Multi-instance `HiveLock` + `IVsDebugLaunchHook` (`Index.lock` / build retries) |
| `S08` | Flash Med (Subagent) | 29 / 39 | **74.4%** | 0 | \$0.04 | Probing missing `D:\UnrealEngine\Engine\Binaries\Win64` paths |

---

## 3. Five Core Failure Loops & Stalling Patterns

### 3.1 Compilation & Sandbox Failure Loops (`VBCSCompiler` & `/nr:false`)

- **The Mechanism:** Standard `msbuild.exe` invocations spawn persistent `VBCSCompiler.exe` compiler servers and reuse worker nodes (`/nr:true`). Inside the Windows sandbox, those named pipes block indefinitely without throwing an error—causing the command to silently hang until the pilot notices and asks *"has the build got stuck?"*.
- **Cost & Delay to Institutionalize:**
  - Hung first in **Session 03** (`1` pilot intervention) and **Session 04** (`1` pilot intervention).
  - Hung **3 more times** in **Session 10** before the AI diagnosed `VBCSCompiler` / node reuse and documented `/p:UseSharedCompilation=false /nr:false` in [AGENTS.md](file:///C:/Dev/VSOnDemandSymbols/AGENTS.md) (commit `fb519e7`).
  - Once codified in `AGENTS.md`, sandbox build hangs dropped to **zero** for normal MSBuild builds across Sessions `11–44`. (Later `stuck` prompts in `S45–S46` were out-of-proc `devenv.exe` launches requiring `BypassSandbox: true`.)

### 3.2 COM, UI Threading (`VSTHRD`), and VS SDK Friction

- Visual Studio VSIX development enforces strict UI-thread rules (`Microsoft.VisualStudio.Threading` analyzers) and COM marshaling (`EnvDTE`, `IVsDebugger`, AD7 `IDebugEngine2`).
- Across the transcripts, threading/COM keywords (`RPC_E_WRONG_THREAD`, `VSTHRD*`, `ThrowIfNotOnUIThread`, `SwitchToMainThreadAsync`) spiked heavily in:
  - **`S10`** (186 mentions), **`S43`** (119 mentions), **`S21`** (106 mentions), **`S45`** (97 mentions), and **`S46`** (69 mentions).
- **Root Cause:** Without reading the exact `Microsoft.VisualStudio.SDK` 17.14 interface signatures or Unified Settings schema first, the AI guessed method signatures (`edit_file` -> `msbuild` fails with `CS`/`VSTHRD` -> `edit_file` -> repeat), generating **40+ captured Roslyn/VSTHRD build-error loops** and driving `search_web` up to **168 calls** (heavily concentrated in `S21` when migrating to `registration.json`).

### 3.3 Redundant File Reads

When context windows grew crowded—or when the AI jumped into editing without building a mental map first—it repeatedly re-read the exact same files in the same session:

| Session | Total `read_file` Calls | Unique Files Read | Most Re-Read File (Count) | Files Read 3+ Times |
|---|---:|---:|---|---:|
| `S13` | 79 | 20 | `SessionTracker.cs` (**37×**) | 6 |
| `S10` | 168 | 69 | `SessionTracker.cs` (**36×**) | 13 |
| `S21` | 92 | 21 | `VSOnDemandSymbolsPackage.cs` (**22×**) | 10 |
| `S37` | 36 | 16 | `DebuggerSessionMonitor.cs` (**11×**) | 4 |
| `S49` | 23 | 11 | `DebuggerSessionMonitor.cs` (**9×**) | 1 |
| `S04` | 28 | 13 | `MODULE_RESOLUTION.md` (**8×**) | 3 |
| `S15` | 45 | 24 | `VSOnDemandSymbolsPackage.cs` (**7×**) | 4 |

Reading `SessionTracker.cs` **36–37 times** in a single session is a textbook symptom of **narrow line-range thrashing** paired with **context compaction** (losing track of fields/methods edited earlier in the same session).

### 3.4 Compaction-Induced Thrashing

Only **5 sessions** hit context compaction (`compactions_count >= 1`):

| Session | Compactions | Total Cost | Tool Errors / Calls (Rate) | Post-Compaction Symptom |
|---|---:|---:|---:|---|
| `S02` | 1 | \$8.01 | 49 / 222 (22.1%) | Lost track of earlier Phase 0 NuGet probe outcomes |
| `S10` | 2 | \$30.93 | 100 / 559 (17.9%) | Re-read `SessionTracker.cs` 36×; re-attempted failing Concord `.vsdconfig` patterns |
| `S21` | 2 | \$1.77 | 96 / 456 (21.1%) | Burned dozens of `search_web` & build cycles on `UnifiedSettings` (`registration.json`) |
| `S43` | 1 | \$4.04 | 38 / 151 (25.2%) | High error rate during sweeping multi-file refactor |
| `S46` | 1 | \$7.48 | 33 / 188 (17.6%) | Repeatedly probed `VisualStudioExpFixture.cs` & Lyra process hangs |

**Takeaway:** Crossing even **1 compaction** signals that a session has outlived its optimal context budget. Splitting work at phase boundaries keeps error rates and costs dramatically lower.

### 3.5 Thinking-Trace Bloat vs. Output Value

Extracting `<details>...Thought Process...</details>` blocks from `conversation.md` reveals a clear inverse relationship between **runaway thinking length** and **execution efficiency**:

| Session | Thinking Blocks | Total Thinking Chars | Max Single Block | Avg Block | Tool Errors | Cost |
|---|---:|---:|---:|---:|---:|---:|
| `S10` | 218 | 228,631 | 28,150 | 1,048 | 100 | \$30.93 |
| `S21` | 127 | 161,601 | 11,783 | 1,272 | 96 | \$1.77 |
| `S51` | 46 | 79,359 | 14,338 | 1,725 | 31 | \$17.64 |
| `S43` | 37 | 77,183 | 20,616 | 2,086 | 38 | \$4.04 |
| `S11` | 31 | 68,782 | **40,693** | 2,218 | 24 | \$2.52 |
| `S19` | 40 | 59,362 | 12,503 | 1,484 | 37 | \$0.44 |
| `S01` | 20 | 43,291 | 19,180 | 2,164 | 8 | \$11.79 |

In contrast, clean, surgical sessions (e.g., `S27` avg **594 chars**, `S14` avg **900 chars**) resolved tasks with far less circular reasoning. Single thinking traces above **10,000 characters** almost exclusively occurred when the AI was **inventing speculative workarounds** (`S11` MSBuild crawling, `S51` over-engineered lock manager) or **fighting undocumented VS SDK edges** (`S10`, `S21`).

### 3.6 Fresh Session Splits vs. Continuing Existing Sessions

Grouping the **44 interactive main sessions** by turn count shows that the pilot's habit of starting fresh sessions for discrete tasks was **overwhelmingly effective** compared to continuing long sessions:

| Session Length Bucket | Sessions | Total Cost | Avg Cost | Tool Errors / Calls (Rate) | Compactions |
|---|---:|---:|---:|---:|---:|
| **Short (`<= 40` turns)** | 14 | \$13.36 | \$0.95* | 55 / 282 (19.5%) | **0** |
| **Medium (`41–120` turns)** | 18 | \$30.31 | \$1.68 | 289 / 1,383 (20.9%) | 1 |
| **Long (`121–200` turns)** | 10 | \$22.16 | \$2.22 | 294 / 1,642 (17.9%) | 2 |
| **Mega (`> 200` turns: `S10`, `S21`)** | 2 | **\$32.70** | **\$16.35** | 196 / 1,015 (19.3%) | **4** |

*\*Excluding `S01` (Claude Opus 5.5 initial spec at \$11.79), the other 13 short sessions averaged **\$0.12** each.*

- **Why fresh sessions won (`S14` → `S15`, `S28–S36`):** When the pilot explicitly cut a clean boundary—such as having `S14` (\$0.34) prepare branch `v2-minimal` and handing a fresh prompt to `S15` (\$0.51)—both sessions stayed compaction-free and rebuilt the minimal V2 architecture for **\$0.85 total**. Similarly, isolating single-concern tasks (`S17`, `S18`, `S22`, `S28`, `S31`, `S32`, `S36`, `S44`) in fresh sessions kept cost at **\$0.01–\$0.14** with zero context pollution.
- **When continuing hurt:** Pushing `S10` (470 turns) and `S21` (475 turns) past 200+ turns triggered **4 of the project's 7 total compactions**, caused 36–37× redundant reads of the same files, and concentrated **33.1% of total project spend (\$32.70)** into just 2 sessions. Moreover, when the pilot escalated to **Claude Opus 5.5** mid-session inside an already-bloated context (`S10`: 24 Opus turns = **\$20.84**; `S51`: 18 Opus turns = **\$17.02**), Opus paid premium per-token rates on the entire accumulated conversation history. Starting a **fresh Opus/Argon session** with a 1-page summary would have saved the majority of that context-replay cost.

---

## 4. Concrete Guardrails to Prevent Tool Loops

1. **Codify environment/sandbox workarounds in `AGENTS.md` on first occurrence.** Documenting `/p:UseSharedCompilation=false /nr:false` in `AGENTS.md` immediately stopped MSBuild hangs for the rest of the project.
2. **Hard-stop before Context Compaction.** If a session approaches compaction (or >100 tool calls), **checkpoint to Git and start a fresh session** with a clean handoff prompt instead of pushing through 2 compactions like `S10` (\$30.93) and `S21` (456 tool calls).
3. **Cap identical-file reads and compile-fail retries.** If the same file is read >4 times or a build fails 3 times on the same SDK/COM error, stop guessing and either read the official SDK metadata/docs or ask the pilot.
4. **Never let `S08`-style path probing run blind.** Before probing external directories (`D:\UnrealEngine\Engine\Binaries\Win64`), run one `Test-Path` / `Get-ChildItem` check instead of 29 failing individual file probes.
5. **Treat 10k+ char thinking traces as a "stop and clarify" trigger.** Long internal monologues indicate ambiguity or over-engineering—pause and propose options to the pilot before writing code.

