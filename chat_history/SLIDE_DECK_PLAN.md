# VSOnDemandSymbols: 15-Minute Presentation Slide Plan

> **Target Duration:** 15 minutes (~1–1.5 min per slide across 11 slides)  
> **Format:** Self-contained static website (HTML/CSS/JS) with keyboard navigation, inline charts, and toggleable speaker notes (`N` key)  
> **Note on Numbers:** All percentages are rounded to **2 significant figures** for readability on slides.

---

## Slide-by-Slide Outline

### Slide 1: Title & Hook *(~0.5 min)*
- **Title:** *Building a Visual Studio Extension with AI: Cost, Lessons & Playbook*
- **Subtitle:** What started as *"a quick weekend project"* — 51 Antigravity sessions, 4,386 turns, and \$98.88 in 7 days.
- **Speaker framing:** Honest engineering retrospective on what worked, what burned half the budget, and the playbook we extracted.

### Slide 2: What Is VSOnDemandSymbols? *(~1.5 min)*
- **The Origin:** Intended as *"a quick weekend project"* to fix a daily pain point — debugging UE5 Editor (1,062 DLLs, 26+ GB of PDBs) stalls Visual Studio startup.
- **How It Works:** Puts VS into "load only specified modules" mode, populates `IncludeList` before launch so breakpoints bind immediately, and loads call-stack PDBs on demand when execution breaks.
- **Measured Result (UE5 Lyra):** Startup PDBs cut from **950 → 11** (1,051 skipped); launch-to-breakpoint time reduced by **30%–62%** (e.g., 22.1s → 8.5s warm).
- **Why AI Made It Possible:** *"I would never have been able to build this without AI"* — not because of typing speed, but because of the sheer volume of obscure domain research required.

### Slide 3: The Research Mountain (Why I Couldn't Have Built It Alone) *(~1.5 min)*
- **Unreal Engine Internals (`D:\UnrealEngine`):** 5 parallel Flash subagents (`S05–S09`) ran **235 turns** and **225 tool calls**, absorbing **20,670 lines** of UnrealBuildTool C# source (`UEBuildTarget.cs`, `ModuleManifest.cs`, `*.modules`) in **~15 min** for **\$0.35**.
  - *Estimated human time (cold start):* **1–2 days (8–16 hrs)** of grepping and cross-checking UBT build rules.
- **VS Unified Settings (`S21`):** The AI ran **100+ web searches** in one session to reverse-engineer the sparse/undocumented `registration.json` schema for VS 17.14.
  - *Estimated human time:* **1–2 days (8–16 hrs)** hunting GitHub/docs and testing in Experimental VS.
- **VS Debugger, AD7 & COM Internals (`S01–S49`):** Probing Concord `.vsdconfig`, `IVsDebugLaunchHook`, native `IncludeList`, AD7 `EnumFrameInfo(FIF_MODULE)`, Windows ROT COM automation, and Win32 modal dialog traps.
  - *Estimated human time:* **1–2 weeks (40–80 hrs)** of reading COM/SDK headers and running trial-and-error F5 loops.
- **Total Research Compression:** **~2–3 weeks (60–110 hrs)** of estimated solo human research compressed into hours — turning an impossible *"quick weekend project"* into a shipped VSIX.

### Slide 4: Timeline & Output — 7 Days at a Glance *(~1 min)*
- **Window:** Nov 5 – Nov 12, 2026 (**51 sessions**: 44 main, 7 subagents).
- **Activity:** **4,386 turns**, **4,553 tool calls**, **71 Git commits**.
- **The Churn Story:** **+26,251 / -15,470 lines** → **10,781 net LOC**. **59%** of all code ever written was deleted on purpose.

### Slide 5: The Bill — \$98.88 & The Token Economics Paradox *(~2 min)*
- **Volume:** **450M tokens** (449.58M) with a **95% cache hit rate** — prompt caching saved **\$204.56** (**67%** off the \$303.44 uncached cost).
- **The Paradox (Visual Split):**
  - **Gemini 3.8 Flash:** **80%** of turns (3,514) for **\$10.23** (**10%** of spend).
  - **Claude Opus 5.5:** **1.5%** of turns (64) for **\$49.64** (**50%** of spend).
