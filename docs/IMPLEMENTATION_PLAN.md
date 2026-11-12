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

## Phase 1: Core State, Policy & Coordination Store (`VSOnDemandSymbols.Core`)

All coordination logic stays VS-free in `netstandard2.0` so every interleaving can be deterministically unit-tested.

- [x] **1.1 Process abstraction (`Abstractions/IProcessInfo.cs` & `SystemProcessInfo.cs`):**
  - `int CurrentProcessId { get; }`
  - `DateTime CurrentProcessStartTimeUtc { get; }`
  - `bool IsDevenvProcessAlive(int pid, DateTime expectedStartTimeUtc)` — verifies PID exists, process name is `devenv`, and `StartTime.ToUniversalTime()` matches within ±2 seconds (guards against OS PID reuse).
- [x] **1.2 Snapshot & Coordination DTOs (`State/SymbolSettingsSnapshot.cs`, `State/CoordinationDtos.cs`):**
  - `SymbolSettingsSnapshot`: `IsManualLoad`, `IsLoadAdjacent`, `AutomaticSymbolLoad`, `IncludeList`, `UseMsSymbolServers`, `UseNugetSymbolServers`, `SymbolPaths`, plus structural equality helpers for managed fields vs. server fields.
  - `BaselineDto`: `State` (`Active` | `Released`), `ActiveSinceUtc`, `ReleasedUtc`, `Snapshot`.
  - `SessionLeaseDto`: `ProcessId`, `ProcessStartTimeUtc`, `Kind` (`Unreal` | `Baseline`), `WantedBinaries`, `ServerMaskActive`, `UpdatedUtc`.
  - `DirtyWindowDto` / `DirtyWindowLogDto`: last 20 `[ActiveSinceUtc, ReleasedUtc)` intervals with `RestoredBaseline`.
- [x] **1.3 `CoordinationStore` (`State/CoordinationStore.cs`):**
  - Resolves `%LOCALAPPDATA%/OnDemandSymbols/<VS_HIVE>/` (`baseline.json`, `leases/<pid>.json`, `dirty-windows.json`).
  - Atomic writes (`WriteAllText` temp file + `ReplaceFile`).
  - `MigrateLegacyBackupIfNeeded(...)`: converts pre-ADR-16 `symbol-settings-backup.json` into an `Active` `baseline.json` and deletes the legacy file.
  - `PruneDeadLeases(...)`: deletes leases whose process is dead; returns whether any pruned lease had `ServerMaskActive == true` (crash during break-mode masking).
  - `AppendDirtyWindow(...)` / `HasOverlappingDirtyWindow(DateTime processStartTimeUtc)`.
- [x] **1.4 `EffectiveSettingsPolicy` (`State/EffectiveSettingsPolicy.cs`):**
  - Pure function `Compute(BaselineDto baseline, IReadOnlyList<SessionLeaseDto> liveLeases, bool recoverServerSettings)`.
  - Encodes:
    - No live leases $\to$ restore `baseline` managed fields (and server fields if `recoverServerSettings`), mark `ShouldReleaseBaseline = true`.
    - $\ge 1$ `Baseline` lease $\to$ force `baseline` managed fields (Priority rule P1: correctness beats speed).
    - Only `Unreal` leases $\to$ `IsManualLoad = true`, `IsLoadAdjacent = false`, `AutomaticSymbolLoad = false`, `IncludeList = baseline.IncludeList ∪ union(all live WantedBinaries)` (Priority rule P2).
- [x] **1.5 Unit Tests (`VSOnDemandSymbols.Core.Tests`):**
  - `EffectiveSettingsPolicyTests`: empty leases, single Unreal, two Unreal (merged IncludeList), Unreal + Baseline (Baseline wins), server recovery flag.
  - `CoordinationStoreTests`: atomic round-trips, dead-lease pruning, PID reuse mismatch, `ServerMaskActive` crash detection, dirty-window capping (20 entries) and overlap queries, legacy `symbol-settings-backup.json` migration.
  - `SymbolSettingsSnapshotTests`: cloning and equality checking.

---

## Phase 2: Hive Lock & `SymbolSettingsManager` Refactor (`VSOnDemandSymbols`)

- [ ] **2.1 `HiveLock` (`Debugger/HiveLock.cs`):**
  - Wraps named `Mutex` `Local\VSOnDemandSymbols.<VS_HIVE>` with a 2,000 ms timeout.
  - Treats `AbandonedMutexException` as acquired (previous holder crashed).
  - Returns `null` / `false` on timeout so callers fail safe without blocking debug launch (P3).
