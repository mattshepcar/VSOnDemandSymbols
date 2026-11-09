using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using EnvDTE;
using EnvDTE80;
using Newtonsoft.Json;
using VSOnDemandSymbols.Core.State;

namespace VSOnDemandSymbols.IntegrationTests;

/// <summary>
/// Manages the lifecycle of a Visual Studio Experimental instance (<c>devenv.exe /rootsuffix Exp</c>)
/// and attaches to its <see cref="DTE2"/> automation object via the Windows Running Object Table (ROT).
/// </summary>
public sealed class VisualStudioExpFixture : IDisposable
{
    public string RepoRoot { get; }
    public string MiniUnrealSolutionPath { get; }
    public string MiniUnrealBinariesDir { get; }
    public string ModuleASourcePath { get; }
    public string ModuleBSourcePath { get; }
    public string DevenvPath { get; }

    public System.Diagnostics.Process? DevenvProcess { get; private set; }
    public DTE2? Dte { get; private set; }

    public VisualStudioExpFixture()
    {
        RepoRoot = LocateRepoRoot();
        MiniUnrealSolutionPath = Path.Combine(RepoRoot, "tests", "Fixtures", "MiniUnreal", "MiniUnreal.sln");
        MiniUnrealBinariesDir = Path.Combine(RepoRoot, "tests", "Fixtures", "MiniUnreal", "Binaries", "Win64");
        ModuleASourcePath = Path.Combine(RepoRoot, "tests", "Fixtures", "MiniUnreal", "Source", "ModuleA", "ModuleA.cpp");
        ModuleBSourcePath = Path.Combine(RepoRoot, "tests", "Fixtures", "MiniUnreal", "Source", "ModuleB", "ModuleB.cpp");
        DevenvPath = LocateDevenvPath();

        EnsureMiniUnrealBuilt();
        DeployLatestBuildToExpHive();
        StartAndAttach();
    }

    private void DeployLatestBuildToExpHive()
    {
        string buildOutDir = Path.Combine(RepoRoot, "src", "VSOnDemandSymbols", "bin", "Debug", "net472");
        string builtDll = Path.Combine(buildOutDir, "VSOnDemandSymbols.dll");
        if (!File.Exists(builtDll))
        {
            return;
        }

        string vsLocalAppData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "VisualStudio");
        if (!Directory.Exists(vsLocalAppData))
        {
            return;
        }

