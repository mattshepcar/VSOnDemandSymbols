using System;
using System.Diagnostics;

namespace VSOnDemandSymbols.Core.Abstractions;

public interface IProcessInfo
{
    int CurrentProcessId { get; }
    DateTime CurrentProcessStartTimeUtc { get; }
    bool IsDevenvProcessAlive(int pid, DateTime expectedStartTimeUtc);
}

public sealed class SystemProcessInfo : IProcessInfo
{
    public static readonly SystemProcessInfo Instance = new();

    private readonly Lazy<int> _currentPid = new(GetCurrentProcessIdSafe);
    private readonly Lazy<DateTime> _currentStartTimeUtc = new(GetCurrentProcessStartTimeUtcSafe);

    public int CurrentProcessId => _currentPid.Value;
    public DateTime CurrentProcessStartTimeUtc => _currentStartTimeUtc.Value;

    public bool IsDevenvProcessAlive(int pid, DateTime expectedStartTimeUtc)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return false;
            }

            string processName = process.ProcessName ?? string.Empty;
            if (!string.Equals(processName, "devenv", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Guard against OS PID reuse: process start time must match expected within +/- 2 seconds.
            if (expectedStartTimeUtc != DateTime.MinValue)
            {
                DateTime actualStartTimeUtc = process.StartTime.ToUniversalTime();
                TimeSpan diff = actualStartTimeUtc - expectedStartTimeUtc;
                if (Math.Abs(diff.TotalSeconds) > 2.0)
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int GetCurrentProcessIdSafe()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            return current.Id;
        }
        catch
        {
            return 0;
        }
    }

    private static DateTime GetCurrentProcessStartTimeUtcSafe()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            return current.StartTime.ToUniversalTime();
        }
        catch
        {
            return DateTime.UtcNow;
        }
    }
}
