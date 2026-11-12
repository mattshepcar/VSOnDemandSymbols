# Implementation Plan — Multi-Instance Coordination & Just-in-Time Launch Hook

**Goal:** Eliminate symbol-setting leaks between concurrent Visual Studio instances and handle any order of instances starting, debugging, crashing, and exiting cleanly (ADR-15, ADR-16).

**Authoritative References:**
- [SPEC.md](SPEC.md) (§2, §3, §4.1, §4.2, §5)
- [ARCHITECTURE.md](ARCHITECTURE.md) (§1, §2, §4.1, §4.4, §4.5)
- [DECISIONS.md](DECISIONS.md) (ADR-15, ADR-16)
- [MANUAL_VERIFICATION.md](MANUAL_VERIFICATION.md) (Test Cases 1, 6, 7, 10–14)

---

## Phase 0: Runtime Spikes (Verify VS Shell Behavior First)

Before refactoring the production classes, verify the two Visual Studio host assumptions in the Experimental instance (`devenv.exe /rootsuffix Exp`):

- [ ] **Spike S1 — `IVsDebugLaunchHook` registration & firing:**
  - Verify pkgdef registration under `$RootKey$\Debugger\LaunchHooks110` for `SymbolSettingsLaunchHook` (GUID + class).
  - Confirm `OnLaunchDebugTargets` fires synchronously on the UI thread *before* `CppDebug.dll` reads symbol settings across all launch paths (`F5`, `F11` Step Into, Run To Cursor, Restart, Start New Instance, Attach).
  - Confirm whether Visual Studio instantiates and invokes the launch hook in an instance where `VSOnDemandSymbolsPackage` has **not** yet auto-loaded (e.g., a pure C# solution without the C++ UI context rule).
- [ ] **Spike S2 — Fallback path (only if S1 reveals a gap):**
  - If the launch hook does not fire without package load, evaluate adding a lightweight background auto-load on `VSConstants.UICONTEXT.ShellInitialized_string` (with a fast guard so non-C++ instances do zero work unless a dirty baseline/window exists), or using `CommandEvents.BeforeExecute` on the full launch command set + `OnEnterRunMode` (`loadSymbolsNow: true`).

---

## Phase 1: Core State & Hive Coordinator (`VSOnDemandSymbols.Core`)

All coordination logic stays VS-free in `netstandard2.0` so every interleaving can be deterministically unit-tested.

- [x] **1.1 Snapshot & Coordination DTOs (`State/SymbolSettingsSnapshot.cs`, `State/CoordinationDtos.cs`):**
  - `SymbolSettingsSnapshot`: `IsManualLoad`, `IsLoadAdjacent`, `AutomaticSymbolLoad`, `IncludeList`, `UseMsSymbolServers`, `UseNugetSymbolServers`, `SymbolPaths`, plus structural equality helpers for managed fields vs. server fields.
  - `BaselineDto`: `ActiveSinceUtc`, `Snapshot`.
  - `SessionLeaseDto`: `ProcessId`, `WantedBinaries`, `ServerMaskActive`, `UpdatedUtc`.
- [x] **1.2 `HiveCoordinator` (`State/HiveCoordinator.cs`):**
  - Resolves `%LOCALAPPDATA%/OnDemandSymbols/<VS_HIVE>/` (`baseline.json`, `leases/<pid>.json`).
  - Kernel-enforced lease liveness: holds open `FileStream` on `leases/<pid>.json` (`FileShare.Read`) during active debug session.
  - Dead-lease pruning: attempts `File.Delete` on lease files. If it succeeds, the dead lease is pruned. If it throws `IOException` (sharing violation), the owner is alive. Detects if any pruned lease had `ServerMaskActive == true` (crash recovery).
  - Effective settings calculation:
    - If no live leases: restore baseline settings.
    - If $\ge 1$ live Unreal leases: `IsManualLoad = true`, `IsLoadAdjacent = false`, `AutomaticSymbolLoad = false`, `IncludeList = baseline.IncludeList ∪ union(all live WantedBinaries)`.
  - Legacy migration: converts pre-ADR-16 `symbol-settings-backup.json` to `baseline.json` and deletes legacy file.
- [x] **1.3 Unit Tests (`VSOnDemandSymbols.Core.Tests`):**
  - `HiveCoordinatorTests`: atomic baseline save/load/delete, file-handle locking and dead-lease pruning (sharing violation vs. deletion), wanted binaries union, server-mask crash detection, legacy migration.
  - `SymbolSettingsSnapshotTests`: cloning, equality checking.

---

## Phase 2: Hive Lock & `SymbolSettingsManager` Refactor (`VSOnDemandSymbols`)

- [x] **2.1 `HiveLock` (`Debugger/HiveLock.cs`):**
  - Wraps named `Mutex` `Local\VSOnDemandSymbols.<VS_HIVE>` with a 2,000 ms timeout.
  - Treats `AbandonedMutexException` as acquired (previous holder crashed).
  - Returns `null` / `false` on timeout so callers fail safe without blocking debug launch (P3).
- [x] **2.2 Refactor `SymbolSettingsManager` (`Debugger/SymbolSettingsManager.cs`):**
  - Replace `ApplyManualMode`, `RestoreOriginalSettings`, and `RecoverFromCrashIfNeeded` with `Reconcile(IEnumerable<string>? wantedBinaries)`.
  - Inside `Reconcile` (under `HiveLock`):
    1. Migrate legacy backup; prune dead leases via try-delete.
    2. If `wantedBinaries != null`:
       - If no `baseline.json` exists, capture baseline from `GetCurrentSymbolSettings()`.
       - Acquire/update own lease file handle (`FileShare.Read`) for `leases/<selfPid>.json`.
    3. If `wantedBinaries == null`:
       - Release/delete own lease file handle.
    4. Compute effective settings and apply to Visual Studio only if changed (`loadSymbolsNow: false`).
    5. If no live leases remain and `baseline.json` exists:
       - Restore baseline settings to Visual Studio.
       - Delete `baseline.json`.
  - Update `ExecuteWithCandidateModules(...)` to set/clear `ServerMaskActive` on this instance's lease under `HiveLock`.

---

## Phase 3: Launch Hook, Session Monitor & Package Lifecycle (`VSOnDemandSymbols`)

- [x] **3.1 `SymbolSettingsLaunchHook` (`Debugger/SymbolSettingsLaunchHook.cs`):**
  - Implements `IVsDebugLaunchHook` (`OnLaunchDebugTargets`, `SetNextHook`, `IsProcessRecycleRequired`) and registers via `[ProvideObject]` / `.pkgdef` under `Debugger\LaunchHooks110`.
  - If Unreal solution is open and extension is enabled: resolves breakpoints via `DebuggerSessionMonitor.ResolveWantedBinariesFromBreakpoints()` and calls `Reconcile(wantedBinaries)`.
  - Otherwise (non-Unreal launch): if `baseline.json` exists, calls `Reconcile(null)` to restore baseline settings before launch proceeds (Rule P1: correctness beats speed); else zero-touch.
  - Always forwards to `_nextHook?.OnLaunchDebugTargets(...)` inside `try/catch` (Golden Rule #2).
- [x] **3.2 Update `DebuggerSessionMonitor` (`Debugger/DebuggerSessionMonitor.cs`):**
  - Expose `ResolveWantedBinariesFromBreakpoints()`.
  - Remove `CommandEvents.BeforeExecute (cmdidStart)` subscription (unless retained as S2 fallback).
  - Update `OnBreakpointBindError` to call `Reconcile(refreshedWantedBinaries)`.
  - Update `OnEnterDesignMode` to release this instance's lease via `Reconcile(null)`.
- [x] **3.3 Update `VSOnDemandSymbolsPackage` (`VSOnDemandSymbolsPackage.cs`):**
  - Remove `_sessionMonitor?.UpdateIncludeListFromBreakpoints("solution opened")` from `EvaluateOpenSolution()`. Detection only sets `IsEnabled = true`.
  - In `InitializeAsync`: call `Reconcile(null)` (crash recovery / dead-lease prune).
  - In `HandleSolutionClosed` and `Dispose`: call `Reconcile(null)`.

---

## Phase 4: Integration Tests & Verification

- [x] **4.1 Update existing `MiniUnreal` integration tests (`tests/VSOnDemandSymbols.IntegrationTests`):**
  - Update `VisualStudioExpFixture` to inspect `baseline.json` and `leases/<pid>.json` instead of `symbol-settings-backup.json`.
  - Update `Test01`: verify opening an Unreal solution does **not** modify symbol settings or create a baseline/lease until launch.
  - Update `Test05`: verify stopping debugging restores settings and releases the baseline, and force-killing `devenv.exe` mid-session recovers via dead-lease pruning on next startup.
- [x] **4.2 Add multi-instance automated integration tests:**
  - Spawn two `devenv.exe /rootsuffix Exp` processes in the same hive and attach to both via ROT (`!VisualStudio.DTE.*:<PID>`).
  - Cover:
    - Non-Unreal B launches while Unreal A is idle (zero-touch).
    - Non-Unreal B launches & debugs while Unreal A is debugging (P1: B gets baseline, A keeps running, stopping B restores A's manual mode, stopping A releases baseline).
    - Stale in-memory repair (B started while A was debugging, A stops, B launches and repairs itself).
    - Two Unreal instances (baseline preserved, `IncludeList` merged, out-of-order exits).
    - Crash of one instance while another is alive.
- [x] **4.3 Full Verification Pass:**
  - MSBuild build with `/p:UseSharedCompilation=false /nr:false` (zero new warnings).
  - `dotnet run --project tests/VSOnDemandSymbols.Core.Tests --no-build` (all 118 unit tests pass).
  - `VSOnDemandSymbols.IntegrationTests.exe` (all 5 `MiniUnreal` and 4 multi-instance integration tests pass).
