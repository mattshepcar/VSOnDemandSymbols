using System;
using Microsoft.VisualStudio.Debugger;
using VSOnDemandSymbols.Core.Modules;

namespace VSOnDemandSymbols.Debugger;

/// <summary>
/// Tracks a live native <see cref="DkmModuleInstance"/> in <see cref="LoadedModuleRegistry"/>.
/// </summary>
public sealed class LoadedModuleInfo
{
    public Guid UniqueId => ModuleInstance.UniqueId;
    public Guid ProcessUniqueId => ModuleInstance.Process.UniqueId;
    public DkmModuleInstance ModuleInstance { get; }
    public string FileName { get; }
    public string FullPath { get; }
    public ModuleKey? Key { get; }
    public bool HadSymbolsAtModuleMap { get; set; }
    public bool LoadedAtStartup { get; set; }
    public bool LoadedOnDemand { get; set; }
    public bool HasSymbols => ModuleInstance.Module != null || LoadedAtStartup || LoadedOnDemand;

    public LoadedModuleInfo(
        DkmModuleInstance moduleInstance,
        string fileName,
        string fullPath,
        ModuleKey? key)
    {
        ModuleInstance = moduleInstance ?? throw new ArgumentNullException(nameof(moduleInstance));
        FileName = fileName ?? string.Empty;
        FullPath = fullPath ?? string.Empty;
        Key = key;
        HadSymbolsAtModuleMap = moduleInstance.Module != null;
    }
}

