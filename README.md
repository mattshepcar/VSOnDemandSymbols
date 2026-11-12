# On-Demand Symbols for Visual Studio

On-Demand Symbols is a lightweight Visual Studio extension for Unreal Engine 5 C++ projects. It speeds up debugger startup in Visual Studio 2022 (17.14+) and Visual Studio 2026 because it loads symbols only on demand.

## Supported Visual Studio versions

* Visual Studio 2022: version 17.14 or later (64-bit)
* Visual Studio 2026 (64-bit)
* Editions: Community, Professional, and Enterprise
* Target architecture: x64 (amd64)

> [!NOTE]
> Visual Studio 2022 versions before 17.14, Visual Studio 2019, and 32-bit IDE versions are not supported.

## What it does

* The extension puts Visual Studio into "load only specified modules" mode. This skips upfront symbol loading for roughly 1,000 Unreal Engine DLLs. Editor startup with the debugger attached takes almost the same time as launching without the debugger.
* Before launch, it maps source files with breakpoints to their Unreal modules and adds them to the debugger's `IncludeList`. Breakpoints bind at process startup.
* When you add or enable a breakpoint during a debug session, the extension loads symbols for that module right away. You do not need to restart the debugger.
* When execution stops at a breakpoint, crash, assertion, or thread switch, the extension loads symbols for the active call stack frames and background threads.
* The extension backs up your original Visual Studio symbol settings before it changes them. It restores them on solution close, extension disable, or crash recovery.

> [!NOTE]
> In Unreal Engine 5.8.4 with the Lyra Starter Game, the `LyraEditor.target` build manifest tracks 1,168 DLLs (1,129 Unreal modules and 39 third-party) and 1,131 PDBs. The editor maps 933 Unreal modules and 950+ total DLLs during startup.

## How to use

Open any Unreal Engine 5 solution (`.sln` or `.slnx`) and press `F5` to start debugging. The extension turns on automatically and needs no setup.

To change how symbols load when execution stops, open **Tools > Options > Debugging > Symbols > On Demand Loading**:

* `LocalOnly` (default): Loads adjacent and locally cached PDBs. It skips remote symbol servers to avoid network delays.
* `All`: Queries your configured remote symbol servers when a PDB is not on disk.
* `Off`: Turns off automatic symbol loading on break. Breakpoint preloading at startup stays on.

## Output and diagnostics

The extension writes status updates, module matches, and symbol load events to the Visual Studio **Output** window:

1. Open **View > Output** (or press `Ctrl+Alt+O`).
2. In the **Show output from** dropdown, select **On-Demand Symbols**.

## Built with (vibe coded)

This project was "vibe coded" with AI coding assistants:

* Claude Opus 5.5
* Gemini 4 Argon
* Written mostly by Gemini 3.8 Flash

## License

Licensed under the [MIT License](LICENSE).


