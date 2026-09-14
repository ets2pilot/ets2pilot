using Microsoft.Extensions.Logging;
using NReco.Logging.File;

namespace Etssd.Core;

public static class EtssdLogging
{
    /// <summary>建日志工厂，文件日志固定开启，configure 追加控制台或 GUI 等去向。</summary>
    public static ILoggerFactory CreateFactory(string role, Action<ILoggingBuilder>? configure = null)
    {
        Directory.CreateDirectory(AppPaths.LogDir);
        return LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddFile(AppPaths.LogFile(role), append: true);
            configure?.Invoke(builder);
        });
    }
}
