# Dimension 2 Analysis: Prompting Patterns — High-Yield vs. Failure-Inducing

> **Scope:** Forensic analysis of all **202 pilot prompts** across the **44 interactive main sessions** in `chat_history/` (excluding the 7 autonomous subagent sessions).  
> **Goal:** Identify which prompt structures, constraints, and steering interventions produced clean, low-cost, accurate results versus those that triggered tool-error loops, over-engineering, and token waste.

---

## 1. Executive Summary

Across 44 interactive sessions, **how the pilot framed the opening prompt of a session was the single strongest predictor of cost, error rate, and architectural cleanliness**:

- **Single-focus prompts** averaged **84.7 tool calls**, **16.7 errors**, and **$1.64** per session.
- **Multi-part bundled prompts** (`1. ... 2. ... 3. ...`) averaged **203.6 tool calls** (**2.4× higher**), **36.8 errors** (**2.2× higher**), and **$6.93** (**4.2× higher**).
- **Surgical single-concern edits** (e.g., removing a dead field, renaming a class, fixing a Mermaid diagram) completed in **2.5 prompts on average**, **0 compactions**, and **$0.08 average cost**.
- **Design-first guardrails** (*"without implementing yet"*, *"before you even start implementing, update the docs"*, *"no point overcomplicating..."*) consistently prevented the AI from writing hundreds of lines of throwaway code on Turn 1—especially on the project's hardest problems (break-mode symbols, integration testing, and multi-instance VS locking).

---

## 2. Quantitative Breakdown by Prompt Archetype

Every main session was classified by its opening prompt archetype and correlated against execution metrics from `summary_metrics.json`:

| Prompt Archetype | Sessions | Avg Prompts | Avg Tool Calls | Avg Tool Errors (Rate) | Compactions | Avg Cost |
|---|---|---|---|---|---|---|
| **1. Triage / Design-First (Ask Before Coding)** | 5 (`S21, S24, S33, S45, S46`) | 9.6 | 156.2 | 29.8 (20.4%) | 3 | **$2.22** |
| **2. Bounded Spec / Phase Execution** | 4 (`S03, S04, S10, S11`) | 8.5 | 226.2 | 33.5 (12.3%) | 2 | **$9.16** |
| **3. Codebase Review & Rationalization** | 7 (`S13, S14, S16, S27, S35, S43, S47`) | 4.9 | 114.4 | 24.4 (20.9%) | 1 | **$1.03** |
| **4. Surgical Single-Concern Edit** | 8 (`S17, S18, S22, S31, S32, S36, S41, S44`) | **2.5** | **31.1** | **5.4 (15.0%)** | **0** | **$0.08** |
| **5a. Diagnostic Bug Report (With Logs/Telemetry)** | 1 (`S49`) | 5.0 | 50.0 | 18.0 (36.0%) | 0 | **$2.16** |
| **5b. Open-Ended Symptom Report (No Logs)** | 3 (`S23, S39, S40`) | 5.3 | 113.3 | 20.3 (19.9%) | 0 | **$0.34** |
| **6. Exploratory / Tooling / Meta** | 16 (`S01, S02, S12, S15, ...`) | 2.8 | 74.8 | 16.1 (25.3%) | 1 | **$2.49** |

### Single-Focus vs. Multi-Part Opening Prompts

| Prompt Structure | Sessions | Avg Prompts | Avg Tool Calls | Avg Tool Errors | Compactions | Avg Cost |
|---|---|---|---|---|---|---|
| **Single-Focus Opening Prompt** | 39 | 4.4 | 84.7 | 16.7 | 5 | **$1.64** |
| **Multi-Part Opening Prompt (`1. ... 2. ...`)** | 5 | 6.2 | 203.6 | 36.8 | 2 | **$6.93** |

> **Key Takeaway:** Bundling multiple objectives or multi-step implementation checklists into a single opening prompt caused more than double the tool thrashing and 4× the cost compared to focused, single-concern sessions.

---

## 3. High-Yield Prompting Patterns (What Worked)

### Pattern A: "Investigate & Propose Without Implementing Yet"
- **Where Used:** Session 21 (`P01`), Session 24 (`P01`), Session 45 (`P01–P02`), Session 51 (`P02–P03`).
- **Examples:**
  - *Session 21:* `"Suggest possible solutions for this without implementing them yet."`
  - *Session 24:* `"Brain storm some ideas and ask me before deciding on one."`
  - *Session 51:* `"yes, before you even start implementing this, update the docs to reflect the plan if you're sure it will work."`
