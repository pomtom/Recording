using NAudio.CoreAudioApi;
using NAudio.Wave;
using Recorder.Capture;

namespace Recorder.Platform;

/// <summary>Opens WASAPI devices: loopback for system audio, capture for the microphone.</summary>
public sealed class WindowsAudioEndpoints : IAudioEndpointFactory
{
    public IWaveIn CreateSystemAudioCapture(string? deviceId)
    {
        var device = AudioDeviceEnumerator.GetDevice(deviceId, DataFlow.Render);
        return new WasapiLoopbackCapture(device);
    }

    public IWaveIn CreateMicrophoneCapture(string? deviceId)
    {
        var device = AudioDeviceEnumerator.GetDevice(deviceId, DataFlow.Capture);
        return new WasapiCapture(device);
    }
}
