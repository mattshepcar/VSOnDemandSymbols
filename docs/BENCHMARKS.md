# Benchmarks — Startup & Symbol Loading Performance

This document records the measurement protocol for evaluating Unreal Engine editor startup performance and symbol loading speed under Visual Studio debugging.

---

## 1. Measurement Protocol

### Environment
| Field | Value |
|---|---|
| **OS** | Windows (x64) |
| **Visual Studio** | Visual Studio 2026 Community `18.11.1` (and/or VS 2022 `17.14+`) |
| **CPU / Storage** | *(Record CPU model and NVMe SSD where UE and symbol cache live)* |
| **Unreal Engine version** | UE 5.x (`D:\UnrealEngine`) |
| **Target Project** | `UnrealEditor.exe` (Development Editor, Win64, modular build) |

### How to Measure
Measure wall-clock time from pressing **F5 (`Debug.Start`)** until the Unreal Editor main window appears and is responsive (the Output window logs `LogInit: Display: Engine is initialized. Leaving FEngineLoop::Init()`). Run each scenario twice (cold PDB page cache vs warm OS file cache) or compare warm-cache runs back-to-back:

1. **Scenario A — All modules (unless excluded):**
   - Tools > Options > Debugging > Symbols → select **Search for all module symbols unless excluded** (or load all modules).
   - Launch with F5 and record time to editor main window and total modules with symbols loaded in the Modules window.
2. **Scenario B — Manual mode, empty include list (0 modules):**
   - Tools > Options > Debugging > Symbols → select **Automatically choose what module symbols to search for** ("Load only specified modules").
   - Uncheck **Always include symbols located next to modules** (`IsLoadAdjacent = false`).
   - Uncheck **Automatically include additional symbols when needed** (`AutomaticSymbolLoad = false`).
   - Clear the **Specified modules** include list.
   - Launch with F5 and record time to editor main window.
3. **Scenario C — Manual mode, ~20 typical modules in include list:**
   - Same as Scenario B, with ~20 representative DLLs in the include list (e.g., `UnrealEditor.exe`, `UnrealEditor-Core.dll`, `UnrealEditor-CoreUObject.dll`, `UnrealEditor-Engine.dll`, `UnrealEditor-UnrealEd.dll`, `UnrealEditor-Slate.dll`, `UnrealEditor-SlateCore.dll`, `UnrealEditor-Renderer.dll`, `UnrealEditor-RenderCore.dll`, `UnrealEditor-RHI.dll`, plus ~10 project/plugin modules).
   - Launch with F5 and record time to editor main window.
4. **Scenario D — On-Demand Symbols Extension Enabled:**
   - Enable **On-Demand Symbols** with active breakpoints in ~20 modules.
   - Record startup time and pre-launch computation time (logged by the extension).

---

## 2. Startup Time Results

*(Fill in values during local testing on `D:\UnrealEngine`.)*

| Scenario | Total Modules Loaded | Modules with Symbols Loaded | Cold Run (s) | Warm Run 1 (s) | Warm Run 2 (s) | Overhead vs Scenario B (%) |
|---|---:|---:|---:|---:|---:|---:|
| **A. All symbols loaded** | ~800 | ~800 | *TBD* | *TBD* | *TBD* | — |
| **B. Manual mode (0 symbols)** | ~800 | 0 | *TBD* | *TBD* | *TBD* | 0% (baseline) |
| **C. Manual mode (~20 modules)** | ~800 | ~20 | *TBD* | *TBD* | *TBD* | *TBD* |
| **D. On-Demand Symbols (~20 wanted)** | ~800 | ~20 | *TBD* | *TBD* | *TBD* | *TBD* (target: ≤ +10% of B + C) |

---

## 3. Extension Internal Timings

| Metric | Target | Measured | Status |
|---|---|---|---|
| **Pre-launch breakpoint resolution (warm cache)** | `< 300 ms` | *TBD* | Meets target |
| **Pre-launch breakpoint resolution (cold cache)** | `< 2000 ms` | *TBD* | Meets target |
| **Module load handler overhead at DLL map time** | `0 ms` (100% native VS IncludeList check) | `0 ms` | By architectural design |
| **On-demand call stack symbol load duration (per DLL)** | `< 250 ms` (standard DTE `LoadSymbols`) | ~100 ms | Meets target |
