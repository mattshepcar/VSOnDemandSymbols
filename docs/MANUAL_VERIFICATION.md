### Manual Verification Steps (Experimental Instance — Lyra `DebugGame Editor`)

1. **Launch Experimental Instance**:
   - Open `VSOnDemandSymbols.slnx` in Visual Studio and press **F5** to launch `devenv.exe /rootsuffix Exp`.
2. **Verify Unreal Detection & Manual-Load Mode Application**:
   - In the Experimental instance, open `D:\UnrealEngine\Samples\Games\Lyra\Lyra.slnx` and set the solution configuration to **DebugGame Editor** (`Win64`) with **Lyra** as the startup project.
   - Open **View > Output** and select **On-Demand Symbols** from the *Show output from* dropdown:
     - Confirm it logs `On-demand symbol mode active...` and `Unreal solution detected: 'Lyra.slnx' (ProjectRoot=D:\UnrealEngine\Samples\Games\Lyra, EngineRoot=D:\UnrealEngine, ...)`.
   - Open **Tools > Options > Debugging > Symbols**:
     - Confirm manual-load mode is selected ("Load only specified modules" / "Automatically choose what module symbols to search for"), "Always load symbols located next to modules" is unchecked, and your existing include/exclude lists are untouched.
3. **Verify Wanted-Set Symbol Loading on Launch (M1 / M2 / M22 / MRU)**:
   - Set a breakpoint in a Lyra game or plugin `.cpp` file (for example `Source\LyraGame\AbilitySystem\LyraAbilitySystemComponent.cpp` or `Plugins\GameFeatures\ShooterCore\Source\ShooterCoreRuntime\Private\ShooterCoreRuntimeModule.cpp`), or add `"LyraGame"` to `D:\UnrealEngine\Samples\Games\Lyra\.vs\Lyra\OnDemandSymbols\state.json`.
   - Press **F5** to start debugging (`UnrealEditor-Win64-DebugGame.exe`):
     - Confirm the **On-Demand Symbols** Output pane logs:
       - `Session #1 — UnrealEditor-Win64-DebugGame.exe — wanted N modules (pinned ..., MRU ..., breakpoints ..., always ...); pruned ... from MRU.`
       - `Loading symbols: UnrealEditor-Win64-DebugGame.exe — AlwaysLoad`
       - `Loading symbols: UnrealEditor-LyraGame-Win64-DebugGame.dll — ...` (and/or `UnrealEditor-ShooterCoreRuntime-Win64-DebugGame.dll`) only for the wanted modules.
     - Open **Debug > Windows > Modules** and confirm symbols are loaded only for `UnrealEditor-Win64-DebugGame.exe` and the wanted module(s), not all ~800 DLLs.
   - Stop debugging (**Shift+F5**) and confirm the Output pane logs:
     - `Session ended — <total> modules loaded, symbols for <count> (<atLoad> at load, <onDemand> on demand). MRU: <count> entries.`
4. **Verify Settings Restore on Solution Close (M16)**:
   - Close the solution (**File > Close Solution**).
   - Confirm the Output pane logs `Restored user symbol settings (...)`, `%LOCALAPPDATA%\OnDemandSymbols\<InstanceId>\symbol-settings-backup.json` is deleted, and **Tools > Options > Debugging > Symbols** matches your original settings.
5. **Verify Crash Recovery on Startup (M17)**:
   - Re-open `Lyra.slnx` so on-demand mode is active (`symbol-settings-backup.json` exists).
   - Kill the Experimental `devenv.exe` process from Task Manager.
   - Restart the Experimental instance and open `Lyra.slnx`:
     - Confirm the **On-Demand Symbols** Output pane logs `[Warning] Restored symbol settings after a previous unexpected exit (...)`.