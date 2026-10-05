namespace Recorder.Settings;

/// <summary>How recordings are named.</summary>
/// <remarks>
/// See <see cref="Recorder.Utils.FilenameTemplate"/> for the token set. The default reproduces the
/// original hard-coded scheme exactly, so an upgrade changes nothing unless the user asks it to.
/// </remarks>
public sealed class NamingSettings
{
    public string FilenameTemplate { get; set; } = "{yyyy}-{MM}-{dd}_{HH}-{mm}-{ss}";

    public NamingSettings Clone() => (NamingSettings)MemberwiseClone();
}
