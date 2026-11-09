# AGENTS.md — Guide for AI coding assistants

## What this project is

**On-Demand Symbols** is a lightweight Visual Studio extension (VSIX) that speeds up debugging large native C++ applications, specifically the **Unreal Engine 5 editor** (ballpark ~1,000 DLLs). It puts the debugger into "load only specified modules" mode, then loads symbols automatically for exactly the modules that are needed:
- modules with breakpoints (populated into the debugger's `IncludeList` before launch so breakpoints bind immediately)
- modules on the call stack when execution breaks (loaded on demand via DTE)

**Read [docs/SPEC.md](docs/SPEC.md) and [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) before writing any code.** They are the source of truth for the system architecture.

## Documentation map

| Doc | Use it for |
|---|---|
| [docs/SPEC.md](docs/SPEC.md) | **Primary specification** for the extension |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | **System architecture**, component responsibilities, and **learnings on why Concord, MSBuild, and MRU were eliminated** |
| [docs/DECISIONS.md](docs/DECISIONS.md) | **Architecture Decision Records (ADRs)** and key design rationale |
| [docs/MODULE_RESOLUTION.md](docs/MODULE_RESOLUTION.md) | **Unreal module naming rules** and confirmed UBT source citations |
| [docs/MANUAL_VERIFICATION.md](docs/MANUAL_VERIFICATION.md) | **Step-by-step test scenarios** for manual verification in the Experimental instance |

## Current status

- The Unreal Engine source is at `D:\UnrealEngine`. It is read-only reference material outside the workspace.

## Golden rules

1. **Never leave the user's VS symbol settings modified.** All writes go through `SymbolSettingsManager`, which backs up first and restores on solution close, extension disable, VS exit, or startup crash recovery (SPEC §5). The manual-load flag (`IsManualLoad`), the load-adjacent flag (`IsLoadAdjacent`), the automatic symbol load flag (`AutomaticSymbolLoad`), and the `IncludeList` are managed dynamically by the extension. The user's original `IncludeList` is preserved and merged with modules containing breakpoints (D-17, D-18, D-20). **Never** touch symbol paths, servers, the cache, or the exclude list.
2. **Never break the debug session.** Every debugger event handler is wrapped in try/catch and logs failures.
3. **Zero module-load overhead.** Visual Studio's native debug engine checks `IncludeList` directly at module map time. No file I/O, no LINQ allocations, and no extension code runs on the module mapping path.
4. **Core stays VS-free.** Logic that can be tested without VS (Unreal module resolution, naming, path sanitization, backup store) lives in `VSOnDemandSymbols.Core` (netstandard2.0) with unit tests. The VSIX project is a thin adapter.
5. **Spec first.** If behaviour needs to differ from SPEC.md, update SPEC.md (and add a DECISIONS.md entry) in the same change, or ask the user. Don't silently diverge.
6. **Don't guess Unreal naming rules.** If something isn't confirmed in MODULE_RESOLUTION.md §7, check the UBT source or ask.

## Tech stack and conventions

- C# (LangVersion 14, `Nullable` enabled), .NET Framework 4.7.2 for the VSIX; netstandard2.0 for Core; xUnit for tests.
- SDK-style VSIX project using `Microsoft.VisualStudio.SDK` 17.14+ and `Microsoft.VSSDK.BuildTools`. `InstallationTarget` is `[17.14,)`, so **only use APIs available in VS 2022 17.14**, unless guarded at runtime.
- `AsyncPackage` with `AllowsBackgroundLoading = true`. Auto-load through a UI context rule for solutions containing C++ projects (`ProvideUIContextRule`).
- Threading: follow the VS threading rules (Microsoft.VisualStudio.Threading analyzers).
  - `await JoinableTaskFactory.SwitchToMainThreadAsync()` before any COM/DTE call; `ThreadHelper.ThrowIfNotOnUIThread()` at the top of UI-thread-only methods.
  - No `.Result`, `.Wait()` or `Task.Run(...).GetAwaiter().GetResult()` on the UI thread.
  - Use `JoinableTaskFactory.RunAsync` for fire-and-forget work. Always observe exceptions.
- JSON: Newtonsoft.Json (ships with VS; reference it without bundling it in the VSIX).
- UI: Zero UI clutter. Transparent background operation with status and diagnostics reported to the "On-Demand Symbols" Output pane.
- Logging: use the `ILogger` abstraction → "On-Demand Symbols" output pane.
- Style: see `.editorconfig`. File-scoped namespaces, `var` when the type is obvious, `_camelCase` private fields, and `Async` suffix on async methods.

## Build, test, debug

Run from the repo root in PowerShell:

```powershell
# Locate MSBuild (VSIX projects need VS's MSBuild, not just the dotnet CLI)
$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -prerelease -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1

# Build everything (sandbox-safe: no VBCSCompiler named pipes, no MSBuild node reuse)
& $msbuild VSOnDemandSymbols.slnx /p:Configuration=Debug /p:UseSharedCompilation=false /nr:false /v:minimal

# Unit tests (Core only — no VS needed; --no-build reuses the MSBuild output above)
dotnet run --project tests/VSOnDemandSymbols.Core.Tests --no-build

# Automated Experimental instance integration tests (boots devenv.exe /rootsuffix Exp via ROT COM;
# must run with BypassSandbox: true so devenv.exe can access %LOCALAPPDATA% and the Windows ROT)
tests\VSOnDemandSymbols.IntegrationTests\bin\Debug\net472\VSOnDemandSymbols.IntegrationTests.exe
```

- **Preventing sandbox build hangs (important for agents):**
  - Always pass `/p:UseSharedCompilation=false /nr:false` (and avoid `/m`) when running MSBuild in the sandbox. Roslyn's `VBCSCompiler.exe` compiler server and MSBuild worker node reuse (`/nr:true`) rely on named pipes that hang indefinitely inside the Windows sandbox. (`<UseSharedCompilation>false</UseSharedCompilation>` is also set in the `.csproj` files as a safeguard.)
  - Omit `/restore` on normal builds once packages are restored; only pass `/restore` when adding or changing NuGet packages (which requires network access).
  - Run unit tests via `dotnet run --project tests/VSOnDemandSymbols.Core.Tests --no-build` rather than `dotnet test`, because the VSTest host uses local sockets/named pipes that can be blocked by the sandbox.
- Interactive debugging: F5 in VS launches the **Experimental instance** (`devenv.exe /rootsuffix Exp`). Agents can't drive that UI. Follow the manual test steps in [docs/MANUAL_VERIFICATION.md](docs/MANUAL_VERIFICATION.md).

## Definition of done (per change)

- Builds with no new warnings; unit tests pass.
- Behaviour matches SPEC.md (or the spec has been updated, with a decision entry).
- No UI-thread blocking, no unguarded event handlers, no change to symbol settings outside `SymbolSettingsManager`.
- Docs and the progress tracker are updated.
- **Git commits**:
  - Always commit at the completion of a logical step (e.g. Step 6.1), bug fix, or milestone. Committing on every single turn is not required for intermediate tweaks, but logical units of work must be committed before marking them complete.
  - If unsure whether a change is ready to commit, prompt the user: *"Should I commit that now?"* Never leave completed work uncommitted without asking.
  - Commit messages should be clear and descriptive (e.g., `feat(...)`, `fix(...)`, `refactor(...)`).

## Don'ts

- Don't modify files under `D:\UnrealEngine`. It's read-only reference material.
- Don't add telemetry or network calls.
- Don't bundle VS-provided assemblies (Newtonsoft.Json, VS SDK assemblies) into the VSIX.
- Don't use `DTE.ExecuteCommand` to drive the Modules window as a way to load symbols. Use the debugger APIs.
- Don't commit `bin/`, `obj/`, `.vs/`, or spike code into `src/`.
