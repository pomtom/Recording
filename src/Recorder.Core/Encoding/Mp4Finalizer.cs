using System.Diagnostics;
using System.IO;
using Recorder.Utils;

namespace Recorder.Encoding;

/// <summary>
/// Turns the fragmented capture file into the MP4 the user keeps.
/// </summary>
/// <remarks>
/// The remux is a stream copy — no re-encoding — so it costs about a second even for a long
/// recording. If it fails for any reason the <c>.part</c> file is simply renamed into place:
/// a fragmented MP4 plays fine in every mainstream player, so a failed remux must never be
/// allowed to cost the user their recording.
/// </remarks>
public sealed class Mp4Finalizer
{
    private static readonly TimeSpan RemuxTimeout = TimeSpan.FromMinutes(10);

    private readonly FFmpegProvisioner _provisioner;

    public Mp4Finalizer(FFmpegProvisioner provisioner) => _provisioner = provisioner;

    public sealed record Result(bool Success, string Path, bool Remuxed, string? Warning);

    /// <summary>Remuxes <paramref name="partPath"/> to <paramref name="finalPath"/>.</summary>
    public async Task<Result> FinalizeAsync(string partPath, string finalPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(partPath))
            return new Result(false, finalPath, false, "The capture file is missing.");

        if (new FileInfo(partPath).Length == 0)
        {
            TryDelete(partPath);
            return new Result(false, finalPath, false, "The capture file is empty — nothing was recorded.");
        }

        try
        {
            var ffmpeg = _provisioner.GetFFmpegPath();
            var args = FFmpegArgumentBuilder.BuildRemuxArguments(partPath, finalPath);

            var psi = new ProcessStartInfo(ffmpeg)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = false,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null for the remux.");

            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(RemuxTimeout);

            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException("The remux did not finish in time.");
            }

            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode == 0 && File.Exists(finalPath) && new FileInfo(finalPath).Length > 0)
            {
                TryDelete(partPath);
                return new Result(true, finalPath, true, null);
            }

            Log.Error($"Remux failed (exit {process.ExitCode}): {stderr}");
            TryDelete(finalPath);   // a partial remux output is worse than none
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Remux threw; keeping the fragmented capture instead.");
            TryDelete(finalPath);
        }

        // Salvage: the fragmented file is already playable, so hand it over under the final name.
        return Salvage(partPath, finalPath);
    }

    private static Result Salvage(string partPath, string finalPath)
    {
        try
        {
            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(partPath, finalPath);
            return new Result(true, finalPath, false,
                "The recording was saved but could not be optimised for streaming.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not rename the capture file to its final name.");
            return new Result(true, partPath, false,
                $"The recording was saved as {Path.GetFileName(partPath)}.");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Log.Warn(ex, $"Could not delete '{path}'."); }
    }
}
