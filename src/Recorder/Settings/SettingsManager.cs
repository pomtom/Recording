using System.IO;
using System.Text.Json;
using Recorder.Hotkeys;
using Recorder.Utils;

namespace Recorder.Settings;

/// <summary>
/// Loads, validates and persists <see cref="AppSettings"/>.
/// </summary>
/// <remarks>
/// A corrupt or hand-mangled settings file must never stop the app from starting, so loading is
/// total: anything unparseable falls back to defaults, and out-of-range values are clamped rather
/// than rejected. Saving is atomic (write to a temp file, then replace) so a crash mid-write cannot
/// leave a truncated config behind.
/// </remarks>
public sealed class SettingsManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        // Without this the default encoder escapes '+' as +, turning "Ctrl+Shift+R" into
        // something nobody wants to hand-edit.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _gate = new();
    private AppSettings _current = new();

    /// <summary>Raised after <see cref="Save"/> completes, on the caller's thread.</summary>
    public event EventHandler<AppSettings>? Changed;

    /// <summary>A snapshot of the current settings. Never null.</summary>
    public AppSettings Current
    {
        get { lock (_gate) return _current; }
    }

    public string SettingsPath => AppPaths.SettingsPath;

    public void Load()
    {
        AppSettings loaded;
        var existed = false;

        try
        {
            var path = AppPaths.SettingsPath;
            existed = File.Exists(path);
            if (existed)
            {
                var json = File.ReadAllText(path);
                loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
            else
            {
                loaded = new AppSettings();
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "settings.json could not be read; falling back to defaults.");
            loaded = new AppSettings();
        }

        Validate(loaded);

        lock (_gate) _current = loaded;

        // Write the defaults out on first run so the file is there to hand-edit.
        if (!existed) Persist(loaded);
    }

    /// <summary>Replaces the current settings and writes them to disk.</summary>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Validate(settings);

        lock (_gate) _current = settings;

        Persist(settings);
        Changed?.Invoke(this, settings);
    }

    /// <summary>
    /// Mutates the current settings in place and persists them, without raising <see cref="Changed"/>.
    /// Used for incidental state such as the overlay position and the output-folder fallback, where
    /// re-applying hotkeys and UI bindings would be pointless churn.
    /// </summary>
    public void Update(Action<AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        AppSettings snapshot;
        lock (_gate)
        {
            mutate(_current);
            Validate(_current);
            snapshot = _current.Clone();
        }
        Persist(snapshot);
    }

    private void Persist(AppSettings settings)
    {
        try
        {
            var path = AppPaths.SettingsPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(settings, JsonOptions);
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);

            // File.Replace needs an existing destination; Move covers the first save.
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Could not persist settings.json.");
        }
    }

    /// <summary>Clamps every field into a usable range. Mutates in place.</summary>
    private static void Validate(AppSettings s)
    {
        s.Resolution = AppSettings.FormatResolution(AppSettings.ParseResolution(s.Resolution));

        // The spec allows 30 or 60 only; snap anything else to the nearer of the two.
        s.FPS = s.FPS <= 45 ? 30 : 60;

        s.Countdown = Math.Clamp(s.Countdown, 0, 10);
        s.AudioBitrateKbps = Math.Clamp(s.AudioBitrateKbps, 64, 320);

        if (string.IsNullOrWhiteSpace(s.OutputFolder))
            s.OutputFolder = AppPaths.FallbackOutputFolder;

        if (string.IsNullOrWhiteSpace(s.MonitorDeviceId))
            s.MonitorDeviceId = null;

        s.StartHotkey = NormalizeHotkey(s.StartHotkey, "Ctrl+Shift+R");
        s.PauseHotkey = NormalizeHotkey(s.PauseHotkey, "Ctrl+Shift+P");
        s.StopHotkey = NormalizeHotkey(s.StopHotkey, "Ctrl+Shift+S");
    }

    private static string NormalizeHotkey(string? value, string fallback)
    {
        if (HotkeyGesture.TryParse(value, out var gesture) && gesture is not null)
            return gesture.ToString();

        if (!string.IsNullOrWhiteSpace(value))
            Log.Warn($"Unrecognised hotkey '{value}'; using {fallback}.");

        return fallback;
    }
}
