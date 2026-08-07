using System.IO;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Recorder.Utils;

/// <summary>
/// Minimal logging facade. Warnings and errors only — no analytics, no telemetry, no info spam.
/// </summary>
/// <remarks>
/// Logging must never be able to take the app down, so every call is guarded: if the log file
/// cannot be opened (read-only media, locked file) the logger silently degrades to a sink that
/// drops everything rather than throwing on a background capture thread.
/// </remarks>
public static class Log
{
    private static ILogger _logger = Serilog.Core.Logger.None;
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            _logger = new LoggerConfiguration()
                .MinimumLevel.Warning()
                .WriteTo.File(
                    path: Path.Combine(AppPaths.LogDirectory, "recorder-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    fileSizeLimitBytes: 8L * 1024 * 1024,
                    // Note: rollOnFileSizeLimit and shared are mutually exclusive in Serilog —
                    // enabling both makes CreateLogger throw and silently disables logging.
                    rollOnFileSizeLimit: true,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();
        }
        catch
        {
            _logger = Serilog.Core.Logger.None;
        }
    }

    public static void Warn(string message)
    {
        try { _logger.Warning(message); } catch { /* logging must never throw */ }
    }

    public static void Warn(Exception ex, string message)
    {
        try { _logger.Warning(ex, message); } catch { }
    }

    public static void Error(string message)
    {
        try { _logger.Error(message); } catch { }
    }

    public static void Error(Exception ex, string message)
    {
        try { _logger.Error(ex, message); } catch { }
    }

    public static void Shutdown()
    {
        try { (_logger as IDisposable)?.Dispose(); } catch { }
        _logger = Serilog.Core.Logger.None;
    }
}
