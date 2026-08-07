using System.IO;
using Recorder.Encoding;
using Recorder.Utils;

namespace Recorder.Recovery;

/// <summary>
/// Salvages recordings that were interrupted by a crash, a power loss or a forced shutdown.
/// </summary>
/// <remarks>
/// This works only because capture writes a <em>fragmented</em> MP4: the file is self-describing as
/// it grows, so even a truncated one contains complete, playable video up to the moment the process
/// died. Recovery is therefore the same stream-copy remux a normal stop performs, just run against
/// a file nobody closed.
/// </remarks>
public sealed class CrashRecoveryService
{
    private readonly Mp4Finalizer _finalizer;

    public CrashRecoveryService(Mp4Finalizer finalizer) => _finalizer = finalizer;

    public sealed record RecoveredRecording(string Path, TimeSpan Age);

    /// <summary>
    /// Finalizes every abandoned recording found on disk.
    /// </summary>
    /// <returns>The recordings that were successfully recovered.</returns>
    public async Task<IReadOnlyList<RecoveredRecording>> RecoverAsync(CancellationToken cancellationToken = default)
    {
        var recovered = new List<RecoveredRecording>();

        foreach (var entry in RecordingJournal.ReadAll())
        {
            if (cancellationToken.IsCancellationRequested) break;

            // Another instance may legitimately be recording right now.
            if (RecordingJournal.IsOwnerAlive(entry)) continue;

            try
            {
                if (!File.Exists(entry.PartPath))
                {
                    // Nothing to salvage — most likely a clean stop whose journal deletion failed.
                    RecordingJournal.Remove(entry.Id);
                    continue;
                }

                var finalPath = ResolveFreePath(entry.FinalPath);
                var result = await _finalizer.FinalizeAsync(entry.PartPath, finalPath, cancellationToken)
                    .ConfigureAwait(false);

                if (result.Success)
                {
                    recovered.Add(new RecoveredRecording(result.Path, DateTime.UtcNow - entry.StartedUtc));
                    Log.Warn($"Recovered an interrupted recording as '{result.Path}'.");
                }
                else
                {
                    Log.Error($"Could not recover '{entry.PartPath}': {result.Warning}");
                }

                // Either way the entry is dealt with; leaving it would nag on every launch.
                RecordingJournal.Remove(entry.Id);
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Recovery of '{entry.PartPath}' threw.");
                RecordingJournal.Remove(entry.Id);
            }
        }

        return recovered;
    }

    /// <summary>Avoids overwriting a file the user has since created at the intended name.</summary>
    private static string ResolveFreePath(string preferred)
    {
        if (!File.Exists(preferred)) return preferred;

        var dir = Path.GetDirectoryName(preferred) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(preferred);
        var ext = Path.GetExtension(preferred);

        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem}_recovered{(i == 1 ? string.Empty : i.ToString())}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }

        return Path.Combine(dir, $"{stem}_recovered_{Guid.NewGuid():N}{ext}");
    }
}
