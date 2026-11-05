using System;
using System.IO;
using System.Linq;
using VSOnDemandSymbols.Core.Abstractions;
using VSOnDemandSymbols.Core.Modules;
using VSOnDemandSymbols.Core.Policy;
using Xunit;

namespace VSOnDemandSymbols.Core.Tests.Policy;

public class RecentlyBuiltScannerTests
{
    private readonly TestClock _clock;
    private readonly InMemoryFileSystem _fileSystem;
    private readonly RecentlyBuiltScanner _scanner;

    public RecentlyBuiltScannerTests()
    {
        _clock = new TestClock(new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc));
        _fileSystem = new InMemoryFileSystem();
        _scanner = new RecentlyBuiltScanner(_clock, _fileSystem, NullLogger.Instance);
    }

    [Fact]
    public void Scan_WithLastSessionStartUtc_DetectsNewerFiles_RB1()
    {
        string binDir = @"C:\Project\Binaries\Win64";
        _fileSystem.CreateDirectory(binDir);

        var lastSessionStart = new DateTime(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc);

        // File 1: written at 09:00 (before session)
        string oldFile = Path.Combine(binDir, "UnrealEditor-OldModule.dll");
        _fileSystem.WriteAllText(oldFile, "content");
        SetFileTimestamp(oldFile, new DateTime(2026, 11, 5, 9, 0, 0, DateTimeKind.Utc));

        // File 2: written at 11:00 (after session)
        string newFile = Path.Combine(binDir, "UnrealEditor-NewModule.dll");
        _fileSystem.WriteAllText(newFile, "content");
        SetFileTimestamp(newFile, new DateTime(2026, 11, 5, 11, 0, 0, DateTimeKind.Utc));

        var result = _scanner.Scan(
            new[] { binDir },
            lastSessionStart,
            file =>
            {
                if (file.Contains("NewModule"))
                    return (new ModuleKey(ModuleKind.Unreal, "NewModule"), false);
                if (file.Contains("OldModule"))
                    return (new ModuleKey(ModuleKind.Unreal, "OldModule"), false);
                return (null, false);
            });

        Assert.False(result.IsGuardTripped);
        Assert.Single(result.Modules);
        Assert.Equal("NewModule", result.Modules[0].Name);
    }

    [Fact]
    public void Scan_FirstSession_UsesFirstSessionWindow_RB3()
    {
        string binDir = @"C:\Project\Binaries\Win64";
        _fileSystem.CreateDirectory(binDir);

        // Current time: 12:00. Default window: 60 minutes (cutoff is 11:00)
        // File 1: written at 10:30 (outside 60m window)
        string fileOld = Path.Combine(binDir, "UnrealEditor-ModA.dll");
        _fileSystem.WriteAllText(fileOld, "content");
        SetFileTimestamp(fileOld, new DateTime(2026, 11, 5, 10, 30, 0, DateTimeKind.Utc));

        // File 2: written at 11:30 (inside 60m window)
        string fileNew = Path.Combine(binDir, "UnrealEditor-ModB.dll");
        _fileSystem.WriteAllText(fileNew, "content");
        SetFileTimestamp(fileNew, new DateTime(2026, 11, 5, 11, 30, 0, DateTimeKind.Utc));

        var result = _scanner.Scan(
            new[] { binDir },
            lastSessionStartUtc: null,
            file =>
            {
                if (file.Contains("ModA")) return (new ModuleKey(ModuleKind.Unreal, "ModA"), false);
                if (file.Contains("ModB")) return (new ModuleKey(ModuleKind.Unreal, "ModB"), false);
                return (null, false);
            });

        Assert.False(result.IsGuardTripped);
        Assert.Single(result.Modules);
        Assert.Equal("ModB", result.Modules[0].Name);
    }

    [Fact]
    public void Scan_MassRebuildGuardTrips_WhenExceedsMaxModules_KeepsLiveCodingExempt_RB4_RB5()
    {
        string binDir = @"C:\Project\Binaries\Win64";
        _fileSystem.CreateDirectory(binDir);

        _scanner.Options.MaxModules = 2; // threshold of 2

        var lastSessionStart = new DateTime(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc);
        var writeTime = new DateTime(2026, 11, 5, 11, 0, 0, DateTimeKind.Utc);

        // Add 3 rebuilt standard modules (exceeds threshold 2)
        for (int i = 1; i <= 3; i++)
        {
            string f = Path.Combine(binDir, $"UnrealEditor-Mod{i}.dll");
            _fileSystem.WriteAllText(f, "content");
            SetFileTimestamp(f, writeTime);
        }

        // Add 1 Live Coding patch (exempt from guard)
        string patchFile = Path.Combine(binDir, "UnrealEditor-PatchMod.patch_0.exe");
        _fileSystem.WriteAllText(patchFile, "content");
        SetFileTimestamp(patchFile, writeTime);

        var result = _scanner.Scan(
            new[] { binDir },
            lastSessionStart,
            file =>
            {
                if (file.Contains("PatchMod"))
                    return (new ModuleKey(ModuleKind.Unreal, "PatchMod"), true); // live coding exempt

                for (int i = 1; i <= 3; i++)
                {
                    if (file.Contains($"Mod{i}"))
                        return (new ModuleKey(ModuleKind.Unreal, $"Mod{i}"), false);
                }
                return (null, false);
            });

        Assert.True(result.IsGuardTripped);
        Assert.Equal(4, result.TotalDetectedCount);
        // Only the exempt Live Coding module survives the guard
        Assert.Single(result.Modules);
        Assert.Equal("PatchMod", result.Modules[0].Name);
    }

    private void SetFileTimestamp(string path, DateTime utc)
    {
        // Use reflection to set timestamp in InMemoryFileSystem
        var field = typeof(InMemoryFileSystem).GetField("_timestamps", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var dict = (System.Collections.Generic.Dictionary<string, DateTime>)field!.GetValue(_fileSystem)!;
        dict[path] = utc;
    }
}

