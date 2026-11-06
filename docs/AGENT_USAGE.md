## Provisional advice by phase

| Phase | Nature of work | Suggested model |
|---|---|---|
| **0 Spikes** (VS debugger APIs) | Most uncertain. COM/AD7 APIs that are poorly documented and easy to get wrong. Mostly waiting on your manual tests, so few tokens. | **Strongest** (Gemini Pro or Claude) |
| **1 Core library** | Fully specified, pure C#, unit tests check the result | **Flash** for scaffolding, records, abstractions and tests. |
| **2 UBT analysis** | Reading lots of Unreal source code. **Highest token risk.** | Search the UE source with **Flash**, then hand only the relevant snippets to **Pro** to work out the naming rules. |
| **3–4 Debugger integration and triggers** | Hardest code: threading, COM, edge cases | **Strongest** model |
| **5 UI** (menus, options page, WPF tool window) | Mostly boilerplate | **Flash**, moving up a model for MVVM wiring if needed |
| **6 Hardening, README, changelog** | Docs and review | **Flash** for docs, **Pro** for the fault-handling review |

## Ways to save quota

- **Start a new conversation for each phase.** `AGENTS.md` and the docs are written so a fresh agent gets up to speed quickly. This conversation already holds the whole spec-writing context, so use `/compact` or start fresh before Phase 0.
- **Point agents at specific sections**, e.g. "implement BP-1 to BP-7 from SPEC §5.1". Don't ask them to "read all the docs".
- **Never let an agent browse the Unreal source freely.** List the exact UBT files from the MODULE_RESOLUTION.md §4 checklist.
- **Keep build output small** (`/v:minimal`, as `AGENTS.md` already says) so long MSBuild logs don't fill the context.
- **Batch the manual tests.** Ask for all the steps for a phase at once rather than going back and forth one test at a time.
