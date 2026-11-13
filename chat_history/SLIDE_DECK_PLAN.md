# On-Demand Symbols: Final 31-Slide Presentation Plan

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
5. **The Project by the Numbers:** 2×2 stat grid (`~19 hrs` active time, `494M` tokens, `4,850` tool calls, `~$100` spend with 95% cache hit rate).
6. **The Timeline:** *Seven days, one aborted approach.* (Diagonal staircase from Day 1 Spec/Hooks → Day 2 Reset & Working V2 → Days 3–5 Polish & Settings → Day 6 Test Harness → Day 7 Multi-Instance & Audit).

## Act II — Catching a Tailwind *(The Upside of AI)*
7. **Act II Divider:** *Catching a Tailwind*
8. **AI Reflected on VS’s Own DLLs:** When docs didn’t exist, AI inspected installed IDE assemblies directly.
9. **AI Built Its Own Test Harness:** When testing needed a human (`Manual F5`), AI built an out-of-proc `Autopilot` harness via ROT COM to self-test against 1,062 real DLLs.
10. **Parallel Wingmen:** *“Fly, my beauties!”* (6 Flash subagents completed 55m of Unreal Engine research in 22m wall-clock for $0.57, keeping main context clean).

## Act III — Turbulence & Headwinds *(The Traps)*
11. **Act III Divider:** *Turbulence & Headwinds*
12. **Code Churn & Root Causes:** *60% of all code written was deleted.* (`+26k / -15k → 10.8k` net LOC across 71 commits; overspecifying + not asking "why?" in notes).
13. **The Pilot’s Job & Cheap Resets:** *AI overbuilds. Jettison the baggage.* (V1 speculative stack `$33 · 2h 45m stalled` / 7,255 lines cut vs. V2 native rebuild `$4.50 · 4h 12m` at 337 turns/hr; ejector-seat parachute takeaway + zero-attachment / lesson-preservation in notes).
14. **Model Speed & Cost:** *81% of turns cost $11. 1.4% cost $50.* (Flash carried 81% of turns *"Gordon's alive!"* — and 6s turns keep you in flow; live prompt-cache toggle).
15. **Compaction & Cost:** *6 compacted sessions caused 71% of spend.* (`71%` of cost & `44%` of errors vs. `$0.12` avg / `0` compactions for short sessions; ~3.8 hrs lost to error loops in notes).

## Act IV — Aviate, Navigate, Communicate *(Playbook)*
18. **Act IV Divider:** *Aviate, Navigate, Communicate*
19. **Model Routing:** *Never escalate models mid-session.* (Fresh session `$0.15–$1.80` vs. mid-bloat escalation `$38` in 27m / 42 Opus turns; downgrading to Flash works cleanly in notes).
20. **Prompting Discipline:** *Single-focus prompts cost 4× less.* (`$1.64` vs. `$6.93` danger bar for bundled multi-part prompts).
21. **Guardrails:** *Let AI write the rules—then cross-check fresh.* (Codify environment quirks in `AGENTS.md`, then verify in a clean session).
22. **Pilot Quotes:** *Mid-flight course corrections. 🎛️* (Real verbatim steering quotes from the logs).
23. **5 Pre-Flight Tips:** *5 pre-flight tips for your next AI project. 📋* + *"Full disclosure: first time 'vibe coding' in anger — I'm still winging it. 🤷‍♂️"* footnote.

## Act V — Uncharted Airspace *(Open Questions)*
24. **Act V Divider:** *Uncharted Airspace* — *“Surely you can’t be serious?” “I am serious… and don’t call me Shirley.”* (Airplane!, 1980).
25. **Open Q1 — Maintainability:** *Small tools are easy. Big codebases?* (Humans own the architecture · Can’t explain it? Don’t merge it · Context is code → *Code is cheap. Understanding isn’t.*)
26. **Open Q2 — What to Code Ourselves:** *What should we still code ourselves?* (Keep the hard thinking · Debug first, ask second · Delegate the drudge → *Pilots still hand-fly to stay current.*)
27. **Open Q3 — Human Collaboration:** *Keeping the human in the crew.* (Share the flight logs · Explain it to a human · Mentor judgement, not syntax → *Use the time AI saves to talk more, not less.*)
28. **More Questions:** *Questions we should also be asking.* (Is it actually faster? · Who reviews the flood? · Where do the next seniors come from? · Who owns the bugs? · What happens when the subsidies end?) — sources and detail in speaker notes.

## And Finally — Reading the Black Box *(Outro, not an act)*
29. **And Finally Divider:** *Reading the Black Box* — *“This is your autopilot speaking. I’ve reviewed my own flight logs, and I’m delighted to report I was brilliant.”* (Black-box/flight-recorder SVG; badge “And Finally” instead of an act number.)
30. **Self-Reflection & Vibe Decking:** *"The bots analysed all 50 chat logs, generated these slides, and had them cross-checked by another member of the cabin crew."* + 2×2 stat grid (`~6 hrs`, `73.9M` tokens, `$14.31` on Gemini 4 Hydrogen, `$0.60 / slide`).
31. **Landing / Q&A:** Aeroplane landing SVG + *Questions? 🙋‍♂️* (deferring hard ones to co-pilot Claude).
