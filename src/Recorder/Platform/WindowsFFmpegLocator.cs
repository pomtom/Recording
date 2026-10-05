using System.IO;
using System.IO.Compression;
using System.Reflection;
using Recorder.Encoding;
using Recorder.Utils;

namespace Recorder.Platform;

/// <summary>
/// Finds a usable ffmpeg.exe, extracting the embedded copy if necessary.
/// </summary>
/// <remarks>
/// The published app is a single file, so ffmpeg travels inside it as an embedded resource and is
/// unpacked once into <c>%LOCALAPPDATA%\PomtomRecorder\bin</c>. The extracted copy is stamped with
/// the app version and payload size, so upgrading the app automatically replaces a stale ffmpeg
/// instead of silently reusing it.
/// <para>
/// A side-by-side ffmpeg.exe wins over the embedded one, which makes it easy to swap in a different
/// build for troubleshooting without rebuilding the app.
/// </para>
/// <para>
/// This class has to live in the Windows app rather than in Core: the resource is embedded in
/// Recorder.exe, and <see cref="Assembly.GetExecutingAssembly"/> is what finds it.
/// </para>
/// </remarks>
public sealed class WindowsFFmpegLocator : IFFmpegLocator
{
    private const string ResourceName = "Recorder.assets.ffmpeg.exe.gz";

    public string UnavailableMessage =>
        "FFmpeg could not be located. Place ffmpeg.exe next to Recorder.exe or install it on PATH.";

    public string? TryLocate()
    {
        // 1. Next to the exe — an explicit override, and how the "ship alongside" layout works.
        var sideBySide = Path.Combine(AppPaths.ExeDirectory, "ffmpeg.exe");
        if (File.Exists(sideBySide)) return sideBySide;

        // 2/3. The extracted copy, refreshed when the embedded payload has changed.
        if (HasEmbeddedResource())
        {
            try
            {
                return EnsureExtracted();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to extract the embedded ffmpeg.exe.");
            }
        }

        // 4. Anything on PATH.
        return FindOnPath("ffmpeg.exe");
    }

    /// <summary>
    /// Ensures the extracted ffmpeg.exe matches the embedded payload, extracting it if not.
    /// </summary>
    /// <remarks>
    /// <para><b>The fast path never touches the embedded resource.</b> In a compressed single-file
    /// publish, merely opening the resource stream forces the runtime to decompress the whole
    /// ~100 MB payload into memory — on every launch, for nothing. So an unchanged install is
    /// detected from the stamp file and the extracted binary alone, and the resource is opened only
    /// when an extraction is genuinely needed.</para>
    ///
    /// <para>The extraction itself streams for the same reason: buffering 100 MB into a
    /// <c>byte[]</c>, even transiently, parks large-object-heap arrays for the life of the process.</para>
    /// </remarks>
    private static string EnsureExtracted()
    {
        Directory.CreateDirectory(AppPaths.BinDirectory);

        var target = Path.Combine(AppPaths.BinDirectory, "ffmpeg.exe");
        var stampPath = Path.Combine(AppPaths.BinDirectory, "ffmpeg.stamp");
        var stamp = typeof(WindowsFFmpegLocator).Assembly.GetName().Version?.ToString() ?? "0";

        if (File.Exists(target) && File.Exists(stampPath))
        {
            try
            {
                if (string.Equals(File.ReadAllText(stampPath).Trim(), stamp, StringComparison.Ordinal) &&
                    new FileInfo(target).Length > 0)
                {
                    return target;
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Could not read the ffmpeg stamp; re-extracting.");
            }
        }

        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new FFmpegUnavailableException("The embedded ffmpeg resource is missing.");

        // Write to a temp name then move, so a half-written ffmpeg.exe is never observable.
        var temp = target + ".tmp";
        using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        using (var gzip = new GZipStream(resource, CompressionMode.Decompress))
        {
            gzip.CopyTo(output, 1 << 20);
        }

        if (File.Exists(target)) File.Delete(target);
        File.Move(temp, target);
        File.WriteAllText(stampPath, stamp);

        Log.Warn($"Extracted the bundled ffmpeg to '{target}'.");
        return target;
    }

    /// <summary>
    /// True when this build carries an embedded ffmpeg.
    /// </summary>
    /// <remarks>
    /// Uses the resource <em>name</em> list rather than opening the stream, which would decompress
    /// the payload — see <see cref="EnsureExtracted"/>.
    /// </remarks>
    private static bool HasEmbeddedResource()
    {
        try
        {
            return Array.IndexOf(Assembly.GetExecutingAssembly().GetManifestResourceNames(), ResourceName) >= 0;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Could not enumerate embedded resources.");
            return false;
        }
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // Malformed PATH entries are common; skip them.
            }
        }
        return null;
    }
}
