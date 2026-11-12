using System;
using System.Threading;
using VSOnDemandSymbols.Core.Abstractions;

namespace VSOnDemandSymbols.Debugger;

/// <summary>
/// Cross-process named mutex providing mutual exclusion per Visual Studio hive (ADR-16).
/// Ensures only one devenv instance reconciles symbol settings at a time.
/// </summary>
public sealed class HiveLock : IDisposable
{
    public const int DefaultTimeoutMs = 2000;

    private readonly Mutex _mutex;
    private bool _hasHandle;
    private bool _disposed;

    private HiveLock(Mutex mutex, bool hasHandle)
    {
        _mutex = mutex;
        _hasHandle = hasHandle;
    }

    /// <summary>
    /// Attempts to acquire the named mutex for the specified VS hive within <paramref name="timeoutMs"/>.
    /// Returns a disposable <see cref="HiveLock"/> if acquired; otherwise <see langword="null"/>.
    /// </summary>
    public static HiveLock? TryAcquire(string vsHiveId, ILogger logger, int timeoutMs = DefaultTimeoutMs)
    {
        string safeHiveId = vsHiveId.Replace('\\', '_').Replace('/', '_');
        string mutexName = $@"Local\VSOnDemandSymbols.{safeHiveId}";

        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: false, mutexName);
            bool hasHandle;
            try
            {
                hasHandle = mutex.WaitOne(timeoutMs, exitContext: false);
            }
            catch (AbandonedMutexException)
            {
                // Previous owner process crashed or exited without releasing the mutex.
                // The OS grants ownership to this thread.
                logger.Warning($"Acquired abandoned hive lock '{mutexName}'. Previous process may have exited unexpectedly.");
                hasHandle = true;
            }

            if (hasHandle)
            {
                return new HiveLock(mutex, hasHandle: true);
            }

            logger.Warning($"Timed out after {timeoutMs} ms waiting for hive lock '{mutexName}'.");
            mutex.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            logger.Warning($"Failed to acquire hive lock '{mutexName}': {ex.Message}");
            mutex?.Dispose();
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hasHandle)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
                // Ignore release errors on dispose
            }
            _hasHandle = false;
        }

        _mutex.Dispose();
    }
}
