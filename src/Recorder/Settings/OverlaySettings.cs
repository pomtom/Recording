namespace Recorder.Settings;

/// <summary>Appearance and behaviour of the floating REC indicator.</summary>
public sealed class OverlaySettings
{
    public bool Enabled { get; set; } = true;

    public bool ShowElapsed { get; set; } = true;

    /// <summary>Whether a muted microphone is called out on the overlay.</summary>
    public bool ShowMuteState { get; set; } = true;

    /// <summary>0.2 to 1.0.</summary>
    public double Opacity { get; set; } = 0.95;

    /// <summary>0.75 to 2.0. Scales the whole indicator for high-DPI or distant screens.</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>
    /// Lets clicks pass straight through the overlay to whatever is beneath it.
    /// </summary>
    /// <remarks>This also makes the overlay undraggable, since it never receives the mouse.</remarks>
    public bool ClickThrough { get; set; }

    public bool PulseWhileRecording { get; set; } = true;

    public OverlaySettings Clone() => (OverlaySettings)MemberwiseClone();
}