- [ ] **2.2 Refactor `SymbolSettingsManager` (`Debugger/SymbolSettingsManager.cs`):**
  - Replace `ApplyManualMode`, `RestoreOriginalSettings`, and `RecoverFromCrashIfNeeded` with `Reconcile(SessionLeaseRequest? selfLease, bool forceBaselineWrite = false)`.
  - Inside `Reconcile` (under `HiveLock`):
    1. Migrate legacy backup; prune dead leases.
    2. If `selfLease != null` and no `Active` baseline exists, capture baseline from `GetCurrentSymbolSettings()` and open a dirty window.
    3. Write/update or delete `leases/<selfPid>.json`.
    4. Compute effective settings via `EffectiveSettingsPolicy.Compute(...)`.
    5. Always re-read `GetCurrentSymbolSettings()` (no cached `_isApplied` short-circuit) and call `SaveSymbolSettings(updated, loadSymbolsNow: false)` only if managed (or recovery) fields differ, or if `forceBaselineWrite` is set for in-memory repair (R5).
    6. If `ShouldReleaseBaseline`: read back `GetCurrentSymbolSettings()` to verify the baseline was written; only then mark `baseline.json` `Released` and close the dirty window.
  - Update `ExecuteWithCandidateModules(...)` to set/clear `ServerMaskActive` on this instance's lease under `HiveLock`.
  - Expose `NeedsDirtyWindowRepair()` (checks `dirty-windows.json` against current process start time, tracked via a per-process `_repairedDirtyWindow` flag).

---

## Phase 3: Launch Hook, Session Monitor & Package Lifecycle (`VSOnDemandSymbols`)

- [ ] **3.1 `SymbolSettingsLaunchHook` (`Debugger/SymbolSettingsLaunchHook.cs`):**
  - Implements `IVsDebugLaunchHook` (`OnLaunchDebugTargets`, `SetNextHook`, `IsProcessRecycleRequired`) and registers via `[ProvideObject]` / `.pkgdef` under `Debugger\LaunchHooks110`.
  - If Unreal solution is open and extension is enabled: resolves breakpoints via `DebuggerSessionMonitor.ResolveWantedBinariesFromBreakpoints()` and calls `Reconcile(Unreal lease)`.
  - Otherwise: if `baseline` is `Active` or `NeedsDirtyWindowRepair()` is true, calls `Reconcile(Baseline lease, forceBaselineWrite: true)`; else zero-touch.
  - Always forwards to `_nextHook?.OnLaunchDebugTargets(...)` inside `try/catch` (Golden Rule #2).
- [ ] **3.2 Update `DebuggerSessionMonitor` (`Debugger/DebuggerSessionMonitor.cs`):**
  - Expose `ResolveWantedBinariesFromBreakpoints()`.
  - Remove `CommandEvents.BeforeExecute (cmdidStart)` subscription (unless retained as S2 fallback).
  - Update `OnBreakpointBindError` to call `Reconcile(Unreal lease)` with the refreshed wanted binaries.
  - Update `OnEnterDesignMode` to release this instance's lease via `Reconcile(null)`.
  - Log a single warning per session if an Unreal session detects its manual mode was overridden by a `Baseline` lease (P1).
- [ ] **3.3 Update `VSOnDemandSymbolsPackage` (`VSOnDemandSymbolsPackage.cs`):**
  - Remove `_sessionMonitor?.UpdateIncludeListFromBreakpoints("solution opened")` from `EvaluateOpenSolution()`. Detection only sets `IsEnabled = true`.
  - In `InitializeAsync`: call `Reconcile(null)` (crash recovery / dead-lease prune).
  - In `HandleSolutionClosed` and `Dispose`: call `Reconcile(null)`.

---

## Phase 4: Integration Tests & Verification

- [ ] **4.1 Update existing `MiniUnreal` integration tests (`tests/VSOnDemandSymbols.IntegrationTests`):**
  - Update `VisualStudioExpFixture` to inspect `baseline.json` and `leases/<pid>.json` instead of `symbol-settings-backup.json`.
  - Update `Test01`: verify opening an Unreal solution does **not** modify symbol settings or create a baseline/lease until launch.
  - Update `Test05`: verify stopping debugging restores settings and releases the baseline, and force-killing `devenv.exe` mid-session recovers via dead-lease pruning on next startup.
- [ ] **4.2 Add multi-instance automated integration tests:**
  - Spawn two `devenv.exe /rootsuffix Exp` processes in the same hive and attach to both via ROT (`!VisualStudio.DTE.*:<PID>`).
  - Cover:
    - Non-Unreal B launches while Unreal A is idle (zero-touch).
    - Non-Unreal B launches & debugs while Unreal A is debugging (P1: B gets baseline, A keeps running, stopping B restores A's manual mode, stopping A releases baseline).
    - Stale in-memory repair (B started while A was debugging, A stops, B launches and repairs itself).
    - Two Unreal instances (baseline preserved, `IncludeList` merged, out-of-order exits).
    - Crash of one instance while another is alive.
- [ ] **4.3 Full Verification Pass:**
  - MSBuild build with `/p:UseSharedCompilation=false /nr:false` (zero new warnings).
  - `dotnet run --project tests/VSOnDemandSymbols.Core.Tests --no-build` (all unit tests pass).
  - `VSOnDemandSymbols.IntegrationTests.exe --all` (all `MiniUnreal`, multi-instance, and `--profile-lyra` tests pass).
