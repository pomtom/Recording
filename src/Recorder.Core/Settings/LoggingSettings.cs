using System.Text.Json.Serialization;

namespace Recorder.Settings;

/// <summary>How much detail reaches the log file.</summary>
public enum LogLevel
{
    Error,
    Warning,

    /// <summary>Adds routine lifecycle lines. Useful when diagnosing, noisy otherwise.</summary>
    Information,
}

/// <summary>Log verbosity and retention. Still no analytics and no telemetry.</summary>
public sealed class LoggingSettings
{
    /// <summary>One of <c>Error</c>, <c>Warning</c>, <c>Information</c>.</summary>
    public string MinimumLevel { get; set; } = "Warning";

    /// <summary>How many daily log files to keep.</summary>
    public int RetainedDays { get; set; } = 7;

    public int MaxFileSizeMb { get; set; } = 8;

    [JsonIgnore]
    public LogLevel MinimumLevelValue => ParseLevel(MinimumLevel);

    public static LogLevel ParseLevel(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "error" or "err" => LogLevel.Error,
        "information" or "info" => LogLevel.Information,
        _ => LogLevel.Warning,
    };

    public static string FormatLevel(LogLevel level) => level.ToString();

    public LoggingSettings Clone() => (LoggingSettings)MemberwiseClone();
}
