using System.IO;
using System.Text.Json;
using Recorder.Utils;

namespace Recorder.Recovery;

/// <summary>A recording that was in progress when its journal was written.</summary>
public sealed record JournalEntry
{
    public required string Id { get; init; }
    public required string PartPath { get; init; }
    public required string FinalPath { get; init; }
    public required DateTime StartedUtc { get; init; }
    public required int ProcessId { get; init; }

    /// <summary>Process start time, so a recycled PID is not mistaken for a live recording.</summary>
    public required DateTime ProcessStartedUtc { get; init; }
}

/// <summary>
/// Sidecar records of recordings that have not been finalized yet.
/// </summary>
/// <remarks>
/// A journal file is written the moment a recording starts and deleted once the MP4 is safely in
/// place. Anything left behind therefore means the app died mid-recording, which is exactly what
/// <see cref="CrashRecoveryService"/> looks for on the next launch. The owning process id is
/// recorded alongside its start time because Windows reuses process ids; without the start time a
/// recycled id could make a genuinely orphaned recording look like it is still running.
/// </remarks>
public static class RecordingJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string Write(string partPath, string finalPath)
    {
        var id = Guid.NewGuid().ToString("N");

        try
        {
            Directory.CreateDirectory(AppPaths.PendingDirectory);

            var process = System.Diagnostics.Process.GetCurrentProcess();
            var entry = new JournalEntry
            {
                Id = id,
                PartPath = partPath,
                FinalPath = finalPath,
                StartedUtc = DateTime.UtcNow,
                ProcessId = Environment.ProcessId,
                ProcessStartedUtc = process.StartTime.ToUniversalTime(),
            };

            File.WriteAllText(PathFor(id), JsonSerializer.Serialize(entry, JsonOptions));
        }
        catch (Exception ex)
        {
            // Losing the journal only costs crash recovery, not the recording itself.
            Log.Warn(ex, "Could not write the recording journal; crash recovery will not cover this recording.");
        }

        return id;
    }

    public static void Remove(string id)
    {
        try
        {
            var path = PathFor(id);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, $"Could not delete the journal entry '{id}'.");
        }
    }

    public static IReadOnlyList<JournalEntry> ReadAll()
    {
        var entries = new List<JournalEntry>();

        try
        {
            if (!Directory.Exists(AppPaths.PendingDirectory)) return entries;

            foreach (var file in Directory.EnumerateFiles(AppPaths.PendingDirectory, "*.json"))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<JournalEntry>(File.ReadAllText(file));
                    if (entry is not null) entries.Add(entry);
                    else TryDelete(file);
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, $"Unreadable journal entry '{file}'; removing it.");
                    TryDelete(file);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Could not enumerate pending recordings.");
        }

        return entries;
    }

    /// <summary>True when the process that created the entry is still alive.</summary>
    public static bool IsOwnerAlive(JournalEntry entry)
    {
        if (entry.ProcessId == Environment.ProcessId) return true;

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(entry.ProcessId);

            // A matching id with a different start time is a recycled id, not our recorder.
            var started = process.StartTime.ToUniversalTime();
            return Math.Abs((started - entry.ProcessStartedUtc).TotalSeconds) < 2;
        }
        catch (ArgumentException)
        {
            return false;   // no such process
        }
        catch (Exception ex)
        {
            // Access denied means something is running under that id; assume it is alive so a
            // live recording is never stolen out from under another instance.
            Log.Warn(ex, $"Could not inspect process {entry.ProcessId}; assuming it is still running.");
            return true;
        }
    }

    private static string PathFor(string id) => Path.Combine(AppPaths.PendingDirectory, id + ".json");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
