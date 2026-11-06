### Manual Verification Steps (Experimental Instance)

1. **Launch Experimental Instance**:
   - Open `VSOnDemandSymbols.slnx` in Visual Studio and press **F5** to launch `devenv.exe /rootsuffix Exp`.
2. **Verify Unreal Detection & Manual-Load Mode Application**:
   - Open an Unreal Engine C++ solution (or `D:\UnrealEngine\UE5.sln`) in the Experimental instance.
   - Open **View > Output** and select **On-Demand Symbols** from the *Show output from* dropdown:
     - Confirm it logs `On-demand symbol mode active...` and `Unreal solution detected: ...`.
   - Open **Tools > Options > Debugging > Symbols**:
     - Confirm manual-load mode is selected ("Load only specified modules" / "Automatically choose what module symbols to search for"), "Always load symbols located next to modules" is unchecked, and your existing include/exclude lists are untouched.
3. **Verify Wanted-Set Symbol Loading on Launch (M1 / M2 / MRU)**:
   - Open `<SolutionDir>\.vs\<SolutionName>\OnDemandSymbols\state.json` (created automatically when the solution opens) and add an MRU entry for a module (e.g. `MyGame` or `Core`), or set a breakpoint in a `.cpp` file belonging to that module.
   - Press **F5** to start debugging `UnrealEditor.exe`:
     - Confirm the **On-Demand Symbols** Output pane logs:
       - `Session #1 — UnrealEditor.exe — wanted N modules (pinned ..., MRU ..., breakpoints ..., always ...); pruned ... from MRU.`
       - `Loading symbols: UnrealEditor-<Module>.dll — ...` only for the wanted modules and `UnrealEditor.exe`.
     - Open **Debug > Windows > Modules** and confirm symbols are loaded only for the wanted modules, not all ~800 DLLs.
   - Stop debugging (**Shift+F5**) and confirm the Output pane logs:
     - `Session ended — <total> modules loaded, symbols for <count> (<atLoad> at load, <onDemand> on demand). MRU: <count> entries.`
4. **Verify Settings Restore on Solution Close (M16)**:
   - Close the solution (**File > Close Solution**).
   - Confirm the Output pane logs `Restored user symbol settings (...)`, `%LOCALAPPDATA%\OnDemandSymbols\<InstanceId>\symbol-settings-backup.json` is deleted, and **Tools > Options > Debugging > Symbols** matches your original settings.
5. **Verify Crash Recovery on Startup (M17)**:
   - Re-open the Unreal solution so on-demand mode is active (`symbol-settings-backup.json` exists).
   - Kill the Experimental `devenv.exe` process from Task Manager.
   - Restart the Experimental instance and open any C++ solution:
     - Confirm the **On-Demand Symbols** Output pane logs `[Warning] Restored symbol settings after a previous unexpected exit (...)`.