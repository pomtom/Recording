using System.IO;
using Recorder.Utils;

namespace Recorder.Platform;

/// <summary>
/// Windows locations: <c>%LOCALAPPDATA%\PomtomRecorder</c> for app data, <c>D:\Recordings</c> as
/// the starting output folder.
/// </summary>
public sealed class WindowsPaths : IPlatformPaths
{
    public string ExeDirectory { get; } = ResolveExeDirectory();

    public string LocalDataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppPaths.AppFolderName);

    public string DefaultOutputFolder => @"D:\Recordings";

    /// <remarks>
    /// Deliberately built from the profile path rather than <c>SpecialFolder.MyVideos</c>. On
    /// machines with OneDrive Known Folder Move, MyVideos resolves inside the synced OneDrive
    /// folder, and dropping multi-gigabyte recordings there would kick off an upload of every
    /// take. The literal profile path stays local.
    /// </remarks>
    public string FallbackOutputFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos", "Recordings");

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
