# Dimension 7 Analysis: Knowledge Discovery & API Resolution Strategies

> **Scope:** Quantitative and qualitative audit of **how** the agents discovered Visual Studio debugger APIs, Unreal Engine rules, and working fixes across all **51 sessions** (`chat_history/`)—comparing Microsoft documentation (`search_web`), local DLL reflection (`System.Reflection`), IL disassembly (`ildasm.exe`), source inspection (`D:\UnrealEngine`), and edit-compile-fail trial-and-error.  
> **Goal:** Understand which discovery channels actually solved hard SDK problems and how to make API resolution fast and deterministic in future projects.

---

## 1. Executive Summary

The agents did **not** rely primarily on Microsoft's public web documentation to solve Visual Studio debugger and shell API problems. Instead, **local binary inspection was the dominant discovery channel**:

- **214 distinct `run_command` calls** used PowerShell `.NET Reflection` (`[System.Reflection.Assembly]::LoadFrom`, `GetTypes()`, `GetExportedTypes()`, `GetMethods()`) to inspect Visual Studio's `Common7\IDE\PublicAssemblies` / `PrivateAssemblies` and NuGet `.dll`s directly.
- **19 distinct `run_command` calls** invoked **`ildasm.exe`** to disassemble VS SDK interfaces and IL metadata (e.g., dumping `IVsDebuggerSymbolSettings120A` in `S21` and package attributes in `S23`).
- **`search_web` was used 168 times total** (69 queries—**41.1%**—targeting Microsoft / `learn.microsoft.com`), and **63.1% of all web searches (106 / 168)** clustered in just **3 sessions** (`S21`: 44, `S39`: 37, `S19`: 25) where VS 2022/2026 **Unified Settings** (`registration.json`, `SVsUnifiedSettingsManager`) and `BreakpointEvents` lacked clear NuGet/reflection examples.
- **Trial-and-Error (`edit_file` → `msbuild` → error):** Whenever the AI skipped reflection/disassembly or hit undocumented runtime behavior (COM threading, `.vsdconfig` registration, Unified Settings schemas), it fell back to high-error build/edit loops (`S10`: 134 edits/writes, 248 commands, 100 errors; `S21`: 104 edits/writes, 206 commands, 96 errors).

---

## 2. Discovery Channel Mix Across the Project

| Discovery Channel | Observed Volume | Primary Sessions | Effectiveness |
|---|---|---|---|
| **1. Local `.NET Reflection` (`Assembly.LoadFrom`, `GetExportedTypes`)** | **214** `run_command` calls (**3,121** total assembly/path hits) | `S02, S10, S21, S39, S23, S51, S37, S42` | **Very High:** Gave 100% exact type names, GUIDs, method signatures, and enum values for VS 17.14 |
| **2. IL Disassembly (`ildasm.exe`)** | **19** `run_command` calls | `S21` (26 hits), `S23` (9 hits) | **High:** Revealed internal COM interface layouts (`IVsDebuggerSymbolSettings120A`) and package metadata |
| **3. Web Search (`search_web` / `learn.microsoft.com`)** | **168** calls (**69** Microsoft-targeted) | `S21` (44), `S39` (37), `S19` (25), `S23` (14), `S40` (10) | **Mixed:** Essential for JSON schemas (`registration.json`) & conceptual behavior (`LoaderLock`), but noisy on legacy/undocumented VS APIs |
| **4. Target Source Inspection (`D:\UnrealEngine`)** | **5** subagent runs (`S05–S09`, **\$0.35**) | `S04–S09` | **Highest ROI:** Verified exact UBT `.modules` and DLL naming rules directly from C# source |
| **5. Edit-Build-Test Trial & Error** | **997** `write`/`edit` calls vs. **2,054** commands (**915** errors) | `S10, S21, S02, S43, S46, S51` | **Expensive Fallback:** Productive only once automated tests (`Core.Tests` / `IntegrationTests.exe`) gave fast ground-truth feedback |

---

## 3. Session-by-Session Discovery Breakdown

### 3.1 Top Web-Search Sessions (`search_web`)

| Session | `search_web` Calls | Cost | What Was Missing From Local DLLs |
|---|---:|---:|---|
| **`S21`** | **44** | \$1.77 | Migrating legacy Options to VS Unified Settings (`registration.json`, `legacyOptionPageId`, `ISettingsManager`) |
| **`S39`** | **37** | \$0.57 | `ProvideSettingsManifest`, `SVsUnifiedSettingsManager`, `ISettingsManager.GetWriter` runtime wiring |
| **`S19`** | **25** | \$0.44 | `EnvDTE.BreakpointEvents` behavior & AD7 `IDebugBreakpointErrorEvent2` mid-session firing |
| **`S23`** | **14** | \$0.35 | VSIX Extension Manager Markdown `Overview` / `vsixmanifest` asset rules |
| **`S40`** | **10** | \$0.10 | `LoaderLock` MDA on `Package.Dispose` vs. `IVsPackage.Close` & `AdviseDebugEventCallback` |

