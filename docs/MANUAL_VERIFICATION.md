# Manual & Automated Verification Guide — On-Demand Symbols

This document provides instructions for running the automated Experimental instance integration tests as well as step-by-step manual verification in Visual Studio 2022 (17.14+) and Visual Studio 2026.

---

## 0. Automated Experimental Instance Integration Tests

An end-to-end automated test runner (`tests/VSOnDemandSymbols.IntegrationTests`) boots `devenv.exe /rootsuffix Exp`, deploys the latest built VSIX binaries into the `Exp` hive, attaches via the Windows Running Object Table (ROT), and runs 5 end-to-end scenarios against the `tests/Fixtures/MiniUnreal` native C++ fixture (~20 seconds total):

1. `Test01_SolutionOpen_DetectsUnrealSolutionAndCreatesBackup`
2. `Test02_PreLaunchBreakpoint_LoadsSymbolsOnlyForBreakpointModule`
3. `Test03_CrashInUnsymbolizedModule_LoadsSymbolsAndSetsContextAndOpensSourceFile`
4. `Test04_AddBreakpointWhileRunning_LoadsSymbolsOnDemandAndBindsBreakpoint`
5. `Test05_SolutionCloseAndCrashRecovery_RestoresOriginalSymbolSettings`

Run from the repository root:

```powershell
# Run all 5 fast MiniUnreal integration tests (~12s total)
tests\VSOnDemandSymbols.IntegrationTests\bin\Debug\net472\VSOnDemandSymbols.IntegrationTests.exe

# Run the full Unreal Engine 5 + Lyra Starter Game Cold Boot vs. Warm Boot profiling suite (~32s total;
# launches UnrealEditor-Win64-DebugGame-Cmd.exe headlessly across ~1,060 DLLs via a zero-write %TEMP% harness)
tests\VSOnDemandSymbols.IntegrationTests\bin\Debug\net472\VSOnDemandSymbols.IntegrationTests.exe --profile-lyra

# Run both MiniUnreal integration tests and the Lyra profiling suite
tests\VSOnDemandSymbols.IntegrationTests\bin\Debug\net472\VSOnDemandSymbols.IntegrationTests.exe --all

# Optionally override the Unreal Engine 5 root directory (also configurable via
# $env:VSONDEMANDSYMBOLS_UE_ROOT or tests/VSOnDemandSymbols.IntegrationTests/testsettings.local.json)
tests\VSOnDemandSymbols.IntegrationTests\bin\Debug\net472\VSOnDemandSymbols.IntegrationTests.exe --profile-lyra --ue-root "D:\UnrealEngine"
```

---

## 1. Setup & Launching the Experimental Instance Manually

1. Open `VSOnDemandSymbols.slnx` in Visual Studio.
2. Ensure the configuration is **Debug | Any CPU** (or run the build via MSBuild).
3. Press **F5** to launch the Visual Studio Experimental instance (`devenv.exe /rootsuffix Exp`).

---

## 2. Test Cases

