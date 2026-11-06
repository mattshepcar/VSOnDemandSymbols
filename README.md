# On-Demand Symbols for Visual Studio

> A lightweight Visual Studio extension that accelerates native C++ debugging startup for **Unreal Engine 5** projects in **Visual Studio 2022 (17.14+)** and **Visual Studio 2026** by loading symbols on demand.

---

## Supported Visual Studio Versions

* **Visual Studio 2022** — Version **17.14** or later (64-bit)
* **Visual Studio 2026** — (64-bit)
* **Editions:** Community, Professional, and Enterprise
* **Target Architectures:** x64 (amd64)

> [!NOTE]
> Visual Studio 2022 releases prior to 17.14, Visual Studio 2019, and 32-bit IDE versions are not supported.

---

## What It Does

* **Fast Debugger Startup:** Configures Visual Studio into "load only specified modules" mode, bypassing upfront symbol loading for roughly ~1,000 Unreal Engine DLLs so editor boot times with the debugger attached become comparable to launching without the debugger.
* **Startup Savings & Statistics:** Automatically calculates how many unnecessary PDB symbol loads were avoided when booting up the editor and estimates developer time saved (both per launch and lifetime), displayed in the Output window and on the settings page.
* **Breakpoint Preloading:** Automatically resolves source files to their Unreal modules and preloads symbols into the debugger's `IncludeList` before launch so breakpoints bind immediately at process startup.
* **Runtime Breakpoint Binding:** Detects newly placed or enabled breakpoints while running mid-session and loads module symbols immediately without requiring a debug restart.
* **On-Demand Break Symbols:** Automatically resolves and loads symbols for active call stack frames and background thread locations when execution stops (breakpoint, crash, assertion, or thread switch).
* **Safe Settings Management:** Automatically backs up your original Visual Studio symbol settings and restores them cleanly on solution close, extension disable, or crash recovery.

> [!NOTE]
> **Scale in Practice:** In Unreal Engine 5.8.4 with the Lyra Starter Game, the `LyraEditor.target` build manifest tracks **1,168 DLLs** (1,129 Unreal modules + 39 third-party) and **1,131 PDBs**, with **933 Unreal modules** (and 950+ total DLLs) mapped during editor startup.

---

## Output and Diagnostics

Real-time status, startup time savings, module resolutions, and symbol-loading events are reported directly to Visual Studio's **Output** window:

* Open **View &gt; Output** (or press `Ctrl+Alt+O`).
* In the **Show output from** dropdown, select **"On-Demand Symbols"**.
* When the editor boots, view the startup acceleration summary (e.g. `Startup symbol acceleration: avoided loading 948 PDBs (estimated ~25s saved this launch)`).

---

## How to Use

1. **Zero Configuration:** Simply open any Unreal Engine 5 solution (`.sln` / `.slnx`) and start debugging (`F5`). The extension activates automatically.
2. **Settings & Lifetime Savings:** View cumulative statistics (PDBs avoided, estimated time saved) and customize break symbol behavior under:
   * **Tools &gt; Options &gt; Debugging &gt; Symbols &gt; On Demand Loading**
   * **Startup Acceleration Stats:** Displays lifetime PDBs avoided and estimated time saved.
   * **Break Symbol Loading Mode:**
     * **LocalOnly (Default):** Loads adjacent and locally cached PDBs while masking remote servers to prevent background network latency.
     * **All:** Queries configured remote symbol servers if symbols are missing locally.
     * **Off:** Disables automatic symbol loading on break (startup breakpoint preloading remains active).

---

## Built With (Vibe Coded)

This project was "vibe coded" using AI coding assistants:
* **Claude Opus 5.5**
* **Gemini 4 Argon**
* ...and written mostly by **Gemini 3.8 Flash**

---

## License

Licensed under the [MIT License](LICENSE).

