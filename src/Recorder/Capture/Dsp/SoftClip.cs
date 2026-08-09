namespace Recorder.Capture.Dsp;

/// <summary>
/// Curves loud samples into the ±1.0 ceiling instead of letting them clip.
/// </summary>
/// <remarks>
/// Two places need this and they need it to behave identically: a boosted microphone can exceed
/// full scale on its own, and summing several sources can exceed it again. Hard clipping either one
/// produces harsh crackle, so samples above the knee are eased into the ceiling — audibly a gentle
/// compression. Below the knee the signal is bit-for-bit untouched, which is the normal case.
/// </remarks>
public static class SoftClip
{
    private const float Knee = 0.75f;
    private const float Range = 1f - Knee;

    public static float Apply(float value)
    {
        var magnitude = Math.Abs(value);
        if (magnitude <= Knee) return value;

        var over = Math.Min((magnitude - Knee) / Range, 1f);

        // Quadratic ease-out: continuous at the knee, asymptotic at 1.0.
        magnitude = Knee + (Range * (over - (over * over * 0.5f)));
        return value < 0 ? -magnitude : magnitude;
    }
}
