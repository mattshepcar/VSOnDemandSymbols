using System;
using System.Runtime.InteropServices;

namespace VSOnDemandSymbols.Debugger;

/// <summary>
/// COM import for VS 17.0+ <c>IVsDebuggerSymbolSettings170</c>
/// (defined in <c>Microsoft.VisualStudio.Debugger.Interop.17.0.dll</c>).
/// Controls the "Automatically include additional symbols when needed" setting (<c>AutomaticSymbolLoad</c>).
/// </summary>
[ComImport]
[Guid("aa6086eb-b8f7-4b4a-be6b-37e4dfe1a933")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVsDebuggerSymbolSettings170
{
    void set_AutomaticSymbolLoad([In] bool value);
    bool get_AutomaticSymbolLoad();
}

