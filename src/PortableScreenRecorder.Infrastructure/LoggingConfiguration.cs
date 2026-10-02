using Microsoft.Extensions.Logging;
using Serilog;

namespace PortableScreenRecorder.Infrastructure;

public static class LoggingConfiguration
{
    public static ILoggerFactory Create(string logsDirectory)
    {
        Directory.CreateDirectory(logsDirectory);
        var logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(logsDirectory, "recorder-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true)
            .CreateLogger();
        return LoggerFactory.Create(builder => builder.AddSerilog(logger, dispose: true));
    }
}
