# Dimension 6 Analysis: Verification, Testing, and Safe Tooling

> **Scope:** Evolution of build, test, and verification practices across all **50 development sessions** (`S01–S49`, `S51` in `chat_history/`), focusing on sandbox-safe tooling (`S03–S10`), unit-test rationalization (`S35`), out-of-proc ROT/DTE Experimental-instance integration (`S45–S47`), and telemetry-driven profiling (`S49`).  
> **Goal:** Document how verification evolved from manual F5 round-trips to automated, empirical validation of **1,062 UE5 DLLs**—and extract reusable testing rules for future sandboxed AI projects.

---

## 1. Executive Summary

- **The Core Constraint:** An AI running inside a Windows sandbox cannot spawn Visual Studio's Experimental instance (`devenv.exe /rootsuffix Exp`) with `%LOCALAPPDATA%` / ROT COM access, nor can default MSBuild/VSTest named pipes (`VBCSCompiler.exe`, `/nr:true`) run without hanging.
- **Two-Tier Solution:**
  1. **Fast Sandbox-Safe Core (`VSOnDemandSymbols.Core.Tests`):** Pure logic in `netstandard2.0` + `/p:UseSharedCompilation=false /nr:false` + `dotnet run --no-build` (bypassing VSTest's named-pipe host).
  2. **Out-of-Sandbox ROT/DTE Integration (`VSOnDemandSymbols.IntegrationTests.exe`, `S45–S47`, \$10.10 total):** Running `devenv.exe /rootsuffix Exp` out-of-proc via Windows ROT COM with `BypassSandbox: true`, testing both a synthetic `MiniUnreal` fixture (`ADR-12`) and real UE5 **Lyra** (`--profile-lyra`, `ADR-14`).
- **Real Bugs Caught Only by Integration Testing:** Running `--profile-lyra` against **1,062 real DLLs** immediately exposed the modal *"Browse for PDB"* freeze (`ADR-13`) and quantified **29.6%–61.5% launch speedups** (**11 / 1,062** PDBs loaded vs. **950 / 1,062** baseline).

---

## 2. Verification Evolution Across Four Stages

| Stage | Sessions | Cost | Mechanism | Limitation / Breakthrough |
|---|---|---:|---|---|
| **1. Sandbox-Safe Build & Core Tests** | `S03–S10`, `S35` | \$0.31 *(S35)* | `msbuild /p:UseSharedCompilation=false /nr:false` + `dotnet run --no-build` | Eliminated MSBuild hangs (`fb519e7`); `S35` pruned dead unit tests (`4b1db45`) & `NativeSample` (`-214` lines) |
| **2. Manual F5 Pilot Loop** | `S10–S44` | — | Pilot presses F5 in VS, reproduces in Lyra, pastes Output pane logs | Slow human-in-the-loop bottleneck for COM, Unified Settings, and break-mode bugs (`S19, S21, S39–S41`) |
| **3. Out-of-Proc `MiniUnreal` & `--profile-lyra`** | `S45–S47` | \$10.10 | `VisualStudioExpFixture` drives `devenv.exe /rootsuffix Exp` via ROT COM (`BypassSandbox: true`) | Autonomous end-to-end VSIX verification (`b5d02ad`, `14633aa`); caught `ADR-13` PDB modal freeze |
| **4. Structured `[Profile]` Telemetry** | `S49` | \$2.16 | `[Profile] BreakMode:` timing logs (`e99d833`) | Pinpointed `stackWalk=401ms [threads=121, frames=479]` → AD7 `FIF_MODULE` fix (`07b1a2a`, `ADR-02`) |

---

## 3. Case Studies in Safe Tooling & Testing

### 3.1 Solving Sandbox Build & Test Hangs (`S03–S10`, `S35`)
- **Problem:** Default `msbuild.exe` (`VBCSCompiler.exe` + `/nr:true`) and `dotnet test` (VSTest socket/pipe host) silently hung in the Windows sandbox.
- **Fix:** Codified `/p:UseSharedCompilation=false /nr:false` in [AGENTS.md](file:///C:/Dev/VSOnDemandSymbols/AGENTS.md) (`fb519e7`) and executed xUnit tests directly via `dotnet run --project tests/VSOnDemandSymbols.Core.Tests --no-build` (`Program.cs` console runner).
- **Rationalization (`S35`, \$0.31):** Audited the unit test suite (`4b1db45`: **+206 / -116 lines**), removing low-value tests and deleting the unused `samples/NativeSample` fixture (`91ce59f`: **-214 lines**).

### 3.2 Out-of-Proc ROT/DTE Automation (`S45–S47`, \$10.10)
- **`S45` (`$1.67`, 50 turns):** Proved an agent can launch `devenv.exe /rootsuffix Exp`, attach via the Windows Running Object Table (ROT), set breakpoints via DTE, and assert Output pane logs (`MiniUnreal` suite, `b5d02ad`: **+1,503 lines**, `ADR-12`).
- **`S46` (`$7.48`, 152 turns, 1 compaction):** Extended the harness to real UE5 Lyra (`--profile-lyra`, `14633aa`: **+878 lines**, `ADR-14`), comparing **Extension Disabled** vs. **Cold Boot** vs. **Warm Boot** across **1,062 DLLs**:
  - **Discovery:** Triggered and fixed the modal *"Browse for PDB"* Win32 dialog hang by adding the local PDB existence guard (`ADR-13`).
  - **Measured Lyra Results (`S46` / `S47`):** Reduced startup PDBs from **950 / 1,062** (26+ GB) down to **11 / 1,062** (1,051 skipped), cutting launch-to-breakpoint time from **22,095 ms → 10,062 ms Cold (-54.5%) / 8,515 ms Warm (-61.5%)** in `S46` (and **14,203 ms → 9,992 ms Cold (-29.6%) / 8,082 ms Warm (-43.1%)** in `S47` with OS disk cache warm).
- **`S47` (`$0.95`, 26 turns):** Refactored `LyraProfilingTests` (`81a5ae6`: **+273 / -277 lines**), extracting shared helpers and keeping the suite self-contained.

### 3.3 Telemetry-Driven Performance Verification (`S49`, \$2.16)
- Because `[Profile] BreakMode:` telemetry (`e99d833`) logged exact phase timings (`stackWalk=401ms`, `moduleScan=2ms`, `symbolLoad=30ms`, `contextRefresh=36ms`), `S49` immediately isolated the DTE COM stack-walk bottleneck across 121 threads / 479 frames and verified the AD7 `EnumFrameInfo(FIF_MODULE)` replacement (`07b1a2a`).

---

## 4. Verification Rules for Future AI-Built Projects

1. **Split pure logic into a sandbox-testable `Core` library on Day 1.** Running `dotnet run --no-build` inside the sandbox gave sub-5-second feedback with zero host dependencies.
2. **Isolate `BypassSandbox: true` to a single pre-built test runner binary.** Keep builds, file edits, and unit tests 100% sandboxed; elevate only the out-of-proc host runner (`VSOnDemandSymbols.IntegrationTests.exe`).
3. **Never rely solely on synthetic fixtures (`MiniUnreal`) for performance or Win32 UI edge cases.** Running `--profile-lyra` against 1,062 real DLLs caught `ADR-13` (modal PDB dialog) that `MiniUnreal` never triggered.
4. **Instrument structured `[Profile]` logs before optimizing.** Exact millisecond breakdowns turned `S49` ("anything we can do to speed this up?") into a focused **\$2.16** win.

