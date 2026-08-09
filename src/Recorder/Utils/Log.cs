using System.IO;
using Recorder.Settings;
using Serilog;
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
    private static readonly object Gate = new();
    private static ILogger _logger = Serilog.Core.Logger.None;
    private static bool _initialized;

    /// <summary>
    /// Brings logging up with the built-in defaults.
    /// </summary>
    /// <remarks>
    /// Called before settings are loaded, because loading them is itself something that can produce
    /// a warning worth keeping. <see cref="Reconfigure"/> then applies the user's preferences once
    /// they are known.
    /// </remarks>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        Configure(LogEventLevel.Warning, retainedFiles: 7, maxFileSizeMb: 8);
    }

    /// <summary>Rebuilds the logger with the user's level and retention.</summary>
    public static void Reconfigure(LoggingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var level = settings.MinimumLevelValue switch
        {
            Settings.LogLevel.Error => LogEventLevel.Error,
            Settings.LogLevel.Information => LogEventLevel.Information,
            _ => LogEventLevel.Warning,
        };

        _initialized = true;
        Configure(level, settings.RetainedDays, settings.MaxFileSizeMb);
    }

    private static void Configure(LogEventLevel level, int retainedFiles, int maxFileSizeMb)
    {
        lock (Gate)
        {
            var previous = _logger;

            try
            {
                Directory.CreateDirectory(AppPaths.LogDirectory);
                _logger = new LoggerConfiguration()
                    .MinimumLevel.Is(level)
                    .WriteTo.File(
                        path: Path.Combine(AppPaths.LogDirectory, "recorder-.log"),
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: retainedFiles,
                        fileSizeLimitBytes: maxFileSizeMb * 1024L * 1024L,
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

            // Release the old file handle only once the new one is open, so a failed reconfigure
            // leaves logging degraded rather than the log file locked by nothing.
            if (!ReferenceEquals(previous, _logger))
            {
                try { (previous as IDisposable)?.Dispose(); } catch { }
            }
        }
    }

    /// <summary>Routine lifecycle detail. Dropped unless the user raised the level to Information.</summary>
    public static void Info(string message)
    {
        try { _logger.Information(message); } catch { }
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
        lock (Gate)
        {
            try { (_logger as IDisposable)?.Dispose(); } catch { }
            _logger = Serilog.Core.Logger.None;
        }
    }
}
