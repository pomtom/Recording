using Recorder.Settings;

namespace Recorder.Capture.Dsp;

/// <summary>
/// The ordered stages applied to one channel of one source.
/// </summary>
/// <remarks>
/// Order is not arbitrary. High-pass first, so low-frequency rumble cannot skew the spectral stage's
/// per-bin noise estimate. Spectral subtraction next, doing the bulk of the work. The gate after it,
/// where it only has to separate cleaned speech from cleaned silence and can therefore be gentle.
/// Gain and the ceiling last, so the user's level adjustment is applied to the finished signal and
/// nothing downstream can push it back over full scale.
/// </remarks>
public sealed class AudioProcessorChain : IAudioProcessor
{
    private readonly IAudioProcessor[] _stages;

    /// <summary>The gain stage, kept to hand because mute is toggled through it while recording.</summary>
    public GainStage Gain { get; }

    private AudioProcessorChain(IAudioProcessor[] stages, GainStage gain)
    {
        _stages = stages;
        Gain = gain;
    }

    /// <summary>Builds the full microphone chain from the user's settings.</summary>
    public static AudioProcessorChain ForMicrophone(int sampleRate, AppSettings settings)
    {
        var ns = settings.NoiseSuppression;
        var stages = new List<IAudioProcessor>(4);

        if (ns.Enabled && ns.PresetValue != NoiseSuppressionPreset.Off)
        {
            if (ns.HighPassHz > 0)
                stages.Add(new HighPassProcessor(sampleRate, ns.HighPassHz));

            if (ns.SpectralStrength > 0)
                stages.Add(new SpectralSubtractor(sampleRate, ns.SpectralStrength, ns.SpectralFloorDb, ns.AdaptiveNoiseFloor));

            if (ns.GateRatio > 1)
                stages.Add(new NoiseGate(sampleRate, ns.GateThresholdDb, ns.GateRatio, ns.GateAttackMs, ns.GateReleaseMs));
        }

        var gain = new GainStage(sampleRate, settings.Audio.MicrophoneGainDb, ns.MuteRampMs);
        stages.Add(gain);

        return new AudioProcessorChain([.. stages], gain);
    }

    /// <summary>
    /// Builds the system-audio chain: gain and mute only.
    /// </summary>
    /// <remarks>
    /// System audio is program material — music, video, someone else's voice on a call. Noise
    /// suppression would be actively destructive there, so the only stage it gets is the one that
    /// lets the user set a level and mute it cleanly.
    /// </remarks>
    public static AudioProcessorChain ForSystemAudio(int sampleRate, AppSettings settings)
    {
        var gain = new GainStage(sampleRate, settings.Audio.SystemAudioGainDb, settings.NoiseSuppression.MuteRampMs);
        return new AudioProcessorChain([gain], gain);
    }

    public void Process(float[] buffer, int offset, int count)
    {
        foreach (var stage in _stages) stage.Process(buffer, offset, count);
    }

    public void Reset()
    {
        foreach (var stage in _stages) stage.Reset();
    }
}
