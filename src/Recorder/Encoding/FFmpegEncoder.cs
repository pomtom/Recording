using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using Recorder.Utils;

namespace Recorder.Encoding;

/// <summary>
/// Owns the ffmpeg child process and the two named pipes feeding it.
/// </summary>
/// <remarks>
/// <para><b>The inputs connect in sequence, not in parallel.</b> ffmpeg opens input files one at a
/// time, and for each one it runs <c>find_stream_info</c>, which blocks reading actual data before
/// it moves on to the next input. So it connects the video pipe, then waits for video frames, and
/// only opens the audio pipe once it has them. Waiting for both connections before sending any
/// video therefore deadlocks: ffmpeg wants data before opening audio, and the app wants audio open
/// before sending data. <see cref="StartAsync"/> returns as soon as <em>video</em> is connected so
/// the caller can start the pacer; <see cref="WaitForAudioAsync"/> completes later, once the frames
/// flowing have let ffmpeg reach its second input.</para>
///
/// <para>Two other details are load-bearing: both pipe servers exist before ffmpeg starts (or it
/// races the app and fails to open a pipe that is not there yet), and stderr is drained
/// continuously (ffmpeg writes warnings there, and a full OS pipe buffer blocks the child forever
/// mid-recording).</para>
/// </remarks>
public sealed class FFmpegEncoder : IAsyncDisposable
{
    /// <summary>How long ffmpeg gets to open the video pipe.</summary>
    private static readonly TimeSpan VideoConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long ffmpeg gets to reach its audio input once frames are flowing. Generous, because it
    /// has to buffer a full frame through find_stream_info first.
    /// </summary>
    private static readonly TimeSpan AudioConnectTimeout = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);

    private readonly string _ffmpegPath;

    private Process? _process;
    private NamedPipeServerStream? _videoPipe;
    private NamedPipeServerStream? _audioPipe;
    private Task? _stderrPump;
    private readonly StringBuilder _stderrTail = new();
    private readonly object _stderrGate = new();

    private volatile bool _videoBroken;
    private volatile bool _audioBroken;
    private volatile bool _completing;

    private FFmpegEncoder(string ffmpegPath) => _ffmpegPath = ffmpegPath;

    public VideoEncoder ActiveEncoder { get; private set; }

    public EncodeSpec Spec { get; private set; } = null!;

    /// <summary>True once either pipe has broken — ffmpeg has gone away and the recording is doomed.</summary>
    public bool IsBroken => _videoBroken || _audioBroken;

    /// <summary>The last few stderr lines, for error messages.</summary>
    public string StderrTail
    {
        get { lock (_stderrGate) return _stderrTail.ToString(); }
    }

    /// <summary>
    /// Starts ffmpeg and waits for it to open the video pipe.
    /// </summary>
    /// <remarks>
    /// The audio pipe is <em>not</em> connected yet when this returns — see the class remarks.
    /// The caller must start writing frames, then await <see cref="WaitForAudioAsync"/>.
    /// </remarks>
    public static async Task<FFmpegEncoder> StartAsync(
        string ffmpegPath,
        EncodeSpec spec,
        VideoEncoder encoder,
        CancellationToken cancellationToken)
    {
        // Unique pipe names per recording: a previous ffmpeg may still hold the old ones briefly.
        var attemptSpec = spec with
        {
            VideoPipeName = $"pomrec-v-{Guid.NewGuid():N}",
            AudioPipeName = $"pomrec-a-{Guid.NewGuid():N}",
        };

        var instance = new FFmpegEncoder(ffmpegPath);
        try
        {
            await instance.StartOnceAsync(attemptSpec, encoder, cancellationToken).ConfigureAwait(false);
            return instance;
        }
        catch
        {
            await instance.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private Task? _audioConnectTask;

    /// <summary>
    /// Completes once ffmpeg has opened the audio pipe, which only happens after video flows.
    /// </summary>
    public async Task WaitForAudioAsync(CancellationToken cancellationToken)
    {
        var task = _audioConnectTask;
        if (task is null) return;

        try
        {
            await task.WaitAsync(AudioConnectTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(
                $"ffmpeg did not open its audio input within {AudioConnectTimeout.TotalSeconds:0}s. {StderrTail}");
        }
    }

    private async Task StartOnceAsync(EncodeSpec spec, VideoEncoder encoder, CancellationToken cancellationToken)
    {
        Spec = spec;
        ActiveEncoder = encoder;

        // Create both pipe servers first so ffmpeg finds them the moment it looks.
        // The buffers are sized to roughly a third of a second of 1080p60 NV12 so a brief
        // scheduling hiccup in ffmpeg does not stall the pacer thread.
        _videoPipe = new NamedPipeServerStream(
            spec.VideoPipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, inBufferSize: 0, outBufferSize: 8 * 1024 * 1024);

        _audioPipe = new NamedPipeServerStream(
            spec.AudioPipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, inBufferSize: 0, outBufferSize: 1 * 1024 * 1024);

        var args = FFmpegArgumentBuilder.BuildRecordArguments(spec, encoder);
        var psi = new ProcessStartInfo(_ffmpegPath)
        {
            RedirectStandardError = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(spec.PartPath) ?? Environment.CurrentDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null for ffmpeg.");

        _stderrPump = PumpStderrAsync(_process);

        // Start listening on both pipes now. Only the video connection is awaited here; ffmpeg
        // cannot reach the audio input until frames are flowing, which the caller arranges.
        _audioConnectTask = _audioPipe.WaitForConnectionAsync(CancellationToken.None);

        // Nothing observes a failure on this task until WaitForAudioAsync, so make sure an early
        // fault (ffmpeg died before opening audio) is never an unobserved exception.
        _ = _audioConnectTask.ContinueWith(
            t => Log.Warn(t.Exception!, "Waiting for the audio pipe faulted."),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(VideoConnectTimeout);

        try
        {
            await _videoPipe.WaitForConnectionAsync(connectCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"ffmpeg did not open its video input within {VideoConnectTimeout.TotalSeconds:0}s. {StderrTail}");
        }

        if (_process.HasExited)
        {
            throw new InvalidOperationException(
                $"ffmpeg exited immediately (code {SafeExitCode(_process)}) using " +
                $"{EncoderProbe.EncoderName(encoder)}. {StderrTail}");
        }
    }

    /// <summary>Writes one raw frame. Returns false once ffmpeg has gone away.</summary>
    public bool WriteVideoFrame(ReadOnlySpan<byte> frame)
    {
        var pipe = _videoPipe;
        if (pipe is null || _videoBroken || _completing) return false;

        try
        {
            pipe.Write(frame);
            return true;
        }
        catch (Exception ex)
        {
            _videoBroken = true;
            if (!_completing) Log.Error(ex, "Video pipe write failed; ffmpeg has stopped reading.");
            return false;
        }
    }

    /// <summary>Writes interleaved 16-bit PCM. Returns false once ffmpeg has gone away.</summary>
    public bool WriteAudio(ReadOnlySpan<byte> pcm)
    {
        var pipe = _audioPipe;
        if (pipe is null || _audioBroken || _completing) return false;

        try
        {
            pipe.Write(pcm);
            return true;
        }
        catch (Exception ex)
        {
            _audioBroken = true;
            if (!_completing) Log.Error(ex, "Audio pipe write failed; ffmpeg has stopped reading.");
            return false;
        }
    }

    /// <summary>
    /// Closes the inputs and waits for ffmpeg to flush and finalize the fragmented MP4.
    /// </summary>
    /// <returns>True if ffmpeg exited cleanly.</returns>
    public async Task<bool> CompleteAsync()
    {
        _completing = true;

        // Closing both pipes is what tells ffmpeg "end of stream". It then drains its queues,
        // writes the trailing fragment and exits on its own — no signal needed.
        ClosePipes();

        var process = _process;
        if (process is null) return false;

        try
        {
            var exited = await WaitForExitAsync(process, ExitTimeout, CancellationToken.None).ConfigureAwait(false);
            if (!exited)
            {
                Log.Error($"ffmpeg did not exit within {ExitTimeout.TotalSeconds:0}s; terminating. {StderrTail}");
                TryKill(process);
                return false;
            }

            if (_stderrPump is not null)
            {
                try { await _stderrPump.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
            }

            var code = SafeExitCode(process);
            if (code != 0) Log.Error($"ffmpeg exited with code {code}. {StderrTail}");
            return code == 0;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Waiting for ffmpeg to exit failed.");
            return false;
        }
    }

    private async Task PumpStderrAsync(Process process)
    {
        try
        {
            var reader = process.StandardError;
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0) continue;

                lock (_stderrGate)
                {
                    _stderrTail.AppendLine(line);
                    // Keep only a recent window; a long recording can emit a lot of warnings.
                    if (_stderrTail.Length > 4000) _stderrTail.Remove(0, _stderrTail.Length - 3000);
                }

                // At -loglevel warning everything ffmpeg emits is worth recording.
                Log.Warn("[ffmpeg] " + line);
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "ffmpeg stderr pump stopped.");
        }
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken token)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(timeout);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return false;   // still running, which is the good outcome during startup
        }
    }

    private void ClosePipes()
    {
        try { _videoPipe?.Dispose(); } catch (Exception ex) { Log.Warn(ex, "Closing the video pipe failed."); }
        try { _audioPipe?.Dispose(); } catch (Exception ex) { Log.Warn(ex, "Closing the audio pipe failed."); }
        _videoPipe = null;
        _audioPipe = null;
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; } catch { return -1; }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        _completing = true;
        ClosePipes();

        var process = _process;
        _process = null;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    // Inputs are already closed, so a healthy ffmpeg exits on its own shortly.
                    if (!await WaitForExitAsync(process, TimeSpan.FromSeconds(5), CancellationToken.None)
                            .ConfigureAwait(false))
                    {
                        TryKill(process);
                    }
                }
            }
            catch { }
            finally
            {
                try { process.Dispose(); } catch { }
            }
        }

        if (_stderrPump is not null)
        {
            try { await _stderrPump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
            _stderrPump = null;
        }
    }
}
