using System.Diagnostics;
using System.IO;
using Recorder.Utils;

namespace Recorder.Encoding;

/// <summary>An H.264 encoder ffmpeg can be asked to use, best first.</summary>
public enum VideoEncoder
{
    Nvenc,
    QuickSync,
    Amf,
    X264,
}

/// <summary>
/// Works out which H.264 encoder this machine should actually use.
/// </summary>
/// <remarks>
/// <c>ffmpeg -encoders</c> lists what the binary was <em>built</em> with, which says nothing about
/// whether this machine can use it: a laptop ships an NVENC-capable ffmpeg and still fails at
/// runtime because the GPU is disabled, the driver is too old, or another process holds the
/// encoder session. So each listed candidate is proved with a real fifth-of-a-second encode before
/// it is trusted.
/// <para>
/// Proving up front matters more than it looks. A hardware encoder does not fail until well after
/// ffmpeg has opened its inputs, so discovering the problem during a live recording would mean
/// losing the take. Better to spend ~200 ms once at startup and cache the answer.
/// </para>
/// </remarks>
public sealed class EncoderProbe
{
    private static readonly TimeSpan ValidationTimeout = TimeSpan.FromSeconds(20);

    private readonly FFmpegProvisioner _provisioner;
    private readonly object _gate = new();
    private VideoEncoder? _cached;

    public EncoderProbe(FFmpegProvisioner provisioner) => _provisioner = provisioner;

    public static string EncoderName(VideoEncoder encoder) => encoder switch
    {
        VideoEncoder.Nvenc => "h264_nvenc",
        VideoEncoder.QuickSync => "h264_qsv",
        VideoEncoder.Amf => "h264_amf",
        _ => "libx264",
    };

    public static string FriendlyName(VideoEncoder encoder) => encoder switch
    {
        VideoEncoder.Nvenc => "NVIDIA NVENC",
        VideoEncoder.QuickSync => "Intel Quick Sync",
        VideoEncoder.Amf => "AMD AMF",
        _ => "Software (x264)",
    };

    /// <summary>
    /// The best encoder this machine can actually use. Falls back to libx264, which always works.
    /// </summary>
    public VideoEncoder GetPreferredEncoder()
    {
        lock (_gate)
        {
            if (_cached is not null) return _cached.Value;

            var chosen = LoadCachedChoice() ?? DetermineAndCache();
            _cached = chosen;
            return chosen;
        }
    }

    private VideoEncoder DetermineAndCache()
    {
        var built = ListBuiltInEncoders();
        var chosen = VideoEncoder.X264;

        foreach (var candidate in new[] { VideoEncoder.Nvenc, VideoEncoder.QuickSync, VideoEncoder.Amf })
        {
            if (!built.Contains(EncoderName(candidate))) continue;
            if (!Validate(candidate)) continue;

            chosen = candidate;
            break;
        }

        try
        {
            Directory.CreateDirectory(AppPaths.BinDirectory);
            File.WriteAllLines(CacheFile, [FingerprintOf(_provisioner.GetFFmpegPath()), chosen.ToString()]);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Could not write the encoder cache.");
        }

        Log.Warn($"Video encoder selected: {EncoderName(chosen)} ({FriendlyName(chosen)}).");
        return chosen;
    }

    /// <summary>
    /// Proves an encoder works by encoding a fraction of a second of generated video.
    /// </summary>
    /// <remarks>
    /// The output goes to the null muxer, so nothing is written to disk. A non-zero exit code means
    /// the encoder is present but unusable on this machine.
    /// </remarks>
    private bool Validate(VideoEncoder encoder)
    {
        try
        {
            var psi = new ProcessStartInfo(_provisioner.GetFFmpegPath())
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var arg in new[]
            {
                "-hide_banner", "-nostdin", "-loglevel", "error",
                "-f", "lavfi", "-i", "color=c=black:s=256x144:r=30:d=0.2",
                "-c:v", EncoderName(encoder),
                "-pix_fmt", "yuv420p",
                "-f", "null", "-",
            })
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi);
            if (process is null) return false;

            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)ValidationTimeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                Log.Warn($"{EncoderName(encoder)} validation timed out; skipping it.");
                return false;
            }

            if (process.ExitCode == 0) return true;

            Log.Warn($"{EncoderName(encoder)} is present but unusable: {stderr.GetAwaiter().GetResult().Trim()}");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, $"{EncoderName(encoder)} validation failed.");
            return false;
        }
    }

    private string CacheFile => Path.Combine(AppPaths.BinDirectory, "encoder.cache");

    private VideoEncoder? LoadCachedChoice()
    {
        try
        {
            if (!File.Exists(CacheFile)) return null;

            // Invalidate whenever the ffmpeg binary changes.
            var lines = File.ReadAllLines(CacheFile);
            if (lines.Length < 2) return null;
            if (!string.Equals(lines[0], FingerprintOf(_provisioner.GetFFmpegPath()), StringComparison.OrdinalIgnoreCase))
                return null;

            return Enum.TryParse<VideoEncoder>(lines[1], out var encoder) ? encoder : null;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Could not read the encoder cache; re-probing.");
            return null;
        }
    }

    private HashSet<string> ListBuiltInEncoders()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string ffmpeg;
        try
        {
            ffmpeg = _provisioner.GetFFmpegPath();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Encoder probe skipped: ffmpeg unavailable.");
            return found;
        }

        try
        {
            var psi = new ProcessStartInfo(ffmpeg)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-encoders");

            using var process = Process.Start(psi);
            if (process is null) return found;

            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(15_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                Log.Warn("ffmpeg -encoders timed out; assuming software encoding only.");
                return found;
            }

            foreach (var name in new[] { "h264_nvenc", "h264_qsv", "h264_amf", "libx264" })
            {
                if (output.Contains(name, StringComparison.OrdinalIgnoreCase)) found.Add(name);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Encoder probe failed; assuming software encoding only.");
        }

        return found;
    }

    private static string FingerprintOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return path;
        }
    }
}
