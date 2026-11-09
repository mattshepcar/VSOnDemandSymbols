using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VSOnDemandSymbols.Core.Abstractions;

namespace VSOnDemandSymbols.Logging;

/// <summary>
/// Writes extension log messages to the "On-Demand Symbols" Output window pane (SPEC §4.1).
/// Non-blocking and safe to call from any thread via <see cref="IVsOutputWindowPane.OutputStringThreadSafe"/>.
/// </summary>
public sealed class OutputLog : ILogger
{
    public static readonly Guid PaneGuid = new("d2b57819-4a12-4c8d-9e3f-7152e89c01a1");
    public const string PaneTitle = "On-Demand Symbols";

    private readonly IVsOutputWindowPane? _pane;
    private readonly string? _mirrorLogFilePath;
    private readonly object _fileLock = new();
    private int _level = (int)LogLevel.Info;

    public LogLevel Level
    {
        get => (LogLevel)Volatile.Read(ref _level);
        set => Volatile.Write(ref _level, (int)value);
    }

    public OutputLog(
        IVsOutputWindowPane? pane,
        LogLevel initialLevel = LogLevel.Info)
    {
        _pane = pane;
        Level = initialLevel;
        _mirrorLogFilePath = Environment.GetEnvironmentVariable("VSONDEMANDSYMBOLS_LOG_FILE");
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

        return new OutputLog(pane, initialLevel);
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
            WriteLine(message);
        }
    }

    public void Diagnostic(string message)
    {
        if (IsEnabled(LogLevel.Diagnostic))
        {
            WriteLine($"[Diagnostic] {message}");
        }
    }

    public void Warning(string message)
    {
        if (Level != LogLevel.Off)
        {
            WriteLine($"[Warning] {message}");
        }
    }

    public void Error(string message, Exception? ex = null)
    {
        if (Level != LogLevel.Off)
        {
            string details = ex != null ? $"{message} ({ex.GetType().Name}: {ex.Message})" : message;
            WriteLine($"[Error] {details}");
        }
    }

    private void WriteLine(string message)
    {
        string formatted = $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}";

        if (!string.IsNullOrWhiteSpace(_mirrorLogFilePath))
        {
            try
            {
                lock (_fileLock)
                {
                    File.AppendAllText(_mirrorLogFilePath!, formatted);
                }
            }
            catch
            {
            }
        }

        if (_pane == null)
        {
            return;
        }

        try
        {
#pragma warning disable VSTHRD010 // OutputStringThreadSafe is explicitly thread-safe on background threads
            _pane.OutputStringThreadSafe(formatted);
#pragma warning restore VSTHRD010
        }
        catch
        {
            // Ignore output window write errors
        }
    }
}

