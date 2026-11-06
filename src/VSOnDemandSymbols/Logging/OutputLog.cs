using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using VSOnDemandSymbols.Core.Abstractions;

namespace VSOnDemandSymbols.Logging;

/// <summary>
/// Writes extension log messages to the "On-Demand Symbols" Output window pane (SPEC §8.2).
/// Non-blocking from any thread (NF-1, NF-3).
/// </summary>
public sealed class OutputLog : ILogger
{
    public static readonly Guid PaneGuid = new("d2b57819-4a12-4c8d-9e3f-7152e89c01a1");
    public const string PaneTitle = "On-Demand Symbols";

    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly IVsOutputWindowPane? _pane;
    private readonly ConcurrentQueue<string> _pendingLines = new();
    private int _isFlushing;
    private int _level = (int)LogLevel.Info;

    public LogLevel Level
    {
        get => (LogLevel)Volatile.Read(ref _level);
        set => Volatile.Write(ref _level, (int)value);
    }

    public OutputLog(
        JoinableTaskFactory joinableTaskFactory,
        IVsOutputWindowPane? pane,
        LogLevel initialLevel = LogLevel.Info)
    {
        _joinableTaskFactory = joinableTaskFactory ?? throw new ArgumentNullException(nameof(joinableTaskFactory));
        _pane = pane;
        Level = initialLevel;
    }

    public static async Task<OutputLog> CreateAsync(
        AsyncPackage package,
        LogLevel initialLevel = LogLevel.Info,
        CancellationToken cancellationToken = default)
    {
        if (package == null)
        {
            throw new ArgumentNullException(nameof(package));
        }

        await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        IVsOutputWindowPane? pane = null;
        if (await package.GetServiceAsync(typeof(SVsOutputWindow)) is IVsOutputWindow outputWindow)
        {
            Guid paneGuid = PaneGuid;
            outputWindow.CreatePane(ref paneGuid, PaneTitle, fInitVisible: 1, fClearWithSolution: 0);
            outputWindow.GetPane(ref paneGuid, out pane);
        }

        return new OutputLog(package.JoinableTaskFactory, pane, initialLevel);
    }

    public bool IsEnabled(LogLevel level)
    {
        var current = Level;
        if (current == LogLevel.Off || level == LogLevel.Off)
        {
            return false;
        }

        return level <= current;
    }

    public void Info(string message)
    {
        if (IsEnabled(LogLevel.Info))
        {
            EnqueueLine(message);
        }
    }

    public void Verbose(string message)
    {
        if (IsEnabled(LogLevel.Verbose))
        {
            EnqueueLine($"[Verbose] {message}");
        }
    }

    public void Diagnostic(string message)
    {
        if (IsEnabled(LogLevel.Diagnostic))
        {
            EnqueueLine($"[Diagnostic] {message}");
        }
    }

    public void Warning(string message)
    {
        if (Level != LogLevel.Off)
        {
            EnqueueLine($"[Warning] {message}");
        }
    }

    public void Error(string message, Exception? ex = null)
    {
        if (Level != LogLevel.Off)
        {
            string details = ex != null ? $"{message} ({ex.GetType().Name}: {ex.Message})" : message;
            EnqueueLine($"[Error] {details}");
        }
    }

    private void EnqueueLine(string message)
    {
        if (_pane == null)
        {
            return;
        }

        string formatted = $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}";
        _pendingLines.Enqueue(formatted);

        if (Interlocked.CompareExchange(ref _isFlushing, 1, 0) == 0)
        {
            _ = _joinableTaskFactory.RunAsync(FlushQueueAsync);
        }
    }

    private async Task FlushQueueAsync()
    {
        try
        {
            await _joinableTaskFactory.SwitchToMainThreadAsync();

            var sb = new StringBuilder();
            while (true)
            {
                sb.Clear();
                while (_pendingLines.TryDequeue(out string? line))
                {
                    sb.Append(line);
                }

                if (sb.Length > 0 && _pane != null)
                {
                    _pane.OutputStringThreadSafe(sb.ToString());
                }

                Interlocked.Exchange(ref _isFlushing, 0);

                if (_pendingLines.IsEmpty || Interlocked.CompareExchange(ref _isFlushing, 1, 0) != 0)
                {
                    break;
                }
            }
        }
        catch
        {
            Interlocked.Exchange(ref _isFlushing, 0);
        }
    }
}