- **Why It Worked:** Left unconstrained, the AI defaults to immediate code generation based on its first hypothesis. Forcing a design-only turn surfaces flawed assumptions (e.g., file-system scraping for PDBs in Session 21, or overly complex session-manager locks in Session 51) **before** 500 lines of C# are written and wired into tests.

### Pattern B: Surgical Single-Concern Prompts
- **Where Used:** Sessions 17, 18, 22, 31, 32, 36, 44.
- **Examples:**
  - *Session 17:* `"add the docs folder to the visual studio solution file"` (1 prompt, 12 tools, $0.01)
  - *Session 22:* `"get rid of the static member DebuggerSessionMonitor.BreakpointErrorEventGuid and just use..."` (4 prompts, 38 tools, $0.07)
  - *Session 31:* `"should we rename UnifiedSettings to just Settings? there's no other kind of non-unified settings"` (2 prompts, 27 tools, $0.03)
  - *Session 44:* `"fix spec.md Unable to render rich display Parse error on line 2..."` (2 prompts, 18 tools, $0.14)
- **Why It Worked:** Zero ambiguity, zero architectural sprawl, zero context compaction, and fast verification.

### Pattern C: Socratic / Question-First Code Review
- **Where Used:** Sessions 20, 28, 30, 42, 47.
- **Examples:**
  - *Session 28:* `"what is polyfills.cs and is it still needed?"` (2 prompts, 18 tools, $0.02 → cleanly deleted dead code)
  - *Session 42:* `"do we need to hard code these guids or can they be imported from somewhere?"` (1 prompt, 51 tools, $0.06)
  - *Session 47:* `"conduct a thorough code review of the new integration tests. can they be simplified? ask and answer relevant questions about the code."` (4 prompts, 39 tools, 4 errors)
- **Why It Worked:** Asking *"is X still needed?"* or *"ask and answer relevant questions about the code"* forces the AI to justify existing complexity rather than rubber-stamping it. In Session 47, this yielded an exceptionally clean review with only 4 tool errors.

### Pattern D: Telemetry-Backed Performance Prompts
- **Where Used:** Session 49 (`P01`).
- **Example:**
  - `"anything we can do to speed this up? [08:35:53.087] [Profile] BreakMode: total=470ms (stackWalk=401ms [threads=121, frames=479], moduleScan=..."`
- **Why It Worked:** Pasting exact profiler numbers eliminated guesswork. The agent immediately identified that `stackWalk=401ms` across 121 DTE thread objects was the bottleneck (85% of break latency) and proposed an AD7 replacement rather than optimizing irrelevant string operations.

### Pattern E: Explicit Simplicity Anchors & Negative Constraints
- **Where Used:** Sessions 48, 49, 51.
- **Examples:**
  - *Session 49:* `"let's try #2, using AD7 instead of DTE first and benchmark it. no point overcomplicating things with a 2 phase resolution if the AD7 fix makes..."`
  - *Session 48:* `"ok rewrite readme.md with ste80-humanizer but don't commit yet"`
- **Why It Worked:** AI assistants have a strong bias toward defensive over-engineering (e.g., two-phase async pipelines, extra abstraction layers, fallback registries). Explicitly instructing *"no point overcomplicating things if X is fast enough"* anchored the agent to the simplest testable solution.

---

## 4. Failure-Inducing & Expensive Prompting Patterns (What Backfired)

### Anti-Pattern A: Monolithic "Implement Phase X" Without Prior API Spikes
- **Where Used:** Session 10 (`P01`).
- **What Happened:** Prompt 1 instructed the agent to implement all of Phase 3 (`SymbolSettingsManager`, `ProcessMonitor`, `BreakpointMonitor`, Concord hooks, debugger events, Output logging) in one session. Because `DkmModuleInstance.TryLoadSymbols` had never been spike-tested against native C++ modules, the entire session built on a broken foundation and spiraled into **1,062 steps, 100 errors, 2 compactions, and $30.93**.
- **Contrast with Session 04:** Session 04 (`Phase 2`) succeeded cleanly in **4 prompts and $0.23** because `PHASE2_FLASH_DELEGATION_PLAN.md` delegated bounded, read-only UBT exploration to 5 isolated subagents first.

### Anti-Pattern B: Open-Ended Symptom Reports Without Logs
- **Where Used:** Session 39 (`P01`), Session 21 (`P01` before diagnostics were added).
- **What Happened:**
  - In Session 39 (`"the stats in the settings window don't seem to update when i launch the editor..."`), without an Output pane log or stack trace, the agent had to read multiple settings, serialization, and package lifecycle files, burning **163 tool calls and 32 tool errors** hunting for the root cause.
  - In Session 21, the initial prompt had no logs; only after the pilot added diagnostic logging in Prompt 5–7 (`CurrentProgram.Threads.Count=124`) did the root cause become obvious.

