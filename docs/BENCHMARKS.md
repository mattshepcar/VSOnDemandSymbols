# Benchmarks — Startup & Symbol Loading Performance

This document records baseline measurements from Phase 0 (Spike S6) and final extension measurements from Phase 6 against non-functional requirements **NF-1** (`< 0.1 ms` average per `IDkmModuleInstanceLoadNotification` on the Concord worker thread, `0 ms` on the VS UI thread) and **NF-2** (`< 300 ms` warm / `< 2 s` cold pre-launch wanted-set computation).

---

## 1. Measurement Protocol (Spike S6 & Phase 6)

### Environment
| Field | Value |
|---|---|
| **OS** | Windows (x64) |
| **Visual Studio** | Visual Studio 2026 Community `18.11.1` (and/or VS 2022 `17.14+`) |
| **CPU / Storage** | *(Record CPU model and NVMe SSD where UE and symbol cache live)* |
| **Unreal Engine version** | UE 5.x (`D:\UnrealEngine`) |
| **TargetProject** | `UnrealEditor.exe` (Development Editor, Win64, modular build) |

### How to Measure (S6 Baseline — No Extension Required)
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
4. **Scenario D — On-Demand Symbols Extension Enabled (Phase 3 / Phase 6):**
   - Enable **On-Demand Symbols** with the same ~20 modules in the wanted set.
   - Record startup time, pre-launch computation time (logged by the extension), and average module-load handler time (logged at `Diagnostic` level).

---

## 2. Startup Time Results (S6 & Phase 6)

*(Pending user execution on `D:\UnrealEngine` once built — fill in values below.)*

| Scenario | Total Modules Loaded | Modules with Symbols Loaded | Cold Run (s) | Warm Run 1 (s) | Warm Run 2 (s) | Overhead vs Scenario B (%) |
|---|---:|---:|---:|---:|---:|---:|
| **A. All symbols loaded** | ~800 | ~800 | *TBD* | *TBD* | *TBD* | — |
| **B. Manual mode (0 symbols)** | ~800 | 0 | *TBD* | *TBD* | *TBD* | 0% (baseline) |
| **C. Manual mode (~20 modules)** | ~800 | ~20 | *TBD* | *TBD* | *TBD* | *TBD* |
| **D. On-Demand Symbols (~20 wanted)** | ~800 | ~20 | *TBD* | *TBD* | *TBD* | *TBD* (target: ≤ +10% of B + C) |

---

## 3. Extension Internal Timings (NF-1 & NF-2)

| Metric | Requirement | Measured (Synthetic 800-module test) | Measured (UE5 Editor) | Status |
|---|---|---|---|---|
| **NF-1:** `IDkmModuleInstanceLoadNotification` handler average time (Concord worker thread) | `< 0.1 ms` (`100 µs`) | *TBD* | *TBD* | Pending |
| **NF-2 (Warm):** Pre-launch wanted-set computation (cached `.Build.cs` & manifests) | `< 300 ms` | *TBD* | *TBD* | Pending |
| **NF-2 (Cold):** Pre-launch wanted-set computation (cold cache, full engine solution) | `< 2000 ms` | *TBD* | *TBD* | Pending |
| **S2:** Single `DkmModuleInstance.TryLoadSymbols()` call duration (`UnrealEditor-Engine.dll`) | Logged for reference | *TBD* | *TBD* | Pending |

