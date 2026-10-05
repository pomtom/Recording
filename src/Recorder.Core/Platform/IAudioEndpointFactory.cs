using NAudio.Wave;

namespace Recorder.Platform;

/// <summary>
/// Opens the OS audio devices the recorder listens to.
/// </summary>
/// <remarks>
/// Each method returns an unstarted NAudio <see cref="IWaveIn"/>. Everything after that point —
/// buffering, channel mapping, resampling, noise suppression, gain, mute and mixing — is shared code
/// in Core, so both platforms produce audio through exactly the same pipeline.
/// </remarks>
public interface IAudioEndpointFactory
{
    /// <summary>Captures what the computer is playing.</summary>
    /// <param name="deviceId">A specific output device, or null for the system default.</param>
    /// <remarks>May throw; the caller then records without this source.</remarks>
    IWaveIn CreateSystemAudioCapture(string? deviceId);

    /// <summary>Captures a microphone.</summary>
    /// <param name="deviceId">A specific input device, or null for the system default.</param>
    /// <remarks>May throw; the caller then records without this source.</remarks>
    IWaveIn CreateMicrophoneCapture(string? deviceId);
}
