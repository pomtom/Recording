using NAudio.Dsp;

namespace Recorder.Capture.Dsp;

/// <summary>
/// Removes steady background noise by attenuating each frequency bin toward its own noise floor.
/// </summary>
/// <remarks>
/// <para>The signal is transformed in short overlapping frames. Per bin, an estimate of the noise
/// magnitude is subtracted from the observed magnitude; what remains becomes a gain between a floor
/// and 1.0, which is applied to the bin and the frame transformed back. Steady sources — fans, hiss,
/// hum, room tone — sit at a stable level in every frame and are removed; speech moves constantly
/// and survives.</para>
///
/// <para>The noise estimate is the <em>minimum</em> of each bin's smoothed power over the last one
/// to two seconds, not an average. That choice is what makes the algorithm safe here. An average
/// has to assume the recording opens with silence in order to learn anything trustworthy, and this
/// recorder cannot promise that: the chain sees its first sample the moment the timeline starts, and
/// the user may already be talking. A minimum needs no such assumption — no bin stays loud for two
/// solid seconds during speech, so the floor is found correctly whatever is happening when recording
/// begins. Because a minimum systematically understates the true floor, it is corrected by a fixed
/// bias factor.</para>
///
/// <para>Two further details make it sound acceptable rather than robotic. Suppression fades in over
/// the first second, so even a badly-conditioned start cannot damage the opening words. And the
/// computed gains are smoothed across both time and neighbouring bins, which suppresses "musical
/// noise" — the bubbling artefact of isolated bins flicking between full gain and the floor.</para>
///
/// <para>Frames overlap by half with a square-root Hann window applied on both analysis and
/// synthesis. Squared, that is a plain Hann window, which sums to exactly 1.0 at 50% overlap, so
/// unmodified audio passes through the whole thing unchanged. Latency is one frame and never grows,
/// which is what keeps this safe to put in the recording path.</para>
/// </remarks>
public sealed class SpectralSubtractor : IAudioProcessor
{
    /// <summary>log2 of the frame size. 9 → 512 samples, ~11 ms at 48 kHz.</summary>
    private const int FftOrder = 9;
    private const int FftSize = 1 << FftOrder;
    private const int Hop = FftSize / 2;
    private const int Bins = (FftSize / 2) + 1;

    /// <summary>Floor on the magnitude used as a divisor, so a silent bin cannot divide by zero.</summary>
    private const float Epsilon = 1e-10f;

    /// <summary>
    /// Compensation for the minimum's downward bias.
    /// </summary>
    /// <remarks>
    /// The minimum of a fluctuating power over a window sits well below its mean, so the raw
    /// estimate understates the true noise floor by a roughly constant factor. Correcting it keeps
    /// the user-facing strength meaning what it appears to mean; without it, "Standard" removes
    /// noticeably less than the name suggests.
    /// </remarks>
    private const float BiasCompensation = 1.9f;

    private readonly float[] _window = new float[FftSize];
    private readonly float[] _frame = new float[FftSize];
    private readonly float[] _overlap = new float[FftSize];
    private readonly Complex[] _spectrum = new Complex[FftSize];
    private readonly float[] _gain = new float[Bins];
    private readonly float[] _smoothed = new float[Bins];

    /// <summary>Per-bin smoothed power, the quantity the sliding minimum is taken over.</summary>
    private readonly float[] _power = new float[Bins];

    /// <summary>Minimum over the sub-window currently being filled.</summary>
    private readonly float[] _currentMin = new float[Bins];

    /// <summary>Minimum over the sub-window before it.</summary>
    private readonly float[] _previousMin = new float[Bins];

    /// <summary>The noise power estimate in use: the smaller of the two sub-window minima.</summary>
    private readonly float[] _noisePower = new float[Bins];

    private readonly float[] _inHop = new float[Hop];
    private readonly float[] _outFifo = new float[FftSize * 2];
    private int _inHopCount;
    private int _outRead;
    private int _outWrite;
    private int _outCount;
    private int _framesSeen;

    private readonly float _strength;
    private readonly float _gainFloor;
    private readonly bool _adaptive;
    private readonly float _powerSmoothing;
    private readonly float _gainSmoothing;