### 3.2 Top DLL Reflection & Disassembly Sessions (`Common7\IDE` / `.nuget`)

| Session | Assembly / Reflection Hits | Cost | Key APIs Discovered Locally |
|---|---:|---:|---|
| **`S02`** | **694** | \$8.01 | `IVsDebuggerSymbolSettingsManager120A`, `Microsoft.VisualStudio.Debugger.Engine` (Concord) types |
| **`S10`** | **667** | \$30.93 | Concord `Dkm*` interfaces, `EnvDTE90.Debugger3`, `SymbolSettings` COM signatures |
| **`S21`** | **469** *(incl. 26 `ildasm`)* | \$1.77 | Dumped `IVsDebuggerSymbolSettings120A` via `ildasm.exe`; inspected `Microsoft.VisualStudio.Utilities.dll` (`UnifiedSettings`) |
| **`S39`** | **329** | \$0.57 | Reflected `ISettingsManager` / `ISettingsWriter` methods in `PrivateAssemblies` |
| **`S23`** | **264** *(incl. 9 `ildasm`)* | \$0.35 | Inspected VSIX packaging & `Microsoft.VisualStudio.ExtensionManager` assemblies |
| **`S51`** | **190** | \$17.64 | Reflected `IVsDebugLaunchHook` and `*Symbol*` exported types in `Common7\IDE\PublicAssemblies` |
| **`S37`** | **109** | \$0.44 | Reflected `EnvDTE90.Module` (`SymbolsLoaded`, `SymbolFile`) directly from `.nuget\packages\envdte90` |

---

## 4. Why Local Reflection Beat Web Search—and Where Both Struggled

1. **Why Reflection/Disassembly Dominated VS Debugger APIs:**
   - Visual Studio's debugger SDK (`EnvDTE90`, `IVsDebuggerSymbolSettingsManager120A`, `IVsDebugLaunchHook`, AD7 `EnumFrameInfo(FIF_MODULE)`) is sparsely documented on `learn.microsoft.com`, and web snippets often reference wrong VS versions.
   - Reflecting directly on the installed **VS 17.14** `PublicAssemblies`, `PrivateAssemblies`, and `.nuget\packages` gave **ground-truth, version-exact** signatures in one PowerShell command.
2. **Why Unified Settings (`S21`, `S39`) Triggered Web-Search Storms (81 calls):**
   - Reflection shows C# interface methods (`ISettingsManager`), **not** declarative JSON schemas (`registration.json`) or VS 2026 `"These settings haven't been migrated yet"` UX rules. That gap forced 81 `search_web` calls across `S21` and `S39`—including a moment in `S21` where the pilot explicitly asked: *"isn't this documented on the microsoft website for how to build a VSIX extension?"*
3. **Why Trial-and-Error Exploded in `S10` and `S21`:**
   - Runtime behavior (whether Concord's `IDkmBreakpointManagerNotification` suppresses breakpoint glyphs, or whether `Module.LoadSymbols("")` pops a modal Win32 PDB dialog) cannot be seen in reflection *or* docs. Until automated out-of-proc tests (`S45–S47`) existed, those behaviors could only be discovered via trial-and-error with the pilot pressing F5.

---

## 5. Rules for Faster API Discovery in Future Projects

1. **Always inspect installed SDK DLLs/XML docs *before* `search_web` or coding.** A single PowerShell `GetExportedTypes()` / `GetMethods()` or `ildasm.exe` check eliminates 90% of `CS` compiler-guesswork loops.
2. **Reserve `search_web` for declarative schemas, lifecycle contracts, and error codes** (`registration.json`, `LoaderLock`, `.vsdconfig` XML) that cannot be inferred from C# type signatures.
3. **Delegate SDK/source exploration to a read-only subagent on Turn 1.** Just as `S05–S09` mapped `D:\UnrealEngine` for **\$0.35**, a dedicated subagent can dump exact VS SDK interface signatures into a 1-page reference before implementation begins.
4. **Pair API discovery with an out-of-proc smoke harness early.** Static signatures tell you *how to compile*; only a live host harness (`MiniUnreal` / `IntegrationTests.exe`) tells you *what the host actually does at runtime*.
