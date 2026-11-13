# Presentation Design Rules & Learnings

> **Source:** Lessons distilled while iterating on [docs/index.html](file:///C:/Dev/VSOnDemandSymbols/docs/index.html) — turning raw engineering telemetry into a cohesive, projection-ready keynote.

---

## 1. Serve the Talk’s Thesis, Not the Raw Data

1. **Keep the audience’s lens front and center.**
   If the talk is about *how to work with AI*, don’t drag the audience into implementation weeds (specific internal APIs like Concord, AD7, or ROT) on narrative/timeline slides. Summarize the *type of work* instead.
2. **Strip internal bookkeeping.**
   Session numbers (`S10`, `S51`), exact calendar dates (`Nov 5 – Nov 12` on a 7-day arc), and internal repo identifiers (`VSOnDemandSymbols` → *On Demand Symbols*) add noise without helping the audience.
3. **Use human-scale numbers on takeaway slides.**
   Exact figures (`$98.88`, `4,386 turns`) belong in speaker notes or headline stats; on summary/tips slides, plain language (*"cheap subagents"*, *"cost pennies"*, *"fast workhorse models"*) is faster to scan and ages better. Use **2 significant figures** for percentages (`59%`, `95%`, `30%–62%`) and clean rounded headlines (`~50 sessions`, `~$100`).
4. **Keep terminology audience-facing & accurate.**
   Replace internal repo tags (`[Profile] logs` → *"output logs"*) and over-stated labels (*"shipped VSIX"* → *"working VSIX"*).
5. **Stay proportional—avoid AI-style hyperbole.**
   Don’t call routine steering *"Three questions that saved the project"* when *"Mid-flight course corrections"* is both truer and funnier.

---

## 2. Story Structure & Flow

1. **Use a clear 4-Act arc with explicit divider slides.**
   - **Act I — Setup & Scale:** Problem → Solution → Numbers → Timeline.
   - **Act II — The Upside:** Where the approach genuinely surprised you.
   - **Act III — The Traps:** Churn, dead ends, and cost/context blowups.
   - **Act IV — The Playbook & Outro:** Actionable rules, real quotes, tips, and a human sign-off.
2. **Always put the Problem before the Solution.**
   Establish the relatable pain (*"UE5 + F5 = Tea break time 🍵"*) on its own slide before showing the fix (*"11 PDBs instead of 950"*).
3. **Keep related points together.**
   Don’t scatter one theme (like code deletion or AI superpowers) across opposite ends of the deck. Consolidate redundant slides into one stronger slide.
4. **End with humility and humor.**
   Be upfront about early field-notes status and how the deck was made (*"Full disclosure: I’m still winging it 🤷‍♂️"*).

---

## 3. Sharp Comparisons & Genuine Insights

1. **Compare apples to apples in two-column splits.**
   Both halves of a contrast slide must measure the same dimension (e.g., `Manual F5` vs. `Autopilot`, not `Manual F5` vs. `1,062 DLLs`).
2. **Highlight what is *non-obvious*, not what anyone could do.**
   - *Good:* AI reflecting on installed IDE binaries when docs don’t exist; AI building its own GUI test harness; resetting being cheap because there’s zero emotional attachment and AI preserves hard-won lessons.
   - *Skip:* Routine file grepping that a human would do easily anyway.
3. **Verify hunches against the data before adding them.**
   When you suspect a pattern (e.g., *"Are workhorse models also faster per turn?"*), measure it from the logs (`6.2s` vs. `12.9s` vs. `30.2s`) and surface both cost and speed together.
4. **Use real, imperfect quotes.**
   Verbatim pilot moments (*"Woah, what’s with all the searching?"*, *"The lightning bolt isn’t centred!"*) are far more engaging than sanitized summaries.
5. **Filter idle gaps out of time metrics & explain root causes.**
   Raw session `end - start` spans include overnight idle gaps (`97.9 hrs` wall-clock vs. `27.0 hrs` active)—filter out idle gaps before quoting "hours of AI time." Likewise, pair headline churn stats (`59% deleted`) with *why* it happened (*overspecifying upfront*, *early overengineering*, *not asking "why?"*).

---

## 4. Layout, Typography & Visual Polish

1. **Never squeeze 5+ items into narrow horizontal columns.**
   A 5-column timeline forces tiny text. Use a **diagonal staircase** (`0% → 16%` progressive indent) so every row has room for large, readable projection typography.
2. **Pick a cohesive visual motif and commit to it.**
   Consistent aviation metaphors (*Cleared for Takeoff*, *Catching a Tailwind*, *Turbulence & Headwinds*, *Aviate, Navigate, Communicate*, *ejector seat*, *fuel gauge*, *flight plan*), matching SVG watermarks, and a dedicated default palette (*Blue Skies*) tie the whole deck together.
3. **Add purposeful motion and interactivity—not clutter.**
   - Animate bar fills and key hero numbers on slide entry.
   - Use one interactive toggle (e.g., *Actual $99* vs. *Without Cache $303*) to make a key insight visceral.
   - Include presenter ergonomics: toggleable speaker notes (`N`), a slide-overview grid (`G` / `Esc`), and live theme switching (`T`) between dark and bright-room palettes.

---

## 5. Projection Ergonomics, Color Semantics & Data Honesty

1. **Design for the back row, not a laptop dashboard.**
   Strip scanlines, HUD headers, tiny status pills, and multi-card borders. Target **36–48pt+ titles**, **24–32pt+ body text**, **40%+ empty whitespace**, and **one core idea per slide**—tuck supporting details into speaker notes.
2. **Use the full horizontal canvas (and 2×2 over 4×1).**
   Avoid narrow `max-width: ...ch` caps on titles or lists—they cause awkward orphan wraps (e.g., breaking after a hyphen) and pin lists to the left half of the screen. Use explicit `<br />` only for intentional visual alignment (e.g., stacking `1.5%` directly under `80%`), and lay out 4 big stats in a **2×2 grid** rather than squeezing 4×1.
3. **Color encodes importance; bar widths must match their labels.**
   Never render a critical warning or high-cost spike (`$50` Opus, `$6.93` bundled prompt) in muted grey—it signals "unimportant." Use a high-contrast danger color (`--danger`) for the costly/bad bar, and ensure bar widths match the numbers being labeled (`$10` vs. `$50` → `20%` vs. `100%` width).

