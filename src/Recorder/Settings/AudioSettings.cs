namespace Recorder.Settings;

/// <summary>
/// Which audio devices to record and how loud each one should be.
/// </summary>
/// <remarks>
/// A null device id means "follow whatever Windows currently calls the default", which is what the
/// recorder did before devices were selectable and remains the right behaviour for most people: a
/// headset that is unplugged and replaced keeps working without anyone opening Settings.
/// </remarks>
public sealed class AudioSettings
{
    /// <summary>Endpoint id of the microphone, or null to follow the Windows default.</summary>
    public string? MicrophoneDeviceId { get; set; }

    /// <summary>Endpoint id of the playback device to capture, or null for the Windows default.</summary>
    public string? SystemAudioDeviceId { get; set; }

    /// <summary>Microphone gain in decibels. 0 leaves the signal untouched.</summary>
    public double MicrophoneGainDb { get; set; }

    /// <summary>System audio gain in decibels. 0 leaves the signal untouched.</summary>
    public double SystemAudioGainDb { get; set; }

    /// <summary>Mixer sample rate. 44100 or 48000.</summary>
    public int SampleRate { get; set; } = 48_000;

    /// <summary>Channels in the recorded track. 1 (mono) or 2 (stereo).</summary>
    public int Channels { get; set; } = 2;

    public AudioSettings Clone() => (AudioSettings)MemberwiseClone();
}
