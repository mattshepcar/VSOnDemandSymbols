using System;

namespace VSOnDemandSymbols.Core.Abstractions;

public enum LogLevel
{
    Off,
    Info,
    Verbose,
    Diagnostic
}

public interface ILogger
{
    LogLevel Level { get; set; }
    bool IsEnabled(LogLevel level);
    void Info(string message);
    void Diagnostic(string message);
    void Warning(string message);
    void Error(string message, Exception? ex = null);
}

public sealed class NullLogger : ILogger
{
    public static readonly NullLogger Instance = new();

    public LogLevel Level { get; set; } = LogLevel.Off;

    public bool IsEnabled(LogLevel level) => false;
    public void Info(string message) { }
    public void Diagnostic(string message) { }
    public void Warning(string message) { }
    public void Error(string message, Exception? ex = null) { }
}

