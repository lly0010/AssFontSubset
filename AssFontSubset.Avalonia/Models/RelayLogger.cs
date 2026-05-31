using System;
using Microsoft.Extensions.Logging;

namespace AssFontSubset.Avalonia.Models;

/// <summary>
/// Minimal <see cref="ILogger"/> that forwards rendered log lines to a callback,
/// used to surface Core progress in the GUI log panel.
/// </summary>
public sealed class RelayLogger(Action<LogLevel, string> sink) : ILogger
{
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) { return; }
        var message = formatter(state, exception);
        if (exception is not null)
        {
            message += " " + exception.Message;
        }
        sink(logLevel, message);
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
