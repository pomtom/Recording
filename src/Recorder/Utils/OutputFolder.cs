using System.IO;

namespace Recorder.Utils;

/// <summary>
/// Decides where a recording actually gets written.
/// </summary>
/// <remarks>
/// The shipped default is <c>D:\Recordings</c>, which does not exist on most machines. Rather than
/// failing at the moment the user hits record, the configured folder is probed for real writability
/// (create the directory, write and delete a marker file) and silently swapped for
/// <c>%USERPROFILE%\Videos\Recordings</c> when that fails. Existence is not enough on its own —
/// a folder can exist and still reject writes.
/// </remarks>
public static class OutputFolder
{
    public sealed record Result(string Path, bool UsedFallback, string? Reason);

    public static Result Resolve(string? configured)
    {
        string? reason = "No folder configured.";

        if (!string.IsNullOrWhiteSpace(configured) && TryPrepare(configured, out reason))
            return new Result(System.IO.Path.GetFullPath(configured), false, null);

        var fallback = AppPaths.FallbackOutputFolder;
        if (TryPrepare(fallback, out var fallbackReason))
        {
            Log.Warn($"Output folder '{configured}' unusable ({reason}); using '{fallback}'.");
            return new Result(fallback, true, reason);
        }

        // Both unusable: the temp folder always works and is better than losing the recording.
        var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), AppPaths.AppFolderName);
        Directory.CreateDirectory(temp);
        Log.Error($"Neither '{configured}' ({reason}) nor '{fallback}' ({fallbackReason}) is writable; using '{temp}'.");
        return new Result(temp, true, reason);
    }

    /// <summary>Creates the folder if needed and confirms a file can actually be written into it.</summary>
    public static bool TryPrepare(string folder, out string? reason)
    {
        reason = null;
        try
        {
            Directory.CreateDirectory(folder);

            var probe = System.IO.Path.Combine(folder, $".write-probe-{Guid.NewGuid():N}");
            using (var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                           bufferSize: 1, FileOptions.DeleteOnClose))
            {
                fs.WriteByte(0);
            }
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Builds the path for a new recording from the user's filename template.
    /// </summary>
    /// <remarks>
    /// The default template reproduces the spec's <c>YYYY-MM-DD_HH-MM-SS.mp4</c> exactly. If the
    /// expanded name is already taken, <c>{counter}</c> is re-expanded with the next index — so a
    /// template that mentions the counter puts it where the user asked for it, and one that does not
    /// gets it appended.
    /// </remarks>
    public static string BuildRecordingPath(string folder, DateTime localTime, string? template = null, string? monitorLabel = null)
    {
        // A template that places {counter} itself expands to a different name each pass, so appending
        // a second suffix on top would read as "recording_3_3". One or the other, not both.
        var templatesCounter = (template ?? FilenameTemplate.Default)
            .Contains("{counter}", StringComparison.OrdinalIgnoreCase);

        for (var counter = 1; counter < 10_000; counter++)
        {
            var stem = FilenameTemplate.Expand(template, localTime, monitorLabel, counter);
            var name = templatesCounter || counter == 1 ? stem : $"{stem}_{counter}";
            var candidate = System.IO.Path.Combine(folder, name + ".mp4");

            if (!File.Exists(candidate) && !File.Exists(candidate + ".part")) return candidate;
        }

        // Pathological: ten thousand collisions. A timestamp with sub-second precision always wins.
        var unique = localTime.ToString("yyyy-MM-dd_HH-mm-ss-fff");
        return System.IO.Path.Combine(folder, unique + ".mp4");
    }
}