    /// <summary>Frames per sub-window; the minimum therefore spans one to two of these.</summary>
    private readonly int _subWindowFrames;

    private int _subWindowPosition;
    private int _subWindowsSeen;

    public SpectralSubtractor(int sampleRate, double strength, double floorDb, bool adaptive)
    {
        _strength = (float)Math.Max(0, strength);
        _gainFloor = (float)Math.Pow(10, Math.Min(0, floorDb) / 20.0);
        _adaptive = adaptive;

        // Square-root Hann. Applied twice (analysis and synthesis) it becomes a Hann window, which
        // is what makes the 50% overlap-add reconstruct exactly.
        for (var i = 0; i < FftSize; i++)
            _window[i] = MathF.Sqrt(0.5f * (1f - MathF.Cos(2f * MathF.PI * i / FftSize)));

        var hopSeconds = Hop / (double)sampleRate;
        _powerSmoothing = Coefficient(hopSeconds, 0.10);
        _gainSmoothing = Coefficient(hopSeconds, 0.03);

        // 1.5 s per sub-window, so the minimum spans 1.5–3 s. The lower bound is what protects
        // speech: pauses between phrases come far more often than that, and anything that really
        // does hold a steady level for three seconds is hum, not talking. Shortening this makes the
        // estimator quicker to follow a changing room and quicker to mistake a held vowel for noise.
        _subWindowFrames = Math.Max(1, (int)Math.Round(1.5 / hopSeconds));

        Array.Fill(_gain, 1f);
        Array.Fill(_smoothed, 1f);
        Array.Fill(_currentMin, float.MaxValue);
        Array.Fill(_previousMin, float.MaxValue);
    }

    /// <summary>Exponential smoothing coefficient for a given time constant.</summary>
    private static float Coefficient(double stepSeconds, double tauSeconds) =>
        (float)Math.Exp(-stepSeconds / tauSeconds);

    public void Process(float[] buffer, int offset, int count)
    {
        for (var i = 0; i < count; i++)
        {
            _inHop[_inHopCount++] = buffer[offset + i];

            if (_inHopCount == Hop)
            {
                _inHopCount = 0;
                AnalyzeHop();
            }

            // Before the first frame completes there is nothing to emit yet, so the opening samples
            // are silence. That is the algorithm's fixed latency, not a dropout.
            buffer[offset + i] = ReadOutput();
        }
    }

    private void AnalyzeHop()
    {
        // Slide the analysis window along and drop the newest hop into the tail.
        Array.Copy(_frame, Hop, _frame, 0, FftSize - Hop);
        Array.Copy(_inHop, 0, _frame, FftSize - Hop, Hop);

        for (var i = 0; i < FftSize; i++)
        {
            _spectrum[i].X = _frame[i] * _window[i];
            _spectrum[i].Y = 0f;
        }

        FastFourierTransform.FFT(true, FftOrder, _spectrum);

        UpdateNoiseAndGains();
        ApplyGains();

        FastFourierTransform.FFT(false, FftOrder, _spectrum);

        for (var i = 0; i < FftSize; i++)
            _overlap[i] += _spectrum[i].X * _window[i];

        for (var i = 0; i < Hop; i++) WriteOutput(_overlap[i]);

        Array.Copy(_overlap, Hop, _overlap, 0, FftSize - Hop);
        Array.Clear(_overlap, FftSize - Hop, Hop);
    }

