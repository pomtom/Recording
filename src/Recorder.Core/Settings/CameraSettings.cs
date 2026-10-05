namespace Recorder.Settings;

/// <summary>Which bubble outline the camera is drawn inside.</summary>
public enum CameraShape
{
    Circle,
    RoundedRect
}

/// <summary>
/// The webcam bubble: which camera, how it is captured, and how it looks on screen.
/// </summary>
/// <remarks>
/// <para>Position and size live here rather than being recomputed each launch because the bubble is
/// furniture — it belongs where the user last put it, the same way <c>OverlayLeft</c>/<c>OverlayTop</c>
/// work for the REC indicator.</para>
///
/// <para>Every geometric value is in <em>physical pixels</em>, unlike the REC indicator's. That is
/// not a style choice: the bubble's rectangle is measured with <c>GetWindowRect</c> and baked into
/// the video, so keeping it in one coordinate space end to end is what removes the DPI conversion
/// that would otherwise sit on one side of that round trip and not the other.</para>
///
/// <para><see cref="Enabled"/> is the camera's master switch and is persisted deliberately: unlike
/// mute, which resets with every recording, a user who has turned their camera off means it to stay
/// off until they say otherwise.</para>
/// </remarks>
public sealed class CameraSettings
{
    /// <summary>Master switch. False means no bubble and no camera device is opened at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>WinRT device id of the camera, or null to follow the first one enumerated.</summary>
    public string? DeviceId { get; set; }

    /// <summary>Requested capture width. The closest format the camera offers is used.</summary>
    public int CaptureWidth { get; set; } = 1280;

    public int CaptureHeight { get; set; } = 720;

    /// <summary>
    /// Requested camera frame rate, 5–60.
    /// </summary>
    /// <remarks>
    /// This is also the rate at which the camera re-drives compositing while the screen is idle, so
    /// it is a real cost knob and not only a quality one. 30 is plenty for a talking head.
    /// </remarks>
    public int Fps { get; set; } = 30;

    /// <summary>One of <c>Circle</c> or <c>RoundedRect</c>.</summary>
    public string Shape { get; set; } = "Circle";

    /// <summary>
    /// Horizontal flip, so the bubble reads like a mirror rather than a photograph.
    /// </summary>
    /// <remarks>
    /// Deliberately one setting rather than a preview/recording pair. Meeting apps mirror the
    /// preview and transmit un-mirrored, but they are not baking the preview into a file — this is.
    /// Two knobs here would let someone arrange a bubble that looks right and get a recording that
    /// does not, which is the one thing the whole composited design exists to prevent.
    /// </remarks>
    public bool Mirror { get; set; } = true;

    /// <summary>Last bubble position in physical pixels. Null places it bottom-right.</summary>
    public double? Left { get; set; }

    public double? Top { get; set; }

    /// <summary>Bubble width in physical pixels, 120–900. Height follows the shape.</summary>
    public double Size { get; set; } = 260;

    /// <summary>Ring drawn around the bubble, 0–16 physical pixels. 0 disables it.</summary>
    public double BorderThickness { get; set; } = 4;

    /// <summary>Border colour as <c>#RRGGBB</c> or <c>#AARRGGBB</c>.</summary>
    public string BorderColor { get; set; } = "#FFFFFFFF";

    /// <summary>0.2 to 1.0. Applies to the preview and the recording alike.</summary>
    public double Opacity { get; set; } = 1.0;

    /// <summary>
    /// How close to a corner a dropped bubble has to land before it snaps, in physical pixels.
    /// 0 turns snapping off.
    /// </summary>
    public double SnapDistance { get; set; } = 160;

    /// <summary>Gap in physical pixels left between the bubble and the screen edge when it snaps.</summary>
    public double SnapMargin { get; set; } = 32;

    public static CameraShape ParseShape(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "roundedrect" or "rounded" or "rect" or "rectangle" => CameraShape.RoundedRect,
        _ => CameraShape.Circle,
    };

    public static string FormatShape(CameraShape shape) =>
        shape == CameraShape.RoundedRect ? "RoundedRect" : "Circle";

    public CameraSettings Clone() => (CameraSettings)MemberwiseClone();
}
