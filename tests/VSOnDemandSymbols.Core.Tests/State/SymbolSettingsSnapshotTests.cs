using System;
using System.Collections.Generic;
using VSOnDemandSymbols.Core.State;
using Xunit;

namespace VSOnDemandSymbols.Core.Tests.State;

public class SymbolSettingsSnapshotTests
{
    [Fact]
    public void Clone_CreatesDeepCopy()
    {
        var original = new SymbolSettingsSnapshot
        {
            IsManualLoad = true,
            IsLoadAdjacent = false,
            AutomaticSymbolLoad = false,
            UseMsSymbolServers = true,
            UseNugetSymbolServers = false,
            SymbolPaths = new List<SymbolOptionBackupDto>
            {
                new() { Name = @"C:\Cache", IsEnabled = true }
            },
            IncludeList = new List<SymbolOptionBackupDto>
            {
                new() { Name = "UnrealEditor*.exe", IsEnabled = true }
            }
        };

        var clone = original.Clone();

        Assert.True(clone.ManagedFieldsEqual(original));
        Assert.True(clone.ServerSettingsEqual(original));

        // Mutate clone and ensure original does not change
        clone.IncludeList.Add(new SymbolOptionBackupDto { Name = "Extra.dll", IsEnabled = true });
        Assert.Single(original.IncludeList);
        Assert.Equal(2, clone.IncludeList.Count);
        Assert.False(clone.ManagedFieldsEqual(original));
    }

    [Fact]
    public void ManagedFieldsEqual_OrderIndependentIncludeList()
    {
        var a = new SymbolSettingsSnapshot
        {
            IsManualLoad = true,
            IncludeList = new List<SymbolOptionBackupDto>
            {
                new() { Name = "ModuleA.dll", IsEnabled = true },
                new() { Name = "ModuleB.dll", IsEnabled = false }
            }
        };

        var b = new SymbolSettingsSnapshot
        {
            IsManualLoad = true,
            IncludeList = new List<SymbolOptionBackupDto>
            {
                new() { Name = "ModuleB.dll", IsEnabled = false },
                new() { Name = "ModuleA.dll", IsEnabled = true }
            }
        };

        Assert.True(a.ManagedFieldsEqual(b));

        // Different enabled state
        b.IncludeList[0].IsEnabled = true;
        Assert.False(a.ManagedFieldsEqual(b));
    }

    [Fact]
    public void ServerSettingsEqual_ChecksServersAndPaths()
    {
        var a = new SymbolSettingsSnapshot
        {
            UseMsSymbolServers = true,
            SymbolPaths = new List<SymbolOptionBackupDto>
            {
                new() { Name = @"C:\Syms", IsEnabled = true }
            }
        };

        var b = new SymbolSettingsSnapshot
        {
            UseMsSymbolServers = true,
            SymbolPaths = new List<SymbolOptionBackupDto>
            {
                new() { Name = @"C:\Syms", IsEnabled = true }
            }
        };

        Assert.True(a.ServerSettingsEqual(b));

        b.UseNugetSymbolServers = true;
        Assert.False(a.ServerSettingsEqual(b));
    }
}
