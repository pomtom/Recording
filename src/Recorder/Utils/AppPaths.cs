using System.IO;

namespace Recorder.Utils;

/// <summary>
/// Every location the app reads or writes, resolved once.
/// </summary>
/// <remarks>
/// The app is portable-first: if a <c>settings.json</c> sits next to the executable that copy wins,
/// so a USB stick carries its own configuration. Otherwise everything lives under
/// <c>%LOCALAPPDATA%\PomtomRecorder</c>, which is always writable even when the exe is on read-only media.
/// </remarks>
public static class AppPaths
{
    public const string AppFolderName = "PomtomRecorder";
    public const string SettingsFileName = "settings.json";

    /// <summary>Directory containing the running executable (the extracted host dir for single-file builds).</summary>
    public static string ExeDirectory { get; } = ResolveExeDirectory();

    /// <summary><c>%LOCALAPPDATA%\PomtomRecorder</c>. Created on demand.</summary>
    public static string LocalDataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

    public static string LogDirectory => Path.Combine(LocalDataDirectory, "logs");

    /// <summary>Where the extracted ffmpeg.exe and the encoder-probe cache live.</summary>
    public static string BinDirectory => Path.Combine(LocalDataDirectory, "bin");

    /// <summary>Journal sidecars for recordings that have not been finalized yet.</summary>
    public static string PendingDirectory => Path.Combine(LocalDataDirectory, "pending");

    /// <summary>settings.json beside the exe, if present — portable mode.</summary>
    public static string PortableSettingsPath => Path.Combine(ExeDirectory, SettingsFileName);

    public static string RoamingSettingsPath => Path.Combine(LocalDataDirectory, SettingsFileName);

    /// <summary>True when a settings file exists next to the exe.</summary>
    public static bool IsPortableMode => File.Exists(PortableSettingsPath);

    public static string SettingsPath => IsPortableMode ? PortableSettingsPath : RoamingSettingsPath;

    /// <summary>
    /// Default output folder used when the configured one is unusable.
    /// </summary>
    /// <remarks>
    /// Deliberately built from the profile path rather than <c>SpecialFolder.MyVideos</c>. On
    /// machines with OneDrive Known Folder Move, MyVideos resolves inside the synced OneDrive
    /// folder, and dropping multi-gigabyte recordings there would kick off an upload of every
    /// take. The literal profile path stays local.
    /// </remarks>
    public static string FallbackOutputFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos", "Recordings");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(LocalDataDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(BinDirectory);
        Directory.CreateDirectory(PendingDirectory);
    }

    private static string ResolveExeDirectory()
    {
        // AppContext.BaseDirectory points at the extraction folder for single-file builds, which is
        // not where the user's exe lives. ProcessPath is the real .exe on disk.
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath))
        {
            var dir = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrEmpty(dir)) return dir;
        }
        return AppContext.BaseDirectory;
    }
}
