using Microsoft.Extensions.Logging;

namespace Etssd.Gui;

/// <summary>把日志行推给日志页。事件在写日志的线程上触发，订阅方自行切到 UI 线程。</summary>
public sealed class UiLoggerProvider : ILoggerProvider
{
    public event Action<string>? EntryLogged;

    public ILogger CreateLogger(string categoryName) => new UiLogger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class UiLogger(UiLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }
            var line = $"{DateTime.Now:HH:mm:ss} [{logLevel}] {category}: {formatter(state, exception)}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }
            owner.EntryLogged?.Invoke(line);
        }
    }
}