- **Where the Money Went:** **52%** (**\$51.09**) went to two dead-end/overshoot clusters (`S10–S11` V1 Concord + `S51` overbuilt multi-instance). Meanwhile, V2 clean-slate through polish (`S12–S44`) delivered 2,616 turns of shipped code for **\$11.59**.

### Slide 6: Playbook 1 — Model Selection & Routing *(~1.5 min)*
- **Match Tier to Task:**
  - *Opus / Argon High* in **fresh sessions** (`≤ 25` turns) for upfront specs & architecture reviews (`S01`, `S43`).
  - *Flash Subagents* for read-only SDK/engine research (`S05–S09`, **\$0.35 total**).
  - *Flash High* for bounded implementation (`\$0.15–\$1.77` per session).
  - *Argon Medium* for complex integration & perf profiling (`S45–S47`, `S49`).
- **Golden Rule:** *Never escalate to a frontier model mid-stream inside a bloated session.* Two mid-session Opus escalations (`S10`, `S51`) burned **\$37.86 in 42 turns** paying cache-read on old history. Start fresh with a 1-page handoff instead.

### Slide 7: Playbook 2 — Context, Compaction & Session Splitting *(~1.5 min)*
- **Hard-Stop Before Compaction:** Only **5 of 51 sessions** crossed compaction, yet they caused **52%** of total spend (**\$52.23**) and **35%** of all tool errors (316 / 915).
- **Split at Logical Boundaries:** Short sessions (`≤ 40` turns) had **0 compactions** and averaged **\$0.12**.
- **Circuit Breakers:** Cap identical-file reads (e.g., `S13` read `SessionTracker.cs` 37×), cap build retries at 3, and pause on 10k+ char circular thinking traces.

### Slide 8: Playbook 3 — Prompt Engineering for the Pilot *(~1.5 min)*
- **Single-Focus vs. Bundled Prompts:** Focused opening prompts averaged **\$1.64** (85 tool calls) vs. **\$6.93** (204 tool calls) for multi-part bundled (`1. ... 2. ...`) prompts.
- **4 High-ROI Prompt Patterns:**
  1. *Design-First Gate* — "Propose 2–3 options without implementing yet."
  2. *Read-Only Subagent Research* — Exact paths, line numbers, snippets `≤ 35` lines.
  3. *Telemetry-Backed Bug Reports* — Paste exact `[Profile]` timing lines (`S49` → **\$2.16** fix).
  4. *Challenge Complexity Early* — "Do we even need this class/feature?"

### Slide 9: Playbook 4 — Architecture & Safe Verification *(~1.5 min)*
- **Native Host Primitives Beat Custom Hooks:** Replacing Concord `.vsdconfig`, MSBuild crawling, MRU caches, and custom menus with native VS `IncludeList` + `IVsDebugLaunchHook` deleted **>8,500 lines**.
- **Day-1 Sandbox Split:** Pure logic in `VSOnDemandSymbols.Core` (`netstandard2.0`) + sandbox-safe build/test flags (`/p:UseSharedCompilation=false /nr:false`, `dotnet run --no-build`).
- **Real-World Benchmarks Beat Mocks:** Pre-built out-of-proc `IntegrationTests.exe` driving `devenv.exe /rootsuffix Exp` via ROT COM (`BypassSandbox: true`) tested **1,062 real Lyra DLLs** and caught a modal *"Browse for PDB"* freeze that `MiniUnreal` never triggered.

### Slide 10: The Meta-Lesson — The Pilot's Real Job *(~1 min)*
- **Before vs. After:** V1 Over-engineered (**\$33.45**, 7,255 lines deleted) vs. V2 Lean (**\$4.51** to rebuild, 10,781 net LOC).
- **Core Insight:** AI has near-zero friction for *generating* abstractions, which means it will happily over-engineer. Almost every major simplification in the repo happened because the human pilot questioned *"Is this actually needed?"*

### Slide 11: Conclusion — 5 Takeaways for Monday Morning *(~1 min)*
1. **Use AI as a research engine first** — \$0.35 of subagent exploration beats weeks of guessing unfamiliar SDKs.
2. **Flash by default, Frontier in fresh sessions** — never escalate mid-bloat.
3. **Fear compaction** — split sessions early, keep prompts single-focus.
4. **Prefer native host primitives & fast sandboxed tests** — validate on real-world scale early.
5. **Your highest-leverage skill as an AI pilot is deletion.**