### Anti-Pattern C: Early Unquestioned Approval of Over-Engineered Plans
- **Where Used:** Session 01–03 (`RecentlyBuiltScanner`, `.modules` manifest watching, module-pinning UI).
- **What Happened:** In early sessions, the AI proposed ambitious features (scanning recently built DLLs, watching `.modules` manifests with `FileSystemWatcher`, complex `ModuleKey` reverse regexes, custom pinning). Because those designs weren't challenged upfront with *"do we actually need this for v1?"*, they were built and later had to be explicitly stripped out in **Session 13** (*"simplify the implementation plan and docs"*), **Session 14**, and **Session 43** (which hit a compaction cleaning up accrued complexity).

---

## 5. The Pilot's Most Effective Mid-Flight Steering Interventions

Reading across all 202 prompts, **the pilot's real-time interventions were the decisive quality gate** of the project. Four intervention types consistently rescued sessions from drifting:

1. **Deadlock / Hang Detection (`"has the build hung?"` / `"is it stuck?"`):**
   - Used in **Sessions 03, 04, 10, and 46**. Agents repeatedly waited passively on frozen MSBuild named pipes or hung `devenv.exe` processes until the pilot intervened.
2. **Challenging Unnecessary Complexity (`"why can't we just..."` / `"is there even any point..."`):**
   - *Session 03:* `"explain the rationale behind RecentlyBuiltScanner. why can't we just check if a module was recently built at the point when the debugger is..."` → Triggered major simplification.
   - *Session 10:* `"if we're using the IncludeList to set which symbols load on startup, is there even any point hooking OnModuleInstanceLoad?"` → Triggered the pivotal architecture shift off Concord.
   - *Session 51:* `"before we go further, can you just check that this session manager stuff is the optimal way to achieve our multi-instance safety."` → Caught over-engineering mid-flight.
3. **Forcing Documentation of Environment Traps:**
   - *Session 03:* `"update the agent rules to make it clear you should be committing to git after each stage"`
   - *Session 10:* `"can you make sure the agent docs are clear on how to build the solution without getting that hang problem again"`
   - *Result:* Once encoded into `AGENTS.md` (`/p:UseSharedCompilation=false /nr:false`), build hangs disappeared from all subsequent sessions.
4. **Fact-Checking Claims Against Ground Truth:**
   - *Session 29:* `"verify and correct (if necessary) the claim of 800 DLLs by looking at the lyra project"` → Replaced an unverified guess with exact empirical numbers (**1,168 DLLs**, **933 Unreal modules** mapped at startup).

---

## 6. Battle-Tested Prompt Templates for Future Projects

Derived directly from the highest-ROI sessions in this repository:

### Template 1: The Spike-First Design Prompt (For New Features / Unfamiliar APIs)
```text
We need to solve [PROBLEM]. 
Before writing any production code or updating docs:
1. Investigate how [API / System] actually behaves for our exact target ([e.g., native C++ / VS 2022]).
2. If any API behavior is unverified, propose a 10-line spike test to confirm it works.
3. Present 2–3 options ordered from simplest to most complex, with trade-offs.
Do NOT implement anything yet — wait for my selection.
```

### Template 2: The Bounded Implementation Prompt (Max 150-Step Scope)
```text
Follow AGENTS.md to implement ONLY [Specific Component / Sub-step] from docs/IMPLEMENTATION_PLAN.md.
Constraints:
- Keep the implementation as simple as possible; no speculative abstractions or future-proofing.
- If a build or test hangs for >60s, stop and diagnose immediately.
- If you hit an unexpected platform exception or 3 consecutive test failures, STOP and report rather than adding workarounds.
Run the relevant unit tests and commit when green.
```

### Template 3: The Telemetry-First Bug Prompt
```text
Bug: [1-sentence symptom].
Here is the exact log / profiler / stack trace from reproducing it:
[PASTE LOGS / TIMINGS]

First, explain the root cause and propose the simplest fix without overcomplicating it. Wait for my go-ahead before editing files (or fix directly if trivial).
```

### Template 4: The Socratic Code Review & Purge Prompt
```text
Conduct a thorough review of [Target Files / Subsystem].
Ask and answer critical questions about the code:
1. Are any classes, abstractions, or fallbacks unused or redundant?
2. Can any multi-step flow be simplified without losing correctness?
3. Do all comments and docs match the actual implementation?
Present your findings and simplification plan before deleting or refactoring anything.
```

