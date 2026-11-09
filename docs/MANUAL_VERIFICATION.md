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
# Run all 5 integration tests (or pass a substring filter, e.g. Test03)
tests\VSOnDemandSymbols.IntegrationTests\bin\Debug\net472\VSOnDemandSymbols.IntegrationTests.exe
```

---

## 1. Setup & Launching the Experimental Instance Manually

1. Open `VSOnDemandSymbols.slnx` in Visual Studio.
2. Ensure the configuration is **Debug | Any CPU** (or run the build via MSBuild).
3. Press **F5** to launch the Visual Studio Experimental instance (`devenv.exe /rootsuffix Exp`).

---

## 2. Test Cases

### Test Case 1: Unreal Engine Solution Detection & IncludeList Setup
1. In the Experimental instance, open an Unreal Engine solution (e.g. `D:\UnrealEngine\Samples\Games\Lyra\Lyra.slnx` or any UE5 project containing a `*.uproject`).
2. Open **View > Output** and select **On-Demand Symbols** from the *Show output from* dropdown.
3. **Verify:**
   - The Output pane logs:
     ```
     [Info] Unreal solution detected: '<SolutionName>.slnx'. On-Demand Symbols active.
     [Info] IncludeList updated (solution opened (active breakpoints: 0)): 1 wanted binaries + <N> user entries.
     ```
   - In **Tools > Options > Debugging > Symbols**:
     - "Load only specified modules" is selected (`IsManualLoad = true`).
     - "Always load symbols located next to modules" is unchecked (`IsLoadAdjacent = false`).
     - Visual Studio's backup file exists at `%LOCALAPPDATA%\OnDemandSymbols\<VS_INSTANCE_EXP>\symbol-settings-backup.json`.
     - Your pre-existing include list entries are preserved, and `UnrealEditor*.exe` has been added.

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
   - No backup file is created.

---

### Test Case 6: Clean Restore on Solution Close
1. Open an Unreal solution so On-Demand Symbols is active.
2. Close the solution via **File > Close Solution**.
3. **Verify:**
   - The Output pane logs:
     ```
     [Info] Solution closed; restored original symbol settings.
     ```
   - In **Tools > Options > Debugging > Symbols**, the user's original settings and IncludeList are completely restored (**Golden Rule #1**).
   - The backup JSON file in `%LOCALAPPDATA%\OnDemandSymbols\<VS_INSTANCE_EXP>\` is deleted.

---

### Test Case 7: Crash Recovery
1. Open an Unreal solution so On-Demand Symbols is active (backup file exists on disk).
2. Force-kill the Visual Studio process (`devenv.exe`) via Task Manager or `taskkill /F /IM devenv.exe`.
3. Re-launch the Experimental instance.
4. **Verify:**
   - On package load, `RecoverFromCrashIfNeeded()` detects the orphaned backup file from the terminated PID.
   - The Output pane logs:
     ```
     [Warning] Restored symbol settings after a previous unexpected exit (IsManualLoad=..., IsLoadAdjacent=..., AutomaticSymbolLoad=..., IncludeList.Count=...).
     ```
   - Symbol settings are restored to their pre-crash original state, and the orphaned backup file is deleted.

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