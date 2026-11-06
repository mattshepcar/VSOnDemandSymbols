# Manual Verification Guide — On-Demand Symbols (V2 Minimal Architecture)

This document provides step-by-step verification instructions for the V2 clean-slate architecture in Visual Studio 2022 (17.14+) and Visual Studio 2026.

---

## 1. Setup & Launching the Experimental Instance

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
   - No unnecessary menu items, toolbars, or sub-menus are added (clean IDE experience per D-23).
   - All extension status messages and diagnostic logs appear exclusively in the dedicated **Output** window under the "On-Demand Symbols" pane.

---

### Test Case 5: Non-Unreal Solution Inactivity (e.g. NativeSample)
1. Open a standard non-Unreal C++ solution (e.g. `tests/Samples/NativeSample/NativeSample.sln`).
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