    private void UpdateNoiseAndGains()
    {
        var first = _framesSeen == 0;
        if (_framesSeen < int.MaxValue) _framesSeen++;

        // Suppression fades in across the first sub-window. Until a full minimum has been observed
        // the estimate cannot be trusted, and a second of untouched noise at the start of a
        // recording is a far better outcome than a second of mangled speech.
        var confidence = MathF.Min(1f, _framesSeen / (float)_subWindowFrames);
        var effectiveStrength = _strength * BiasCompensation * confidence;

        // The minimum is frozen once it has settled if the user asked for a fixed floor.
        var tracking = _adaptive || _subWindowsSeen < 2;

        for (var bin = 0; bin < Bins; bin++)
        {
            var re = _spectrum[bin].X;
            var im = _spectrum[bin].Y;
            var power = (re * re) + (im * im);
            var magnitude = MathF.Sqrt(power);

            _power[bin] = first
                ? power
                : (_powerSmoothing * _power[bin]) + ((1f - _powerSmoothing) * power);

            if (tracking)
            {
                if (_power[bin] < _currentMin[bin]) _currentMin[bin] = _power[bin];

                var candidate = MathF.Min(_currentMin[bin], _previousMin[bin]);
                if (candidate < float.MaxValue) _noisePower[bin] = candidate;
            }

            var noise = MathF.Sqrt(_noisePower[bin]);
            var clean = magnitude - (effectiveStrength * noise);
            var target = clean / MathF.Max(magnitude, Epsilon);
            _gain[bin] = Math.Clamp(target, _gainFloor, 1f);
        }

        AdvanceSubWindow(tracking);

        // Smooth across neighbouring bins, then across time. Both attack the same artefact: a bin
        // that swings between full gain and the floor from frame to frame is what "musical noise"
        // actually is.
        for (var bin = 0; bin < Bins; bin++)
        {
            var low = _gain[Math.Max(bin - 1, 0)];
            var mid = _gain[bin];
            var high = _gain[Math.Min(bin + 1, Bins - 1)];
            var neighbourhood = (low + mid + mid + high) * 0.25f;

            _smoothed[bin] = (_gainSmoothing * _smoothed[bin]) + ((1f - _gainSmoothing) * neighbourhood);
        }
    }

    /// <summary>
    /// Rolls the sub-window on, retiring the older minimum.
    /// </summary>
    /// <remarks>
    /// Two buffers rather than a full ring of per-frame values: the minimum then spans somewhere
    /// between one and two sub-windows depending on where in the cycle it is read, which is the
    /// standard cheap approximation and is entirely adequate for tracking a room's noise floor.
    /// </remarks>
    private void AdvanceSubWindow(bool tracking)
    {
        if (!tracking) return;
        if (++_subWindowPosition < _subWindowFrames) return;

        _subWindowPosition = 0;
        if (_subWindowsSeen < int.MaxValue) _subWindowsSeen++;

        Array.Copy(_currentMin, _previousMin, Bins);
        Array.Fill(_currentMin, float.MaxValue);
    }

    private void ApplyGains()
    {
        for (var bin = 0; bin < Bins; bin++)
        {
            var gain = _smoothed[bin];

            _spectrum[bin].X *= gain;
            _spectrum[bin].Y *= gain;

            // The spectrum of a real signal is conjugate-symmetric. DC and Nyquist have no partner;
            // every other bin must be scaled identically or the inverse transform stops being real.
            var mirror = FftSize - bin;
            if (mirror > bin && mirror < FftSize)
            {
                _spectrum[mirror].X *= gain;
                _spectrum[mirror].Y *= gain;
            }
        }
    }

    private void WriteOutput(float value)
    {
        _outFifo[_outWrite] = value;
        _outWrite = (_outWrite + 1) % _outFifo.Length;

        if (_outCount == _outFifo.Length)
        {
            // Unreachable while the caller reads one sample per sample written, which Process does.
            // Advancing the read cursor keeps the FIFO coherent rather than silently corrupting it.
            _outRead = (_outRead + 1) % _outFifo.Length;
        }
        else
        {
            _outCount++;
        }
    }

    private float ReadOutput()
    {
        if (_outCount == 0) return 0f;

        var value = _outFifo[_outRead];
        _outRead = (_outRead + 1) % _outFifo.Length;
        _outCount--;
        return value;
    }

    public void Reset()
    {
        Array.Clear(_frame);
        Array.Clear(_overlap);
        Array.Clear(_inHop);
        Array.Clear(_outFifo);
        Array.Clear(_power);
        Array.Clear(_noisePower);
        Array.Fill(_gain, 1f);
        Array.Fill(_smoothed, 1f);
        Array.Fill(_currentMin, float.MaxValue);
        Array.Fill(_previousMin, float.MaxValue);

        _inHopCount = 0;
        _outRead = 0;
        _outWrite = 0;
        _outCount = 0;

        // Re-learn the floor, fading suppression back in as it does: after a pause the room may be
        // a different room.
        _framesSeen = 0;
        _subWindowPosition = 0;
        _subWindowsSeen = 0;
    }
}