### Test Case 1: Unreal Engine Solution Detection (No Settings Change on Open)
1. In the Experimental instance, open an Unreal Engine solution (e.g. `D:\UnrealEngine\Samples\Games\Lyra\Lyra.slnx` or any UE5 project containing a `*.uproject`).
2. Open **View > Output** and select **On-Demand Symbols** from the *Show output from* dropdown.
3. **Verify:**
   - The Output pane logs:
     ```
     [Info] Unreal solution detected: '<SolutionName>.slnx'. On-Demand Symbols active.
     ```
   - **Tools > Options > Debugging > Symbols** is unchanged from your own settings (ADR-15: settings are applied only at launch).
   - No `baseline.json` and no `leases\` entries exist under `%LOCALAPPDATA%\OnDemandSymbols\<VS_HIVE>\`.
4. Press **F5**, then check the symbol settings while the editor is running:
   - "Load only specified modules" is selected (`IsManualLoad = true`) and "Always load symbols located next to modules" is unchecked.
   - Your pre-existing include list entries are preserved, and `UnrealEditor*.exe` has been added.
   - `baseline.json` (`State: Active`) and `leases\<devenv PID>.json` (`Kind: Unreal`) exist.

---

### Test Case 2: Breakpoint Resolution & Launch (F5)
1. In the Unreal solution, open a source file in a game or plugin module (e.g. `LyraGameplayAbility.cpp` or `MyActor.cpp`).
2. Set a breakpoint on an executable line inside a method.
3. Press **F5** to launch the editor.
4. **Verify:**
   - In the Output pane:
     ```
     [Info] IncludeList updated (before launch (active breakpoints: 1)): 2 wanted binaries + <N> user entries.
     ```
   - Candidate wildcard for the module (`UnrealEditor-<Module>*.dll`) was merged into `IncludeList` (no redundant configuration-specific entries).
   - The editor boots quickly because Visual Studio loads symbols **only** for the candidate binaries in `IncludeList`.
   - Once the module loads, the breakpoint glyph immediately turns solid red with a checkmark ("Breakpoint bound").

---

### Test Case 3: Call Stack On-Demand Symbol Loading
1. While the editor is running under the debugger, cause execution to break (hit your breakpoint, trigger an exception, or select **Debug > Break All**).
2. Look at the **Debug > Windows > Call Stack** window.
3. If un-symbolized modules are on the breaking thread's stack frames:
   - **Verify:**
     - The Output pane logs:
       ```
       [Info] Loading symbols on demand: <ModuleName>.dll — CallStack
       ```
     - Visual Studio automatically loads symbols for those frames, resolving file names and source line numbers.

---

### Test Case 4: Clean IDE UI (Transparent Background Operation)
1. Inspect the Visual Studio menus (**Debug**, **Tools**, etc.).
2. **Verify:**
   - No unnecessary menu items, toolbars, or sub-menus are added (clean IDE experience per ADR-10).
   - All extension status messages and diagnostic logs appear exclusively in the dedicated **Output** window under the "On-Demand Symbols" pane.

---

### Test Case 5: Non-Unreal Solution Inactivity
1. Open any standard non-Unreal C++ solution.
2. **Verify:**
   - The extension detects that the solution does not contain a `*.uproject` or `Engine/Build/Build.version`.
   - The extension remains completely inactive.
   - User symbol settings in **Tools > Options > Debugging > Symbols** remain untouched.
   - No `baseline.json` or `leases\` entries are created.

---

### Test Case 6: Clean Restore on Session End and Solution Close
1. Open an Unreal solution, press **F5**, then stop debugging (**Shift+F5**).
2. **Verify:**
   - The Output pane logs `[Info] Restored original symbol settings.`
   - **Tools > Options > Debugging > Symbols** shows your original settings and IncludeList (**Golden Rule #1**).
   - `baseline.json` is `Released` and `leases\` is empty.
3. Start debugging again, then close the solution via **File > Close Solution** while the session is still running (VS stops debugging first). Verify the same result.

---

### Test Case 7: Crash Recovery
1. Open an Unreal solution and press **F5** so a lease and an active baseline exist.
2. Force-kill the Visual Studio process (`devenv.exe`) via Task Manager or `taskkill /F /PID <pid>`.
3. Re-launch the Experimental instance.
4. **Verify:**
   - On package load, `Reconcile(null)` prunes the dead lease (PID no longer alive or start time mismatch).
   - The Output pane logs:
     ```
     [Warning] Pruned lease from exited process <pid>; restored original symbol settings.
     ```
   - Symbol settings are restored to their pre-crash original state and the baseline is `Released`.
5. Repeat, but kill `devenv.exe` while stopped at a breakpoint inside a `LocalOnly` load pass (lease has `ServerMaskActive: true`). Verify that symbol server flags and `SymbolPaths` are restored too.

---

## 3. Multi-Instance Test Cases (ADR-16)

Use two instances of the **same** hive (for example two `devenv.exe /rootsuffix Exp` windows). "A" opens an Unreal solution; "B" opens a non-Unreal C++ (or C#) solution, unless stated otherwise. Before each case, confirm both instances show your original symbol settings.

### Test Case 10: B Starts While A Is Idle
1. A opens the Unreal solution (no debugging). Start B.
2. **Verify:** B shows your original settings. No baseline or lease files exist.

### Test Case 11: B Starts and Debugs While A Is Debugging
1. A presses **F5** (editor running). Start B, then press **F5** in B with a breakpoint in B's code.
2. **Verify:**
   - B's Output pane logs that it wrote a `Baseline` lease and restored the baseline.
   - B's breakpoint binds and B's modules load symbols per your original settings.
   - A keeps debugging. Breakpoints added in A still bind (ADR-04 path). A logs a single warning about P1.
3. Stop B's session. **Verify:** A's manual mode is written back (the only remaining lease is `Unreal`).
4. Stop A's session. **Verify:** the original settings are restored and the baseline is `Released`.

### Test Case 12: Stale In-Memory Repair After A Has Finished
1. A presses **F5**. Start B while A is debugging. Do not debug in B.
2. Stop A's session (baseline released). Then press **F5** in B.
3. **Verify:** B's launch hook detects that B started inside a recorded dirty window, writes the baseline once, and B debugs with your original settings.

### Test Case 13: Two Unreal Instances
1. A opens Lyra, B opens a second Unreal project (or a second copy of Lyra). Set breakpoints in different modules in each.
2. Press **F5** in A, then in B.
3. **Verify:**
   - `baseline.json` was captured once (by A). Its `IncludeList` equals your original list, **not** A's modified list.
   - The live `IncludeList` contains both instances' wanted patterns. Breakpoints bind in both.
4. Stop A first, then B (then repeat stopping in the opposite order). **Verify:** after the first stop the other instance's wanted set remains; after the second stop the original settings are restored.

### Test Case 14: Out-of-Order Exit and Crash
1. A and B both debugging Unreal. Kill A's `devenv.exe`.
2. **Verify:** On B's next reconcile (session end, breakpoint add, or solution close), A's lease is pruned. When B finishes, the original settings are restored.
3. Kill both instances while debugging, then start a fresh instance. **Verify:** package load restores the original settings.

---

### Test Case 8: Adding Breakpoint While Process is Running (ADR-04)
1. Launch the Unreal Editor under the debugger (F5). Ensure the process is actively running (not in a break state).
2. Open a source file belonging to an Unreal module that does not yet have symbols loaded (e.g. `MyGameMode.cpp`).
3. Set a new breakpoint on an executable line (press **F9** or click in the editor margin).
4. **Verify:**
   - The Output pane logs:
     ```
     [Info] IncludeList updated (breakpoint bind error (active breakpoints: <N>)): <M> wanted binaries + <K> user entries.
     [Info] Loading symbols on demand: UnrealEditor-<ModuleName>.dll — Breakpoint
     ```
   - Visual Studio loads symbols for the module immediately while the process continues running.
   - The breakpoint glyph binds and changes from hollow to a solid red circle.
   - When execution reaches that line, the breakpoint hits successfully.

---

### Test Case 9: Activating Disabled Breakpoint While Process is Running (ADR-03)
1. Before starting debugging, set a breakpoint in a module and **disable** it (right-click glyph > Disable Breakpoint, or uncheck it in the Breakpoints window).
2. Press **F5** to launch the editor.
3. **Verify at startup:**
   - The Output pane logs:
     ```
     [Info] IncludeList updated (before launch (breakpoints: 1)): 2 wanted binaries + <N> user entries.
     ```
   - Candidate wildcard for the module (`UnrealEditor-<ModuleName>*.dll`) was included in `IncludeList` even though the breakpoint was disabled.
   - Visual Studio loads symbols for the module during startup.
4. While the editor is running (not in a break state), **enable** the breakpoint (check the box in the Breakpoints window or right-click glyph > Enable Breakpoint).
5. **Verify:**
   - Because symbols are already loaded in the process, the breakpoint activates and binds immediately without any delay (glyph becomes solid red).
   - When execution reaches the line, the breakpoint hits successfully.