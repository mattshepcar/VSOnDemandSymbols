# Phase 0 Spike Harness (`spikes/`)

This folder holds throwaway spike verification code for Phase 0 ([docs/VS_DEBUGGER_INTEGRATION.md](../docs/VS_DEBUGGER_INTEGRATION.md) §6 & §7).
Per `AGENTS.md`, spike code must never be committed into `src/`.

## Contents

- [`Phase0SpikeRunner.cs`](Phase0SpikeRunner.cs) — Self-contained VSIX helper that exercises:
  - **S1:** `IVsDebuggerSymbolSettingsManager120A`, `IVsDebuggerSymbolSettings120A`, `IDebugOptionList120A`, and `IVsDebuggerSymbolSettings170` (`AutomaticSymbolLoad`) round-trip and wildcard lookup, restoring original user settings immediately.
  - **S2:** `IDebugModule3.LoadSymbols()` execution and timing.
  - **S3:** `IDebugModuleLoadEvent2` timing (`Stopwatch`) and `dwAttrib` inspection.
  - **S4:** Strategy A (`IDebugBreakpointErrorEvent2` → `BP_LOCATION.unionmember2` marshaled as `IDebugDocumentPosition2` / `IDebugFunctionPosition2`) and Strategy B (`CommandEvents.AfterExecute` + `DTE.Debugger.Breakpoints` diffing).
  - **S5:** `CommandEvents.BeforeExecute` for `StandardCommandSet97` (`Debug.Start`, `Debug.Restart`, `Debug.StepInto`, `Debug.StepOver`, `Debug.RunToCursor`, `Debug.AttachtoProcess`) and `guidVSDebugCommand` (`Debug.ReattachtoProcess`, `SingleProcStepInto`, `SingleProcStepOver`).
  - **S7:** `IDebugSymbolSearchEvent2` with `MIF_SYMBOLS_LOADED` to distinguish user-initiated symbol loads (`ML-1`) from extension-initiated loads.

## Running in the Experimental Instance (`/rootsuffix Exp`)

1. Temporarily include `<Compile Include="spikes\Phase0SpikeRunner.cs" />` in the VSIX `.csproj`.
2. Call `await VSOnDemandSymbols.Spikes.Phase0SpikeRunner.InitializeAsync(this, cancellationToken);` from `VSOnDemandSymbolsPackage.InitializeAsync`.
3. Press **F5** in Visual Studio to launch the Experimental Instance and view the **On-Demand Symbols Spike** pane in the Output window.
4. Remove the temporary `<Compile>` hook before committing to `src/`.