        foreach (string expDir in Directory.GetDirectories(vsLocalAppData, "*Exp"))
        {
            string extensionsRoot = Path.Combine(expDir, "Extensions");
            if (!Directory.Exists(extensionsRoot))
            {
                continue;
            }

            foreach (string existingDll in Directory.GetFiles(extensionsRoot, "VSOnDemandSymbols.dll", SearchOption.AllDirectories))
            {
                string? targetDir = Path.GetDirectoryName(existingDll);
                if (string.IsNullOrEmpty(targetDir))
                {
                    continue;
                }

                Console.WriteLine($"[Fixture] Updating VSIX files in Exp hive: {targetDir}");
                foreach (string fileName in new[]
                {
                    "VSOnDemandSymbols.dll",
                    "VSOnDemandSymbols.pdb",
                    "VSOnDemandSymbols.pkgdef",
                    "VSOnDemandSymbols.Core.dll",
                    "VSOnDemandSymbols.Core.pdb"
                })
                {
                    string srcFile = Path.Combine(buildOutDir, fileName);
                    if (File.Exists(srcFile))
                    {
                        File.Copy(srcFile, Path.Combine(targetDir, fileName), overwrite: true);
                    }
                }
            }
        }
    }

    public void StartAndAttach()
    {
        KillCurrentInstanceIfRunning();

        Console.WriteLine($"[Fixture] Launching Visual Studio Experimental instance: {DevenvPath} /rootsuffix Exp /nosplash");
        var psi = new ProcessStartInfo
        {
            FileName = DevenvPath,
            Arguments = "/rootsuffix Exp /nosplash",
            WorkingDirectory = RepoRoot,
            UseShellExecute = false
        };

        DevenvProcess = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start devenv.exe process.");

        Dte = WaitForDteInRot(DevenvProcess, TimeSpan.FromSeconds(60));
        Console.WriteLine($"[Fixture] Attached to DTE (PID={DevenvProcess.Id}, Version={Dte.Version}).");

        try
        {
            Dte.SuppressUI = false;
            Dte.MainWindow.Visible = true;
        }
        catch
        {
            // Ignore if main window is still initializing
        }
    }

    public void SetMiniUnrealTestMode(string mode)
    {
        Directory.CreateDirectory(MiniUnrealBinariesDir);
        string modeFile = Path.Combine(MiniUnrealBinariesDir, "test-mode.txt");
        File.WriteAllText(modeFile, mode);
    }

    public string GetOnDemandSymbolsOutputText()
    {
        if (Dte == null)
        {
            return string.Empty;
        }

        try
        {
            var panes = Dte.ToolWindows.OutputWindow.OutputWindowPanes;
            OutputWindowPane? targetPane = null;
            foreach (OutputWindowPane pane in panes)
            {
                if (string.Equals(pane.Name, "On-Demand Symbols", StringComparison.OrdinalIgnoreCase))
                {
                    targetPane = pane;
                    break;
                }
            }

            if (targetPane == null)
            {
                return string.Empty;
            }

            var doc = targetPane.TextDocument;
            var editPoint = doc.StartPoint.CreateEditPoint();
            return editPoint.GetText(doc.EndPoint) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public void ClearOnDemandSymbolsOutputPane()
    {
        if (Dte == null)
        {
            return;
        }

        try
        {
            var panes = Dte.ToolWindows.OutputWindow.OutputWindowPanes;
            foreach (OutputWindowPane pane in panes)
            {
                if (string.Equals(pane.Name, "On-Demand Symbols", StringComparison.OrdinalIgnoreCase))
                {
                    pane.Clear();
                    break;
                }
            }
        }
        catch
        {
        }
    }

    public bool WaitUntilOutputContains(string expectedSubstring, TimeSpan timeout, out string lastOutput)
    {
        var sw = Stopwatch.StartNew();
        lastOutput = string.Empty;
        while (sw.Elapsed < timeout)
        {
            lastOutput = GetOnDemandSymbolsOutputText();
            if (lastOutput.IndexOf(expectedSubstring, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            System.Threading.Thread.Sleep(200);
        }

        lastOutput = GetOnDemandSymbolsOutputText();
        return lastOutput.IndexOf(expectedSubstring, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public bool WaitForDebuggerMode(dbgDebugMode targetMode, TimeSpan timeout)
    {
        if (Dte == null)
        {
            return false;
        }

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                if (Dte.Debugger.CurrentMode == targetMode)
                {
                    return true;
                }
            }
            catch
            {
            }

            System.Threading.Thread.Sleep(150);
        }

        try
        {
            return Dte.Debugger.CurrentMode == targetMode;
        }
        catch
        {
            return false;
        }
    }

    public void StopDebuggingIfActive()
    {
        if (Dte == null)
        {
            return;
        }

        try
        {
            if (Dte.Debugger.CurrentMode != dbgDebugMode.dbgDesignMode)
            {
                Dte.Debugger.TerminateAll();
                WaitForDebuggerMode(dbgDebugMode.dbgDesignMode, TimeSpan.FromSeconds(15));
            }
        }
        catch
        {
        }
    }

    public void ClearAllBreakpoints()
    {
        if (Dte?.Debugger?.Breakpoints == null)
        {
            return;
        }

        try
        {
            var bps = new List<Breakpoint>();
            foreach (Breakpoint bp in Dte.Debugger.Breakpoints)
            {
                bps.Add(bp);
            }

            foreach (var bp in bps)
            {
                try
                {
                    bp.Delete();
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    public EnvDTE90.Module? FindDebuggedModule(string moduleFileName)
    {
        if (Dte?.Debugger?.DebuggedProcesses == null)
        {
            return null;
        }

        foreach (EnvDTE.Process proc in Dte.Debugger.DebuggedProcesses)
        {
            if (proc is EnvDTE90.Process3 proc3 && proc3.Modules != null)
            {
                foreach (EnvDTE90.Module mod in proc3.Modules)
                {
                    string name = Path.GetFileName(mod.Name ?? string.Empty);
                    if (string.Equals(name, moduleFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        return mod;
                    }
                }
            }
        }

        return null;
    }

    public (string? Path, SymbolSettingsBackupDto? Dto) FindBackupFileForCurrentProcess()
    {
        if (DevenvProcess == null)
        {
            return (null, null);
        }

        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OnDemandSymbols");
        if (!Directory.Exists(root))
        {
            return (null, null);
        }

        foreach (string file in Directory.GetFiles(root, "symbol-settings-backup.json", SearchOption.AllDirectories))
        {
            try
            {
                string json = File.ReadAllText(file);
                var dto = JsonConvert.DeserializeObject<SymbolSettingsBackupDto>(json);
                if (dto != null && dto.ProcessId == DevenvProcess.Id)
                {
                    return (file, dto);
                }
            }
            catch
            {
            }
        }

        return (null, null);
    }

    public void KillCurrentInstanceIfRunning()
    {
        Dte = null;
        if (DevenvProcess != null)
        {
            try
            {
                if (!DevenvProcess.HasExited)
                {
                    DevenvProcess.Kill();
                    DevenvProcess.WaitForExit(10000);
                }
            }
            catch
            {
            }
            finally
            {
                DevenvProcess.Dispose();
                DevenvProcess = null;
            }
        }
    }

    public void Dispose()
    {
        try
        {
            StopDebuggingIfActive();
            if (Dte?.Solution?.IsOpen == true)
            {
                Dte.Solution.Close(SaveFirst: false);
            }
            Dte?.Quit();
            DevenvProcess?.WaitForExit(10000);
        }
        catch
        {
        }
        finally
        {
            KillCurrentInstanceIfRunning();
        }
    }

    private static DTE2 WaitForDteInRot(System.Diagnostics.Process process, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        string pidSuffix = ":" + process.Id;

        while (sw.Elapsed < timeout)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"devenv.exe (PID={process.Id}) exited prematurely with code {process.ExitCode}.");
            }

            if (GetRunningObjectTable(0, out IRunningObjectTable rot) == 0 && rot != null)
            {
                rot.EnumRunning(out IEnumMoniker enumMoniker);
                if (enumMoniker != null)
                {
                    IMoniker[] monikers = new IMoniker[1];
                    CreateBindCtx(0, out IBindCtx bindCtx);

                    while (enumMoniker.Next(1, monikers, IntPtr.Zero) == 0)
                    {
                        try
                        {
                            monikers[0].GetDisplayName(bindCtx, null, out string displayName);
                            if (!string.IsNullOrEmpty(displayName) &&
                                displayName.StartsWith("!VisualStudio.DTE.", StringComparison.OrdinalIgnoreCase) &&
                                displayName.EndsWith(pidSuffix, StringComparison.OrdinalIgnoreCase))
                            {
                                if (rot.GetObject(monikers[0], out object obj) == 0 && obj is DTE2 dte)
                                {
                                    // Verify DTE is responsive before returning
                                    _ = dte.Version;
                                    return dte;
                                }
                            }
                        }
                        catch
                        {
                            // Retry while VS initializes COM interfaces
                        }
                    }
                }
            }

            System.Threading.Thread.Sleep(500);
        }

        throw new TimeoutException($"Timed out waiting for DTE instance in ROT for PID {process.Id}.");
    }

    private void EnsureMiniUnrealBuilt()
    {
        string exePath = Path.Combine(MiniUnrealBinariesDir, "UnrealEditor.exe");
        string modAPath = Path.Combine(MiniUnrealBinariesDir, "UnrealEditor-ModuleA.dll");
        string modBPath = Path.Combine(MiniUnrealBinariesDir, "UnrealEditor-ModuleB.dll");

        if (File.Exists(exePath) && File.Exists(modAPath) && File.Exists(modBPath))
        {
            return;
        }

        string msbuildPath = LocateMsBuildPath();
        Console.WriteLine($"[Fixture] Building MiniUnreal C++ fixture with {msbuildPath}...");
        var psi = new ProcessStartInfo
        {
            FileName = msbuildPath,
            Arguments = $"\"{MiniUnrealSolutionPath}\" /p:Configuration=Debug /p:Platform=x64 /nr:false /v:minimal",
            WorkingDirectory = RepoRoot,
            UseShellExecute = false
        };

        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start MSBuild for MiniUnreal.");
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"MSBuild failed to build MiniUnreal.sln (exit code {proc.ExitCode}).");
        }
    }

    private static string LocateRepoRoot()
    {
        string? dir = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "VSOnDemandSymbols.slnx")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException("Could not locate repository root containing VSOnDemandSymbols.slnx.");
    }

    private static string LocateDevenvPath()
    {
        string? envOverride = Environment.GetEnvironmentVariable("VSONDEMANDSYMBOLS_DEVENV");
        if (!string.IsNullOrWhiteSpace(envOverride) && File.Exists(envOverride))
        {
            return envOverride!;
        }

        string vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio",
            "Installer",
            "vswhere.exe");

        if (File.Exists(vswhere))
        {
            var psi = new ProcessStartInfo
            {
                FileName = vswhere,
                Arguments = "-latest -prerelease -property productPath",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            string? output = proc?.StandardOutput.ReadToEnd()?.Trim();
            proc?.WaitForExit();
            if (!string.IsNullOrWhiteSpace(output) && File.Exists(output))
            {
                return output!;
            }
        }

        throw new FileNotFoundException("Could not locate devenv.exe via vswhere.");
    }

    private static string LocateMsBuildPath()
    {
        string vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio",
            "Installer",
            "vswhere.exe");

        if (File.Exists(vswhere))
        {
            var psi = new ProcessStartInfo
            {
                FileName = vswhere,
                Arguments = "-latest -prerelease -requires Microsoft.Component.MSBuild -find \"MSBuild\\**\\Bin\\MSBuild.exe\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            string? output = proc?.StandardOutput.ReadToEnd()?
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            proc?.WaitForExit();
            if (!string.IsNullOrWhiteSpace(output) && File.Exists(output))
            {
                return output!;
            }
        }

        throw new FileNotFoundException("Could not locate MSBuild.exe via vswhere.");
    }

    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable prot);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IBindCtx ppbc);
}

