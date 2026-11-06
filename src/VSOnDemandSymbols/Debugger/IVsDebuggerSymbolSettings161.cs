using System;
using System.Runtime.InteropServices;

namespace VSOnDemandSymbols.Debugger;

/// <summary>
/// COM import for VS 16.1+ <c>IVsDebuggerSymbolSettings161</c>
/// (defined in <c>Microsoft.VisualStudio.Debugger.Interop.16.0.dll</c>).
/// Controls the NuGet symbol servers setting (<c>UseNugetSymbolServers</c>).
/// </summary>
[ComImport]
[Guid("20bc5bab-4e63-49e4-a7e4-611e705e0a05")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVsDebuggerSymbolSettings161
{
    void set_UseNugetSymbolServers([In] bool flag);
    bool get_UseNugetSymbolServers();
}

