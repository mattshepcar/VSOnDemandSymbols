# On-Demand Symbols: Final 22-Slide Presentation Plan

> **Target Duration:** ~15 minutes  
> **Output File:** [docs/index.html](file:///C:/Dev/VSOnDemandSymbols/docs/index.html) (live at `https://mattshepcar.github.io/VSOnDemandSymbols/`)  
> **Design System:** Projection-first 16:9 deck with **Blue Skies** default + live theme switcher (`T`), toggleable speaker notes (`N`), slide overview grid (`G` / `Esc`), animated counters/bars, and a live prompt-cache cost toggle.

---

## Title
1. **From a Weekend Idea to a Working VSIX** — `~50 AI sessions · 7 days · ~$100` (with embedded VSIX lightning icon SVG).

## Act I — Cleared for Takeoff *(Setup & Scale)*
2. **Act I Divider:** *Cleared for Takeoff*
3. **The Problem:** *UE5 + F5 = Tea break time 🍵* (1,000+ DLLs, 950 PDBs, 26+ GB loaded on every launch).
4. **The Solution:** *On Demand Symbols: 11 PDBs instead of 950.* (30%–62% faster launch; loads breakpoint PDBs upfront and call-stack PDBs only on break).
5. **The Project by the Numbers:** 2×2 stat grid (`~27 hrs` active time, `450M` tokens, `4,553` tool calls, `~$100` spend with 95% cache hit rate).
6. **The Timeline:** *Seven days, one sharp reset.* (Diagonal staircase from Day 1 Spec/Hooks → Day 2 Reset & Working V2 → Days 3–5 Polish & Settings → Day 6 Test Harness → Day 7 Multi-Instance & Audit).

## Act II — Catching a Tailwind *(The Upside of AI)*
7. **Act II Divider:** *Catching a Tailwind*
8. **AI Reflected on VS’s Own DLLs:** When docs didn’t exist, AI inspected installed IDE assemblies directly.
9. **AI Built Its Own Test Harness:** When testing needed a human (`Manual F5`), AI built an out-of-proc `Autopilot` harness via ROT COM to self-test against 1,062 real DLLs.

## Act III — Turbulence & Headwinds *(The Traps)*
10. **Act III Divider:** *Turbulence & Headwinds*
11. **Code Churn:** *60% of all code written was deleted.* (`+26k / -15k → 10.8k` net LOC across 71 commits).
12. **Root Causes:** *Why did we delete 60% of the code?* (Overspecifying upfront, early overengineering, not asking "why?").
13. **The Pilot’s Job:** *AI generates abstractions. You delete them.* (V1 speculative stack `$33` / 7,255 lines cut vs. V2 native rebuild `$4.50`).
14. **Cheap Resets:** *Resetting is cheap with AI.* (Zero emotional attachment + AI preserves hard-won lessons in docs/ADRs).
15. **Compaction & Cost:** *5 compacted sessions caused 52% of spend.* (`52%` of cost & `35%` of errors vs. `$0.12` avg / `0` compactions for short sessions).

## Act IV — Aviate, Navigate, Communicate *(Playbook & Outro)*
16. **Act IV Divider:** *Aviate, Navigate, Communicate*
17. **Model Routing:** *Never escalate models mid-session.* (Fresh session `$0.15–$1.80` vs. mid-bloat escalation `$38` in 42 Opus turns; Flash 80% turns / `$10` vs. Opus 1.5% turns / `$50` + cache toggle).
18. **Prompting Discipline:** *Single-focus prompts cost 4× less.* (`$1.64` vs. `$6.93` danger bar for bundled multi-part prompts).
19. **Guardrails:** *Let AI write the rules—then cross-check fresh.* (Codify environment quirks in `AGENTS.md`, then verify in a clean session).
20. **Pilot Quotes:** *Mid-flight course corrections. 🎛️* (Real verbatim steering quotes from the logs).
21. **5 Pre-Flight Tips:** *5 pre-flight tips for your next AI project. 📋*
22. **Closing:** *Full disclosure: I’m still winging it. 🤷‍♂️*
