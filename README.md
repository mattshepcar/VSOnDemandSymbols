# On-Demand Symbols for Visual Studio

> A lightweight Visual Studio extension that accelerates native C++ debugging startup for **Unreal Engine 5** projects by loading symbols on demand.

---

## What It Does

* **Fast Debugger Startup:** Configures Visual Studio into "load only specified modules" mode, bypassing upfront symbol loading for roughly ~1,000 Unreal Engine DLLs so editor boot times with the debugger attached become comparable to launching without the debugger.
* **Breakpoint Preloading:** Automatically resolves source files to their Unreal modules and preloads symbols into the debugger's `IncludeList` before launch so breakpoints bind immediately at process startup.
* **Runtime Breakpoint Binding:** Detects newly placed or enabled breakpoints while running mid-session and loads module symbols immediately without requiring a debug restart.
* **On-Demand Break Symbols:** Automatically resolves and loads symbols for active call stack frames and background thread locations when execution stops (breakpoint, crash, assertion, or thread switch).
* **Safe Settings Management:** Automatically backs up your original Visual Studio symbol settings and restores them cleanly on solution close, extension disable, or crash recovery.

> [!NOTE]
> **Scale in Practice:** In Unreal Engine 5.8.4 with the Lyra Starter Game, the `LyraEditor.target` build manifest tracks **1,168 DLLs** (1,129 Unreal modules + 39 third-party) and **1,131 PDBs**, with **933 Unreal modules** (and 950+ total DLLs) mapped during editor startup.

---

## Output and Diagnostics

Real-time status, module resolutions, and symbol-loading events are reported directly to Visual Studio's **Output** window:

* Open **View &gt; Output** (or press `Ctrl+Alt+O`).
* In the **Show output from** dropdown, select **"On-Demand Symbols"**.

---

## How to Use

1. **Zero Configuration:** Simply open any Unreal Engine 5 solution (`.sln` / `.slnx`) and start debugging (`F5`). The extension activates automatically.
2. **Break Symbol Loading Mode:** Customize symbol resolution behavior when execution breaks by navigating to:
   * **Tools &gt; Options &gt; Debugging &gt; Symbols &gt; On Demand Loading**
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

