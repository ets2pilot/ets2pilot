using Microsoft.Extensions.Logging;

namespace Etssd.Gui;

/// <param name="Category">logger 类别名的末两段，如 Webhook.SubscriberRegistry。</param>
public sealed record LogEntry(DateTime Time, LogLevel Level, string Category, string Message)
{
    public string LevelText => Level switch
    {
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRIT",
        _ => Level.ToString().ToUpperInvariant(),
    };
}

/// <summary>把日志条目推给日志页。事件在写日志的线程上触发，订阅方自行切到 UI 线程。</summary>
public sealed class UiLoggerProvider : ILoggerProvider
{
    public event Action<LogEntry>? EntryLogged;

    public ILogger CreateLogger(string categoryName) => new UiLogger(this, ShortCategory(categoryName));

    public void Dispose()
    {
    }

    private static string ShortCategory(string category)
    {
        var parts = category.Split('.');
        return string.Join('.', parts[Math.Max(0, parts.Length - 2)..]);
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
            var message = formatter(state, exception);
            if (exception is not null)
            {
                message += Environment.NewLine + exception;
            }
            owner.EntryLogged?.Invoke(new LogEntry(DateTime.Now, logLevel, category, message));
        }
    }
}
