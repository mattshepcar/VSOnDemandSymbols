using System;
using System.Runtime.InteropServices;

namespace VSOnDemandSymbols.IntegrationTests;

/// <summary>
/// Standard COM IOleMessageFilter implementation for STA threads driving out-of-process Visual Studio DTE.
/// Automatically retries rejected COM calls (RPC_E_CALL_REJECTED / SERVERCALL_RETRYLATER) when Visual Studio is busy.
/// </summary>
public sealed class OleMessageFilter : IOleMessageFilter
{
    private static IOleMessageFilter? _oldFilter;

    public static void Register()
    {
        IOleMessageFilter newFilter = new OleMessageFilter();
        CoRegisterMessageFilter(newFilter, out _oldFilter);
    }

    public static void Revoke()
    {
        CoRegisterMessageFilter(_oldFilter, out _);
    }

    int IOleMessageFilter.HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo)
    {
        // SERVERCALL_ISHANDLED (0)
        return 0;
    }

    int IOleMessageFilter.RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType)
    {
        // SERVERCALL_RETRYLATER (2): retry after 100ms up to 15 seconds per call
        if (dwRejectType == 2 && dwTickCount < 15000)
        {
            return 100;
        }

        // Cancel call (-1)
        return -1;
    }

    int IOleMessageFilter.MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType)
    {
        // PENDINGMSG_WAITDEFPROCESS (2)
        return 2;
    }

    [DllImport("Ole32.dll")]
    private static extern int CoRegisterMessageFilter(IOleMessageFilter? newFilter, out IOleMessageFilter? oldFilter);
}

[ComImport]
[Guid("00000016-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleMessageFilter
{
    [PreserveSig]
    int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo);

    [PreserveSig]
    int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType);

    [PreserveSig]
    int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType);
}

