using System.IO;
using Recorder.Platform;

namespace Recorder.Utils;

/// <summary>
/// Every location the app reads or writes, resolved once.
/// </summary>
/// <remarks>
/// <para>The app is portable-first: if a <c>settings.json</c> sits next to the program that copy
/// wins, so a USB stick carries its own configuration. Otherwise everything lives under the
/// platform's per-user data folder, which is always writable even when the program is on read-only
/// media.</para>
///
/// <para>The handful of locations that differ between operating systems come from an
/// <see cref="IPlatformPaths"/> the host installs with <see cref="Initialize"/> before anything else
/// runs; everything below is derived from those.</para>
/// </remarks>
public static class AppPaths
{
    public const string AppFolderName = "PomtomRecorder";
    public const string SettingsFileName = "settings.json";

    private static IPlatformPaths? _platform;

    /// <summary>
    /// Installs the platform's locations. Must be the first thing the host does, before settings or
    /// logging are touched.
    /// </summary>
    public static void Initialize(IPlatformPaths platform) =>
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));

    private static IPlatformPaths Platform =>
        _platform ?? throw new InvalidOperationException("AppPaths.Initialize has not been called.");

    /// <summary>Directory containing the running program; where a portable settings.json is looked for.</summary>
    public static string ExeDirectory => Platform.ExeDirectory;

    /// <summary>The per-user data folder (<c>%LOCALAPPDATA%\PomtomRecorder</c> on Windows).</summary>
    public static string LocalDataDirectory => Platform.LocalDataDirectory;

    public static string LogDirectory => Path.Combine(LocalDataDirectory, "logs");

    /// <summary>Where an extracted ffmpeg and the encoder-probe cache live.</summary>
    public static string BinDirectory => Path.Combine(LocalDataDirectory, "bin");

    /// <summary>Journal sidecars for recordings that have not been finalized yet.</summary>
    public static string PendingDirectory => Path.Combine(LocalDataDirectory, "pending");

    /// <summary>settings.json beside the program, if present — portable mode.</summary>
    public static string PortableSettingsPath => Path.Combine(ExeDirectory, SettingsFileName);

    public static string RoamingSettingsPath => Path.Combine(LocalDataDirectory, SettingsFileName);

    /// <summary>True when a settings file exists next to the program.</summary>
    public static bool IsPortableMode => File.Exists(PortableSettingsPath);

    public static string SettingsPath => IsPortableMode ? PortableSettingsPath : RoamingSettingsPath;

    /// <summary>Output folder a brand-new settings file starts with.</summary>
    public static string DefaultOutputFolder => Platform.DefaultOutputFolder;

    /// <summary>Default output folder used when the configured one is unusable.</summary>
    public static string FallbackOutputFolder => Platform.FallbackOutputFolder;

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(LocalDataDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(BinDirectory);
        Directory.CreateDirectory(PendingDirectory);
    }
}
