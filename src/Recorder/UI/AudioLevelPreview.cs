using NAudio.CoreAudioApi;
using NAudio.Wave;
using Recorder.Capture;
using Recorder.Utils;

namespace Recorder.UI;

/// <summary>
/// Opens a device just long enough to show a level meter in the settings window.
/// </summary>
/// <remarks>
/// <para>Answers the one question the device dropdown cannot: is the thing you just picked actually
/// receiving sound? Getting that wrong is otherwise invisible until playback, by which point the
/// recording is over.</para>
///
/// <para>Deliberately short-lived — it holds a capture open only while the Audio tab is on screen,
/// and is disposed the moment the selection changes or the window closes. It also refuses to open
/// anything while a recording is running: settings are already blocked in that state, and competing
/// for a device that is mid-recording is not a risk worth taking for a progress bar.</para>
/// </remarks>
public sealed class AudioLevelPreview : IDisposable
{
    /// <summary>Decay applied on each read, so the bar falls smoothly instead of flickering.</summary>
    private const float Decay = 0.82f;

    private readonly object _gate = new();
    private IWaveIn? _capture;

    /// <summary>
    /// Captured at Start rather than read from the event sender.
    /// </summary>
    /// <remarks>
    /// Reading it back from <c>sender</c> would leave the meter silently dead if any implementation
    /// ever raised the event with something else — and a level meter that never moves is exactly the
    /// symptom it exists to distinguish from a genuinely dead microphone.
    /// </remarks>
    private WaveFormat? _format;

    private float _peak;
    private bool _disposed;

    /// <summary>Non-null when the device could not be opened, for the UI to show instead of a bar.</summary>
    public string? Error { get; private set; }

    /// <summary>Whether a capture is currently open.</summary>
    public bool IsRunning => _capture is not null;

    /// <summary>
    /// Starts previewing a device, replacing whatever was running.
    /// </summary>
    /// <param name="deviceId">Endpoint id, or null for the Windows default.</param>
    public void Start(string? deviceId, DataFlow flow)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Stop();

        try
        {
            var device = AudioDeviceEnumerator.GetDevice(deviceId, flow);
            IWaveIn capture = flow == DataFlow.Render
                ? new WasapiLoopbackCapture(device)
                : new WasapiCapture(device);

            capture.DataAvailable += OnDataAvailable;
            capture.StartRecording();

            lock (_gate)
            {
                _capture = capture;
                _format = capture.WaveFormat;
                _peak = 0f;
                Error = null;
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Could not open an audio device for preview.");
            Error = "This device could not be opened.";
        }
    }

    public void Stop()
    {
        IWaveIn? capture;
        lock (_gate)
        {
            capture = _capture;
            _capture = null;
            _format = null;
            _peak = 0f;
        }

        if (capture is null) return;

        try { capture.DataAvailable -= OnDataAvailable; } catch { }
        try { capture.StopRecording(); } catch (Exception ex) { Log.Warn(ex, "Stopping the preview failed."); }
        try { capture.Dispose(); } catch { }
    }

    /// <summary>
    /// The current level, 0 to 1, ready to drive a bar.
    /// </summary>
    /// <remarks>
    /// Reading is destructive in the sense that it applies the decay, so this is meant to be polled
    /// on a timer at a steady rate rather than called ad hoc.
    /// </remarks>
    public float ReadLevel()
    {
        lock (_gate)
        {
            var value = _peak;
            _peak *= Decay;
            return value;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;

        try
        {
            WaveFormat? format;
            lock (_gate) format = _format;
            if (format is null) return;

            var peak = format.Encoding == WaveFormatEncoding.IeeeFloat
                ? PeakFloat(e.Buffer, e.BytesRecorded)
                : PeakPcm16(e.Buffer, e.BytesRecorded);

            lock (_gate)
            {
                // Rise instantly, fall on the decay in ReadLevel: a meter that lags the sound it is
                // reporting is worse than useless for checking a device.
                if (peak > _peak) _peak = Math.Min(peak, 1f);
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Reading a preview packet failed.");
        }
    }

    private static float PeakFloat(byte[] buffer, int count)
    {
        var peak = 0f;
        for (var i = 0; i + 3 < count; i += 4)
        {
            var value = Math.Abs(BitConverter.ToSingle(buffer, i));
            if (value > peak) peak = value;
        }
        return peak;
    }

    private static float PeakPcm16(byte[] buffer, int count)
    {
        var peak = 0f;
        for (var i = 0; i + 1 < count; i += 2)
        {
            var sample = Math.Abs(BitConverter.ToInt16(buffer, i) / (float)short.MaxValue);
            if (sample > peak) peak = sample;
        }
        return peak;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
