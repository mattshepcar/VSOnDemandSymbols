# Decision Log

Append new decisions at the end. Never rewrite history: if a decision is reversed, add a new entry that supersedes the old one.

| ID | Date | Decision | Rationale / source |
|---|---|---|---|
| D-1 | 2026-11-05 | MRU entries are pruned when **either** `RunsSinceTrigger >= 5` **or** 24 h have passed since the last trigger. Both thresholds are configurable. | User choice (initial Q&A). |
| D-2 | 2026-11-05 | "Recently modified/compiled" is detected **only** from binary timestamps (DLL/PDB newer than the previous session start). Source edits are not tracked. | User choice. Keeps it simple, and it catches builds from any tool. |
| D-3 | 2026-11-05 | Call-stack loading is configurable (`None`, `CurrentThread`, `CurrentThreadAllThreadsOnException`, `AllThreads`). Default: current thread, plus all threads on exceptions and hard breaks. There is also an option to load symbols when the user activates a frame (double-click), on by default. | User asked for both to be configurable. The defaults were chosen by the agent. |
| D-4 | 2026-11-05 | Scope is configurable (`Off`, `UnrealOnly`, `AllNative`). Default `UnrealOnly`. | User choice. |
| D-5 | 2026-11-05 | UI: enable toggle (menu and toolbar), Options page, Output pane log, tool window with pin/unpin/forget, and source-file context menu "Always load symbols for this module". | User selected all of these. |
| D-6 | 2026-11-05 | Supported: VS 2022 17.14+ and VS 2026; UE 5.x naming. The naming logic stays data-driven so UE4 (`UE4Editor-*`) can be added later. | User choice. |
| D-7 | 2026-11-05 | MRU and session state are stored per solution in `.vs/<SolutionName>/OnDemandSymbols/state.json`. | User choice. |
| D-8 | 2026-11-05 | The mechanism is VS's "Load only specified modules" + include list (with load-adjacent off) for pre-launch loads, plus programmatic `LoadSymbols` for on-demand loads. Concord is the fallback. | Agent proposal, pending the Phase 0 spikes. |
| D-9 | 2026-11-05 | The on-demand settings stay applied the whole time a qualifying solution is open, rather than only around launches. This avoids depending on an unreliable before-launch hook. Settings are restored on close, disable, or VS exit, and recovered after a crash. | Agent proposal. |
| D-10 | 2026-11-05 | The Unreal module→DLL mapping prefers UBT's `.modules` manifests and falls back to naming rules derived from the UBT source. | Agent proposal. Manifests are what the engine itself uses at runtime. |
| D-11 | 2026-11-05 | A mass-rebuild guard (default 40 modules) suspends the recently-built rule after large rebuilds. | Agent proposal. Without it, an engine rebuild would load every symbol. |
| D-12 | 2026-11-05 | Reference `Microsoft.VisualStudio.SDK` `17.14.40265` and `Microsoft.VSSDK.BuildTools` `18.9.820` on NuGet. | The template's `18.11.1036`/`18.11.1034` versions are not published on nuget.org. `SDK 17.14.40265` enforces our `[17.14,)` API baseline (NF-8), while `BuildTools 18.9.820` supports SDK-style `<VSSDKBuildToolsAutoSetup>true</VSSDKBuildToolsAutoSetup>` packaging in both VS 2022 and VS 2026. |
| D-13 | 2026-11-05 | Set `IVsDebuggerSymbolSettings170.AutomaticSymbolLoad = false` while on-demand mode is active, and back up / restore it alongside `IsManualLoad`, `IsLoadAdjacent`, and `IncludeList`. | Spike S1 found that VS 17.0+ has an "Automatically include additional symbols when needed" setting (`AutomaticSymbolLoad`, default `true`) under manual-load mode. Disabling it while active gives the extension deterministic control over call-stack loading (`CallStackMode` and `NeverLoadPatterns`, answering SPEC §12 Q4). |
| D-14 | 2026-11-05 | Use public AD7 + `IVsDebuggerSymbolSettingsManager120A` APIs (no Concord `.vsdconfig` component). | Spikes S1–S5 and S7 confirmed that `SaveSymbolSettings(settings, loadSymbolsNow: false)` updates the engine's include list live during a session, `IDebugModule3.LoadSymbols()` triggers Concord's `OnModuleSymbolsLoaded` to bind pending breakpoints automatically, `IDebugBreakpointErrorEvent2` catches editor-margin breakpoints added mid-session, and `IDebugSymbolSearchEvent2` detects manual loads. |

