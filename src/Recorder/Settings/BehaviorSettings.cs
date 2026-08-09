using System.Text.Json.Serialization;

namespace Recorder.Settings;

/// <summary>What the window's close button does.</summary>
public enum CloseAction
{
    MinimizeToTray,
    Exit,
}

/// <summary>
/// Application-level behaviour: startup, the tray, notifications and power events.
/// </summary>
/// <remarks>
/// The four <c>StopOn*</c> flags exist because the reason for stopping is not equally compelling in
/// every case. A shutdown genuinely has to be handled or the file is left unfinalized, but a lock
/// screen is a judgement call — plenty of people lock their machine and expect a long capture to
/// keep running. Defaults preserve the original always-stop behaviour.
/// </remarks>
public sealed class BehaviorSettings
{
    public bool StartWithWindows { get; set; }

    /// <summary>Launch straight to the tray without showing the main window.</summary>
    public bool StartMinimized { get; set; }

    /// <summary><c>MinimizeToTray</c> or <c>Exit</c>.</summary>
    public string CloseButtonAction { get; set; } = "MinimizeToTray";

    public bool ShowTrayNotifications { get; set; } = true;

    public int NotificationDurationMs { get; set; } = 5000;

    public bool StopOnSleep { get; set; } = true;

    public bool StopOnLock { get; set; } = true;

    public bool StopOnLogOff { get; set; } = true;

    public bool StopOnShutdown { get; set; } = true;

    /// <summary>Finalize recordings left behind by a crash on the next launch.</summary>
    public bool RecoverInterruptedRecordings { get; set; } = true;

    [JsonIgnore]
    public CloseAction CloseButtonActionValue => ParseCloseAction(CloseButtonAction);

    public static CloseAction ParseCloseAction(string? value) =>
        value?.Trim().ToLowerInvariant() is "exit" or "close" or "quit"
            ? CloseAction.Exit
            : CloseAction.MinimizeToTray;

    public static string FormatCloseAction(CloseAction action) => action.ToString();

    public BehaviorSettings Clone() => (BehaviorSettings)MemberwiseClone();
}
