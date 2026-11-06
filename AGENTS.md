# AGENTS.md — Guide for AI coding assistants

## What this project is

**On-Demand Symbols** is a Visual Studio extension (VSIX) that speeds up debugging large native C++ applications, mainly the **Unreal Engine 5 editor** (about 800 DLLs). It puts the debugger into "load only specified modules" mode, then loads symbols automatically for exactly the modules that are needed:
- modules with breakpoints (populated into the debugger's `IncludeList` before launch so breakpoints bind immediately)
- modules on the call stack when execution breaks (loaded on demand)

**Read [docs/SPEC_V2.md](docs/SPEC_V2.md) and [docs/IMPLEMENTATION_PLAN_V2.md](docs/IMPLEMENTATION_PLAN_V2.md) before writing any code.** They are the source of truth for the V2 clean-slate architecture.

## Documentation map

| Doc | Use it for |
|---|---|
| [docs/SPEC_V2.md](docs/SPEC_V2.md) | **Primary specification** for the V2 clean-slate architecture |
| [docs/IMPLEMENTATION_PLAN_V2.md](docs/IMPLEMENTATION_PLAN_V2.md) | **Primary progress tracker** with phased checkboxes for V2 |
| [docs/MINIMAL_IMPLEMENTATION.md](docs/MINIMAL_IMPLEMENTATION.md) | Technical reference and code patterns for the minimal 4-component architecture |
| [docs/DECISIONS.md](docs/DECISIONS.md) | Decision log (D-1 to D-20). Append-only |
| [docs/BENCHMARKS.md](docs/BENCHMARKS.md) | Startup measurement protocol and benchmarks |
| [docs/SPEC.md](docs/SPEC.md) | Legacy V1 specification (superseded by V2) |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Legacy V1 architecture overview |

## Current status

- The project has transitioned to the **V2 Clean-Slate Architecture** on branch `v2-minimal`.
- See [docs/IMPLEMENTATION_PLAN_V2.md](docs/IMPLEMENTATION_PLAN_V2.md) for the active phases.
- The Unreal Engine source is at `D:\UnrealEngine`. It's outside the workspace, so reading it may need the user's permission.

## Golden rules

1. **Never leave the user's VS symbol settings modified.** All writes go through `SymbolSettingsController`, which backs up first and restores on close, disable, exit and crash recovery (SPEC §7.4, §9.2). The manual-load flag (`IsManualLoad`), the load-adjacent flag (`IsLoadAdjacent`), the automatic additional symbol load flag (`AutomaticSymbolLoad`), and the `IncludeList` are managed dynamically by the extension. The user's original `IncludeList` is preserved and merged with modules containing breakpoints (D-17, D-18, D-20). **Never** touch symbol paths, servers, the cache, or the exclude list.
2. **Never break the debug session.** Every debugger event handler is wrapped in try/catch and logs failures (NF-5).
3. **Keep the module-load path cheap.** About 800 module loads happen at startup. No file I/O, no blocking, no LINQ-heavy allocation in that handler (NF-1). Precompute everything before launch.
4. **Core stays VS-free.** Logic that can be tested without VS (resolution, naming, policy) lives in `VSOnDemandSymbols.Core` (netstandard2.0) with unit tests. The VSIX project is a thin adapter.
5. **Spec first.** If behaviour needs to differ from SPEC.md, update SPEC.md (and add a DECISIONS.md entry) in the same change, or ask the user. Don't silently diverge.
6. **Don't guess Unreal naming rules.** If something isn't confirmed in MODULE_RESOLUTION.md §8, check the UBT source or ask. Mark any assumption clearly as provisional.

## Tech stack and conventions

- C# (LangVersion 14, `Nullable` enabled), .NET Framework 4.7.2 for the VSIX; netstandard2.0 for Core; xUnit for tests.
- SDK-style VSIX project using `Microsoft.VisualStudio.SDK` 18.x and `Microsoft.VSSDK.BuildTools`. `InstallationTarget` is `[17.14,)`, so **only use APIs available in VS 2022 17.14**, unless guarded at runtime.
- `AsyncPackage` with `AllowsBackgroundLoading = true`. Auto-load through a UI context rule for solutions containing C++ projects; don't use `NoSolution` / `SolutionExists` auto-load.
- Threading: follow the VS threading rules (Microsoft.VisualStudio.Threading analyzers).
  - `await JoinableTaskFactory.SwitchToMainThreadAsync()` before any COM/DTE/AD7 call; `ThreadHelper.ThrowIfNotOnUIThread()` at the top of UI-thread-only methods.
  - No `.Result`, `.Wait()` or `Task.Run(...).GetAwaiter().GetResult()` on the UI thread.
  - Use `JoinableTaskFactory.RunAsync` for fire-and-forget work. Always observe exceptions.
- JSON: Newtonsoft.Json (ships with VS; reference it without bundling it in the VSIX).
- Options UI: `DialogPage` (works in both VS 2022 and VS 2026).
- UI: WPF + MVVM; use VS theme brushes (`EnvironmentColors`, `VsBrushes`) so light, dark and blue themes all work.
- Logging: use the `ILogger` abstraction → "On-Demand Symbols" output pane. Every symbol load logs its reason (SPEC §8.2).
- File naming: one public type per file; folders as in ARCHITECTURE.md §1.
- Style: see `.editorconfig`. File-scoped namespaces, `var` when the type is obvious, `_camelCase` private fields, and `Async` suffix on async methods.

## Build, test, debug

Run from the repo root in PowerShell.

```powershell
# Locate MSBuild (VSIX projects need VS's MSBuild, not just the dotnet CLI)
$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -prerelease -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1

# Build everything (sandbox-safe: no VBCSCompiler named pipes, no MSBuild node reuse)
& $msbuild VSOnDemandSymbols.slnx /p:Configuration=Debug /p:UseSharedCompilation=false /nr:false /v:minimal

# Unit tests (Core only — no VS needed; --no-build reuses the MSBuild output above)
dotnet run --project tests/VSOnDemandSymbols.Core.Tests --no-build

# Opt-in Unreal integration tests (requires access to D:\UnrealEngine)
$env:ODS_UE_ROOT = "D:\UnrealEngine"; dotnet run --project tests/VSOnDemandSymbols.Core.Tests --no-build
```

- **Preventing sandbox build hangs (important for agents):**
  - Always pass `/p:UseSharedCompilation=false /nr:false` (and avoid `/m`) when running MSBuild in the sandbox. Roslyn's `VBCSCompiler.exe` compiler server and MSBuild worker node reuse (`/nr:true`) rely on named pipes that hang indefinitely inside the Windows sandbox. (`<UseSharedCompilation>false</UseSharedCompilation>` is also set in the `.csproj` files as a safeguard.)
  - Omit `/restore` on normal builds once packages are restored; only pass `/restore` when adding or changing NuGet packages (which requires network access).
  - Run unit tests via `dotnet run --project tests/VSOnDemandSymbols.Core.Tests --no-build` rather than `dotnet test`, because the VSTest host uses local sockets/named pipes that can be blocked by the sandbox.
- Interactive debugging: F5 in VS launches the **Experimental instance** (`devenv.exe /rootsuffix Exp`). Agents can't drive that UI. For anything that needs a running VS or Unreal Editor, write precise manual test steps (see TESTING.md §3) and ask the user to run them.

## Working process for agents

1. Find the current phase and next unchecked item in [docs/IMPLEMENTATION_PLAN.md](docs/IMPLEMENTATION_PLAN.md).
2. Read the relevant spec sections and requirement IDs.
3. Implement in small, buildable steps. Add or extend unit tests for all Core logic, mirroring the requirement IDs in test names (for example `Mru_PrunesOnSixthRun_MRU5`).
4. Build and run the tests. Fix warnings you introduced.
5. Tick the checkbox in IMPLEMENTATION_PLAN.md. Record spike results, UBT findings or design changes in the right doc (VS_DEBUGGER_INTEGRATION.md §7, MODULE_RESOLUTION.md §8, DECISIONS.md).
6. Commit to git after completing each stage or phase, and at logical checkpoints when it makes sense (e.g. green test pass, major component addition or refactoring), so changes can be backtracked or compared with previous implementations. Use descriptive commit messages citing requirement IDs where applicable.
7. Summarise what changed, what was verified, and which manual tests the user should run.

## Definition of done (per change)

- Builds with no new warnings; unit tests pass.
- Behaviour matches SPEC.md (or the spec has been updated, with a decision entry).
- No UI-thread blocking, no unguarded event handlers, no change to symbol settings outside `SymbolSettingsController`.
- Docs and the progress tracker are updated.
- Changes are committed to git with a clear, descriptive message.

## Don'ts

- Don't modify files under `D:\UnrealEngine`. It's read-only reference material.
- Don't add telemetry or network calls (NF-6, NF-7).
- Don't bundle VS-provided assemblies (Newtonsoft.Json, VS SDK assemblies) into the VSIX.
- Don't use `DTE.ExecuteCommand` to drive the Modules window as a way to load symbols. Use the debugger APIs.
- Don't commit `bin/`, `obj/`, `.vs/`, or spike code into `src/`.

