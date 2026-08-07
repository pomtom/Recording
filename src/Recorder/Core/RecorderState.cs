namespace Recorder.Core;

public enum RecorderState
{
    /// <summary>Nothing in flight. The only state from which a recording can start.</summary>
    Idle,

    /// <summary>Countdown is on screen; capture is warm but the clock has not started.</summary>
    CountingDown,

    Recording,

    Paused,

    /// <summary>Draining the encoder and remuxing. Rejects new commands until it lands back in Idle.</summary>
    Finalizing,
}

public static class RecorderStateExtensions
{
    public static bool IsActive(this RecorderState state) =>
        state is RecorderState.CountingDown or RecorderState.Recording or RecorderState.Paused;

    public static bool CanStart(this RecorderState state) => state == RecorderState.Idle;

    public static bool CanPause(this RecorderState state) =>
        state is RecorderState.Recording or RecorderState.Paused;

    public static bool CanStop(this RecorderState state) => state.IsActive();
}
