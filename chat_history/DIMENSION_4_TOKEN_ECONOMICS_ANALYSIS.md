# Dimension 4 Analysis: Token Economics and Cost Modeling

> **Scope:** Exact step-level token and cost accounting across all **51 sessions** (**4,386 turns**, **449.58M total tokens**) in `chat_history/summary_metrics.json`, using the turn-level rate card in [extract_chat_history.py](file:///C:/Dev/VSOnDemandSymbols/chat_history/extract_chat_history.py).  
> **Goal:** Account for total spend, cost per milestone, prompt-cache savings, error-loop waste, and the effectiveness of mid-session model switching.

---

## 1. Executive Summary

- **Total Project Cost:** **\$98.88 USD** across **51 sessions** (**44** main sessions, **7** subagent sessions).
- **Total Token Volume:** **449,579,311 tokens** (**21.11M** uncached input, **425.93M** cache read, **2.53M** output), achieving a **95.28% cache hit ratio**.
- **Prompt Caching Saved \$204.56 (67.4%):** Without prompt caching, the exact same workload would have cost **\$303.44**.
- **The Model Cost/Volume Paradox:**
  - **Gemini 3.8 Flash** executed **80.1% of all turns (3,514 / 4,386)** for just **\$10.23 (10.3% of spend)**.
  - **Claude Opus 5.5** executed **1.5% of all turns (64 / 4,386)** yet accounted for **\$49.64 (50.2% of spend)**—driven largely by two mid-session escalations (`S10` and `S51`) that cost **\$37.86** in just **42 turns**.
- **Two Dead-End / Overshoot Clusters Consumed 51.7% of Total Spend:** V1 Concord + MSBuild (`S10–S11`: **\$33.45**) and `S51` multi-instance (`\$17.64`) together cost **\$51.09**.

---

## 2. Model Breakdown & Rate Card

### 2.1 Rate Card (USD per 1M Tokens)

| Tier | Uncached Input | Prompt Cache Read | Output |
|---|---:|---:|---:|
| **Claude Opus 5.5** (`opus`) | \$15.00 | \$3.75 | \$75.00 |
| **Gemini 4 Argon** (`argon`) | \$1.25 | \$0.30 | \$5.00 |
| **Gemini 4 Hydrogen** (`hydrogen`) | \$0.80 | \$0.20 | \$3.20 |
| **Gemini 3.8 Flash** (`flash`) | \$0.10 | \$0.025 | \$0.40 |

### 2.2 Usage & Cost by Model Variant

| Model Variant | Sessions Used | Turns | Turn Share | Input (M) | Cache Read (M) | Output (M) | Actual Cost | Cost Share |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| **Claude Opus 5.5 (Medium)** | 2 | 42 | 1.0% | 0.403 | 6.810 | 0.084 | **\$37.86** | 38.3% |
| **Gemini 4 Argon (Medium)** | 10 | 609 | 13.9% | 3.822 | 72.388 | 0.498 | **\$28.99** | 29.3% |
| **Claude Opus 5.5 (High)** | 1 | 22 | 0.5% | 0.193 | 1.022 | 0.067 | **\$11.79** | 11.9% |
| **Gemini 4 Argon (High)** | 3 | 195 | 4.4% | 1.035 | 24.619 | 0.226 | **\$9.81** | 9.9% |
| **Gemini 3.8 Flash (High)** | 27 | 2,252 | 51.3% | 10.650 | 217.211 | 1.005 | **\$6.90** | 7.0% |
| **Gemini 3.8 Flash (Medium)** | 18 | 1,262 | 28.8% | 4.867 | 103.483 | 0.645 | **\$3.33** | 3.4% |
| **Gemini 4 Hydrogen (Medium)** | 1 | 4 | 0.1% | 0.141 | 0.402 | 0.006 | **\$0.21** | 0.2% |
| **Total** | *51* | **4,386** | **100.0%** | **21.113** | **425.934** | **2.532** | **\$98.88** | **100.0%** |

---

## 3. Cost per Component & Project Milestone

| Milestone / Phase | Sessions | Turns | Tool Calls (Errors) | Total Cost | Share of Spend |
|---|---|---:|---:|---:|---:|
| **Spec & Phase 0** | `S01–S02` | 207 | 253 (57) | **\$19.80** | 20.0% |
| **Phase 1: Core & Unit Tests** | `S03` | 187 | 163 (7) | **\$2.95** | 3.0% |
| **Phase 2: UBT Subagents (`D:\UnrealEngine`)** | `S04–S09` | 328 | 305 (83) | **\$0.57** | 0.6% |
| **Phase 3–4: V1 Concord & MSBuild Triggers** | `S10–S11` | 517 | 662 (124) | **\$33.45** | **33.8%** |
| **V2 Clean-Slate & Break-Mode / Settings** | `S12–S23` | 1,414 | 1,370 (293) | **\$4.51** | 4.6% |
| **Icon, Reviews, Stats & Maintenance** | `S24–S44` | 1,202 | 1,185 (229) | **\$7.08** | 7.2% |
| **Integration Tests (`MiniUnreal` & `--profile-lyra`)** | `S45–S47` | 228 | 302 (48) | **\$10.09** | 10.2% |
| **Break-Mode AD7 `FIF_MODULE` Perf** | `S49` | 53 | 50 (18) | **\$2.16** | 2.2% |
| **Multi-Instance Coordination (`HiveLock`)** | `S51` | 120 | 134 (31) | **\$17.64** | 17.8% |
| **Meta / Chat Analysis / Skills** | `S48, S50` | 130 | 129 (25) | **\$0.62** | 0.6% |
| **Total** | **`S01–S51`** | **4,386** | **4,553 (915)** | **\$98.88** | **100.0%** |

*Note: V2 Clean-Slate through Polish (`S12–S44`) delivered 2,616 turns of working, shipped code for **\$11.59 total** using Gemini 3.8 Flash (High/Medium) and targeted Argon reviews.*

---

## 4. Prompt Cache Savings & Cost of Errors

### 4.1 Prompt Cache Savings by Tier

| Tier | Actual Cost (With Cache) | Counterfactual Cost (Uncached Input Rate) | Dollars Saved by Cache |
|---|---:|---:|---:|
| **Claude Opus 5.5** | \$49.64 | \$137.75 | **\$88.11** |
| **Gemini 4 Argon** | \$38.80 | \$130.95 | **\$92.16** |
| **Gemini 3.8 Flash** | \$10.23 | \$34.28 | **\$24.05** |
| **Gemini 4 Hydrogen** | \$0.21 | \$0.45 | **\$0.24** |
| **Total** | **\$98.88** | **\$303.44** | **\$204.56 (67.4%)** |

*(Note: Individual tier sums match \$98.88 after per-model rounding.)*

### 4.2 Direct Proportional Cost of Tool Error Loops

Using tool failure share (`error_calls / total_calls`) as a proportional proxy for wasted turns:
- **Session 10:** `100 / 559` (17.9%) × \$30.93 = **\$5.53** in direct failing tool steps (excluding the larger downstream cost of Concord rework).
- **Session 21:** `96 / 456` (21.1%) × \$1.77 = **\$0.37** (Flash High kept the dollar penalty tiny despite 96 errors).
- **All 51 Sessions Combined:** **\$20.37** proportional direct cost across all 915 tool errors.

---

## 5. Effectiveness of Mid-Session Model Switching

Seven sessions switched models mid-stream (`S03`, `S10`, `S11`, `S27`, `S43`, `S44`, `S51`). Evaluating pre- vs. post-switch economics reveals a sharp asymmetry:

| Session | Switch Trajectory | Pre-Switch / Post-Switch Cost | Verdict |
|---|---|---|---|
| **`S11`** (\$2.52) | Argon Med (`37t`, \$2.45) → Flash High (`10t`, \$0.06) | Downgraded cleanly | **Effective:** Used Argon for Phase 4 architecture, then Flash to finish cleanly. |
| **`S44`** (\$0.14) | Argon High (`7t`, \$0.12) → Flash Med (`10t`, \$0.01) | Downgraded cleanly | **Effective:** Cheap Flash completion for Mermaid diagram fix. |
| **`S27`** (\$0.73) | Flash Med (`1t`, \$0.00) → Flash High (`199t`, \$0.73) | Same tier, higher reasoning | **Effective:** Negligible cost delta for thorough GitHub-readiness audit. |
| **`S43`** (\$4.04) | Argon High (`11t`, \$0.59) → Flash Med (`40t`, \$0.15) → Argon Med (`54t`, \$3.30) | Mixed plan + execute | **Mixed:** Using Argon High for the initial review (`\$0.59`) was great; switching back to Argon Med for routine edits inflated cost. |
| **`S03`** (\$2.95) | Flash Med (`150t`, \$0.37) → Argon Med (`37t`, \$2.58) | Escalated 7× cost for final 20% | **Costly:** Argon spent \$2.58 on the final 37 turns after Flash already did 150 turns for \$0.37. |
| **`S10`** (\$30.93) | Argon High (`177t`, \$9.10) → Flash Med (`269t`, \$1.00) → **Opus Med (`24t`, \$20.84)** → Flash Med | **\$20.84 for 24 Opus turns** | **Counterproductive:** Opus inherited a bloated Concord context and spent \$0.87/turn without saving the V1 architecture. |
| **`S51`** (\$17.64) | Flash Med (`97t`, \$0.28) → Argon Med (`1t`, \$0.13) → **Opus Med (`18t`, \$17.02)** → Hydrogen/Flash | **\$17.02 for 18 Opus turns** | **Counterproductive:** Opus inherited Flash's 97-turn history (\$0.95/turn) and still overbuilt Phase 1 before the pilot forced Option A. |

### Optimal Model-Routing Strategy for Future Projects

1. **Plan & Review with Argon/Opus in a *Fresh* Session (`<= 20` turns):** `S01` (Opus High, 22 turns, \$11.79) and `S43` (Argon High, 11 turns, \$0.59) proved that frontier models excel at upfront architecture and code review when context is clean.
2. **Execute Bounded Phases with Flash High (`\$0.15–\$1.77` per session):** `S15`, `S21`, `S27`, and `S35` proved Flash High can execute 100–475 turns of complex C#/VSIX work at ~1/12th the cost of Argon and ~1/150th the cost of Opus.
3. **Never Escalate to Opus Mid-Stream Inside a Bloated Session:** Escalating to Opus inside `S10` and `S51` cost **\$37.86** for **42 turns** because Opus paid \$3.75/M cache-read and \$15/M input on an already-huge context. If a task genuinely needs Opus, **start a new session with a 1-page summary**.

