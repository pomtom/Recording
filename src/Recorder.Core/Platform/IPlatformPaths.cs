namespace Recorder.Platform;

/// <summary>The locations that differ between operating systems.</summary>
/// <remarks>Consumed only through <see cref="Utils.AppPaths"/>, which derives everything else.</remarks>
public interface IPlatformPaths
{
    /// <summary>Per-user, always-writable folder for settings, logs, ffmpeg and journals.</summary>
    string LocalDataDirectory { get; }

    /// <summary>The folder a portable <c>settings.json</c> is looked for in — next to the program.</summary>
    string ExeDirectory { get; }

    /// <summary>Output folder a brand-new settings file starts with.</summary>
    string DefaultOutputFolder { get; }

    /// <summary>Output folder used when the configured one is unusable.</summary>
    string FallbackOutputFolder { get; }
}
