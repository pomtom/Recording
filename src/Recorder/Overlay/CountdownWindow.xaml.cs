using System.Windows;
using System.Windows.Interop;
using Recorder.Utils;

namespace Recorder.Overlay;

/// <summary>
/// The 3 → 2 → 1 → "Recording…" countdown shown before capture starts.
/// </summary>
/// <remarks>
/// Click-through and capture-excluded, so it neither blocks whatever the user is about to record
/// nor appears in the recording. The pipeline is already running by the time this is on screen —
/// the countdown is purely a chance for the user to get ready, not warm-up time.
/// </remarks>
public partial class CountdownWindow : Window
{
    public CountdownWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        CaptureExclusion.Exclude(handle);
        CaptureExclusion.MakeClickThrough(handle);
    }

    public void ShowCount(int value)
    {
        CountText.FontSize = 86;
        CountText.Text = value.ToString();
    }

    public void ShowStarting()
    {
        CountText.FontSize = 26;
        CountText.Text = "Recording…";
    }

    /// <summary>Centres the window on the given work area.</summary>
    public void CenterOn(Rect area)
    {
        const double size = 200;
        Left = area.Left + ((area.Width - size) / 2);
        Top = area.Top + ((area.Height - size) / 2);
    }
}
