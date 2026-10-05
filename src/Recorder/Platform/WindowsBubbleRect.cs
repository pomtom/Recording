using Recorder.Capture;
using Recorder.Utils;

namespace Recorder.Platform;

/// <summary>
/// Reads the camera bubble's position straight from its HWND with <c>GetWindowRect</c>.
/// </summary>
/// <remarks>
/// <c>GetWindowRect</c> answers in physical pixels — the space the monitor bounds and the capture
/// are in — and is safe to call from the pacer thread, which is why the compositor asks the window
/// rather than WPF.
/// </remarks>
public sealed class WindowsBubbleRect(IntPtr hwnd) : IBubbleRectProvider
{
    public bool TryGetBounds(out PixelRect bounds) => TryGet(hwnd, out bounds);

    public static bool TryGet(IntPtr hwnd, out PixelRect bounds)
    {
        bounds = default;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var r)) return false;

        bounds = new PixelRect(r.Left, r.Top, r.Width, r.Height);
        return true;
    }
}
