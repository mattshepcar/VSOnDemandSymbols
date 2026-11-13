# The Antigravity Engineering Playbook

> **Synthesized From:** 51 Antigravity sessions (**4,386 turns**, **4,553 tool calls**, **449.58M tokens**, **\$98.88** total spend) and **71 Git commits** (**+26,251 / -15,470** lines → **10,781** net LOC) building **VSOnDemandSymbols**.  
> **Detailed Reports:** [Dimension 1: Architecture](file:///C:/Dev/VSOnDemandSymbols/chat_history/DIMENSION_1_ARCHITECTURE_ANALYSIS.md) · [Dimension 2: Prompting](file:///C:/Dev/VSOnDemandSymbols/chat_history/DIMENSION_2_PROMPT_ANALYSIS.md) · [Dimension 3: Tool Loops](file:///C:/Dev/VSOnDemandSymbols/chat_history/DIMENSION_3_TOOL_LOOPS_ANALYSIS.md) · [Dimension 4: Token Economics](file:///C:/Dev/VSOnDemandSymbols/chat_history/DIMENSION_4_TOKEN_ECONOMICS_ANALYSIS.md) · [Dimension 5: Subagents](file:///C:/Dev/VSOnDemandSymbols/chat_history/DIMENSION_5_SUBAGENT_ANALYSIS.md) · [Dimension 6: Verification](file:///C:/Dev/VSOnDemandSymbols/chat_history/DIMENSION_6_VERIFICATION_ANALYSIS.md) · [Dimension 7: Knowledge Discovery](file:///C:/Dev/VSOnDemandSymbols/chat_history/DIMENSION_7_KNOWLEDGE_DISCOVERY_ANALYSIS.md) · [Session 10 Deep Dive](file:///C:/Dev/VSOnDemandSymbols/chat_history/SESSION_10_DEEP_DIVE.md)

---

## 1. Model Selection & Routing Guide

| Task Type | Recommended Model | Session Budget | Evidence from VSOnDemandSymbols |
|---|---|---|---|
| **Initial Spec & Architecture** | **Claude Opus 5.5 (High)** or **Gemini 4 Argon (High)** | Fresh session (`<= 25` turns) | `S01` Opus High produced the full spec in 22 turns (\$11.79); `S43` Argon High reviewed the repo in 11 turns (\$0.59). |
| **Read-Only Codebase / SDK Research** | **Gemini 3.8 Flash (Medium/High)** subagents | Parallel subagents (`15–80` turns each) | `S05–S09` explored `D:\UnrealEngine` UBT rules for **\$0.35 total**, keeping `S04` clean (**\$0.23**, 3.8% errors). |
| **Bounded Implementation & Bug Fixes** | **Gemini 3.8 Flash (High)** | Fresh session (`<= 120` turns) | Flash executed **80.1% of all turns** for **\$10.23**; built V2 Clean-Slate (`S15`, \$0.51) & Break-Mode/Settings (`S21`, \$1.77). |
| **Complex Perf / Integration Harness** | **Gemini 4 Argon (Medium)** | Scoped session (`<= 100` turns) | Built `MiniUnreal` (`S45`, \$1.67), `--profile-lyra` (`S46`, \$7.48), and AD7 `FIF_MODULE` (`S49`, \$2.16). |
| **Surgical Edits & Docs** | **Gemini 3.8 Flash (Medium/High)** | `1–10` turns | `S17`, `S18`, `S22`, `S28`, `S31`, `S32`, `S36`, `S44` averaged **\$0.08** and **0 compactions**. |

### Golden Routing Rules
1. **Never escalate to Opus mid-stream inside a bloated session.** Switching to Opus inside `S10` (24 turns = **\$20.84**) and `S51` (18 turns = **\$17.02**) cost **\$37.86** for 42 turns because Opus paid \$3.75/M cache-read and \$15/M input on accumulated history. Always start a **fresh session** with a 1-page handoff summary if frontier reasoning is needed.
2. **Downgrade after design is locked.** `S11` (Argon → Flash) and `S44` (Argon → Flash) cleanly saved tokens on execution/wrap-up.

---

## 2. Context, Compaction & Session-Splitting Rules

1. **Hard-stop before Context Compaction.** Only **5 of 51 sessions** (`S02`, `S10`, `S21`, `S43`, `S46`) crossed compaction, yet they consumed **51.5% of total spend (\$52.23 / \$98.88)** and **34.5% of all tool errors (316 / 915)**.
2. **Split at logical boundaries.** Short sessions (`<= 40` turns, 14 sessions) had **0 compactions** and averaged **\$0.12** (excluding `S01`). Deliberate handoffs like `S14` (\$0.34) → `S15` (\$0.51) rebuilt the entire V2 architecture for **\$0.85 total**.
3. **Cap identical-file reads and compile-fail retries.** If the AI reads the same file >4 times (e.g., `S13` read `SessionTracker.cs` **37×**, `S10` **36×**) or fails 3 builds on the same SDK/COM error, stop and either inspect official SDK metadata or start a fresh session.
4. **Watch for 10k+ char thinking traces.** Single thinking blocks above 10,000 characters (`S11`: 40,693 chars; `S10`: 28,150 chars; `S51`: 14,338 chars) signal circular reasoning or speculative over-engineering—pause and ask the pilot.

---

## 3. Prompt Engineering Playbook (For the Pilot)

### Patterns That Consistently Worked
- **Single-Focus Opening Prompts:** Averaged **\$1.64** and **84.7 tool calls** vs. **\$6.93** and **203.6 tool calls** for multi-part bundled (`1. ... 2. ...`) prompts ([Dimension 2](file:///C:/Dev/VSOnDemandSymbols/chat_history/DIMENSION_2_PROMPT_ANALYSIS.md)).
- **Design-First Gate:** *"Suggest possible solutions for this without implementing them yet"* (`S21`) or *"before you even start implementing, update the docs to reflect the plan"* (`S51`).
- **Telemetry-Backed Bug Reports:** Pasting exact `[Profile]` timing lines (`S49`) produced a direct, verified fix in 53 turns (**\$2.16**).
- **Challenge Complexity Early:** Asking *"are there any simplifications we could make as it seems like quite a number of classes?"* (`S51`) or *"do we need to be able to pin specific modules?"* (`S13`) prevented thousands of lines of unnecessary code.

### Reusable Prompt Templates

1. **Read-Only Subagent Research Spike:**
   > *"Explore `<target paths/SDK>` in read-only mode. Do not modify any files. Return: (1) exact file paths and line numbers, (2) 1–3 sentence summaries of how `<mechanism>` works, and (3) verbatim snippets `<= 35` lines."*
2. **Design Gate (Before Coding):**
   > *"Investigate `<bug/feature>` and propose 2–3 architectural options with pros, cons, and estimated files/LOC changed. Do NOT edit code yet. Wait for my selection."*
3. **Bounded Phase Execution:**
   > *"Follow `AGENTS.md`. Implement ONLY `<Phase/Step X>` from `docs/IMPLEMENTATION_PLAN.md`. Run `<build/test command>`. If a build fails 3 times on the same error or requires an architectural change, stop and report instead of guessing."*
4. **Rationalization / Pruning Pass:**
   > *"Review `<component/tests>` for dead code, redundant abstractions, and simpler host-native alternatives. Propose a deletion/simplification plan first; wait for approval before editing."*

---

## 4. Architecture & Verification Rules

1. **Verify host/engine ground truth before designing abstractions.** Run cheap Flash subagents on target SDKs/engines (`S04–S09`, **\$0.57**) before writing Phase 1 specs.
2. **Prefer host-native declarative mechanisms over in-process hooks.** Native VS `IncludeList` (`ADR-01`) + `IVsDebugLaunchHook` (`ADR-15`) beat Concord `.vsdconfig` interception, eliminating **7,255 lines** in `57e200a`.
3. **Start stateless; add state/UI only when proven necessary.** `RecentlyBuiltScanner`, MRU/pinning, `state.json`, and custom menu commands accounted for **>8,500 lines of deletions**.
4. **Split pure logic into a sandbox-testable `Core` library on Day 1.** `VSOnDemandSymbols.Core` (`netstandard2.0`) + `dotnet run --no-build` + `/p:UseSharedCompilation=false /nr:false` in `AGENTS.md` enabled fast, hang-free sandbox verification.
5. **Isolate `BypassSandbox: true` to a pre-built out-of-proc test runner + real-world benchmarks.** `MiniUnreal` (`ADR-12`) + `--profile-lyra` (`ADR-14`) across **1,062 real DLLs** caught Win32 modal dialogs (`ADR-13`) and proved **29.6%–61.5% startup speedups** without manual F5 loops.
6. **Commit at every logical checkpoint and treat deletions as milestones.** Dedicated pruning passes (`S14–S15`, `S35`, `S43`, `S47`) kept the final codebase lean at **10,781 net LOC**.
