# Pomtom Recorder

A lightweight, portable Windows screen recorder. One `.exe`, no installer, no admin rights.
Records a monitor with system audio and microphone mixed into a single track, and writes MP4
(H.264 / AAC).

Windows 10 (2004 or newer) and Windows 11, 64-bit.

---

## Quick start

1. Run `Recorder.exe`.
2. Press **Ctrl+Shift+R** (or click **Start**). A 3-2-1 countdown appears, then recording begins.
3. Press **Ctrl+Shift+S** to stop. The MP4 is saved automatically — no dialog.

Closing the window minimises to the system tray; the recorder keeps running. Use **Exit** (in the
window or the tray menu) to quit, which finalizes any recording still in progress first.

### Hotkeys

| Action | Default |
|---|---|
| Start (or stop, if already recording) | `Ctrl+Shift+R` |
| Pause / resume | `Ctrl+Shift+P` |
| Stop | `Ctrl+Shift+S` |
| Mute microphone | `Ctrl+Shift+M` |
| Mute system audio | *unassigned* |

They work globally, including while the window is hidden. All five are configurable in Settings.
If another application already owns a combination, the recorder says so in a tray notification
rather than failing silently.

The two mute keys are optional and may be left blank — every global hotkey is one combination taken
away from every other application on the machine, so the system-audio one is off by default.

### Muting

Microphone and system audio mute independently, from the main window, the tray menu or a hotkey. All
three surfaces show the same state because the recorder owns it, not the button you last clicked.
Transitions are faded across ~12 ms so there is no click in the recording at either end of a muted
stretch. Muting does not stop capture — the device stays open, so unmuting is instant — and both
sources always start unmuted on a new recording.

---

## Where things go

| What | Where |
|---|---|
| Recordings | `D:\Recordings` by default, named `YYYY-MM-DD_HH-MM-SS.mp4` |
| Settings | `%LOCALAPPDATA%\PomtomRecorder\settings.json` |
| Logs (warnings and errors only) | `%LOCALAPPDATA%\PomtomRecorder\logs\` |
| Extracted ffmpeg | `%LOCALAPPDATA%\PomtomRecorder\bin\ffmpeg.exe` |

**If `D:\Recordings` is not writable** — no D: drive, a disconnected volume, a permissions problem —
the recorder falls back to `%USERPROFILE%\Videos\Recordings` at startup and updates `settings.json`
so the change is visible. That fallback deliberately uses the literal profile path rather than the
shell's Videos folder, which on many corporate machines is redirected into OneDrive; multi-gigabyte
recordings do not belong in a synced folder.

**Portable mode.** If a `settings.json` sits next to `Recorder.exe`, that copy is used instead of the
one under `%LOCALAPPDATA%`, so a USB stick can carry its own configuration.

---

## Settings

Everything is editable in the Settings window — **Capture · Audio · Video · Output · Hotkeys ·
General** — or by hand in `settings.json`. Nothing the app does is hard-coded; every value below is
a default, not a rule.

```json
{
  "SettingsVersion": 2,
  "OutputFolder": "D:\\Recordings",
  "Resolution": "1080p",
  "CustomHeight": 1080,
  "FPS": 60,
  "Countdown": 3,
  "CaptureCursor": true,
  "SuppressCaptureBorder": true,
  "RecordMicrophone": true,
  "RecordSystemAudio": true,
  "MonitorDeviceId": null,
  "StartHotkey": "Ctrl+Shift+R",
  "PauseHotkey": "Ctrl+Shift+P",
  "StopHotkey": "Ctrl+Shift+S",
  "MuteMicHotkey": "Ctrl+Shift+M",
  "MuteSystemHotkey": "",
  "AudioBitrateKbps": 192,

  "Audio": {
    "MicrophoneDeviceId": null,
    "SystemAudioDeviceId": null,
    "MicrophoneGainDb": 0.0,
    "SystemAudioGainDb": 0.0,
    "SampleRate": 48000,
    "Channels": 2
  },

  "NoiseSuppression": {
    "Enabled": true,
    "Preset": "Standard",
    "HighPassHz": 80,
    "SpectralStrength": 0.65,
    "SpectralFloorDb": -18,
    "AdaptiveNoiseFloor": true,
    "GateThresholdDb": -45,
    "GateRatio": 4.0,
    "GateAttackMs": 5,
    "GateReleaseMs": 120,
    "MuteRampMs": 12
  },

  "Video": {
    "QualityMode": "Quality",
    "Quality": 23,
    "MaxBitrateKbps": 0,
    "EncoderOverride": "Auto"
  },

  "Naming": { "FilenameTemplate": "{yyyy}-{MM}-{dd}_{HH}-{mm}-{ss}" },

  "Overlay": {
    "Enabled": true,
    "ShowElapsed": true,
    "ShowMuteState": true,
    "Opacity": 0.95,
    "Scale": 1.0,
    "ClickThrough": false,
    "PulseWhileRecording": true
  },

  "Behavior": {
    "StartWithWindows": false,
    "StartMinimized": false,
    "CloseButtonAction": "MinimizeToTray",
    "ShowTrayNotifications": true,
    "NotificationDurationMs": 5000,
    "StopOnSleep": true,
    "StopOnLock": true,
    "StopOnLogOff": true,
    "StopOnShutdown": true,
    "RecoverInterruptedRecordings": true
  },

  "Logging": { "MinimumLevel": "Warning", "RetainedDays": 7, "MaxFileSizeMb": 8 }
}
```

### Capture

- `Resolution` — `720p`, `1080p`, `1440p`, `Native`, or `Custom` to use `CustomHeight` (240–4320).
  The width always follows the monitor's aspect ratio, and the source is never upscaled: asking for
  1440p on a 1080p display records 1080p.
- `FPS` — 10 to 240. The Settings dropdown offers the usual choices; the file accepts any value in
  range.
- `Countdown` — 0 to 60 seconds. `0` starts immediately.
- `MonitorDeviceId` — e.g. `\\.\DISPLAY1`. `null` follows the primary display.
- `SuppressCaptureBorder` — hides the yellow "being captured" border Windows 11 draws. No effect on
  Windows 10, which does not draw one.

### Audio and noise suppression

- `MicrophoneDeviceId` / `SystemAudioDeviceId` — `null` follows the Windows default, which is what
  you usually want: a headset unplugged and replaced keeps working without opening Settings. A
  device that has since disappeared falls back to the default with a warning rather than failing.
- `Channels` — `2` for stereo, `1` for mono (roughly half the audio bitrate for a voice recording).
- `Preset` — `Off`, `Light`, `Standard`, `Strong` or `Custom`. Choosing a named preset **writes its
  values into the fields below it**, so the file always shows the numbers actually in effect;
  `Custom` simply means one of them has been edited.
- Noise suppression applies to the **microphone only**. System audio is program material — denoising
  it would damage the recording.

### Video

- `Quality` — 15 (best) to 35 (smallest). One number across all four encoders, which each spell it
  differently internally.
- `MaxBitrateKbps` — `0` derives a ceiling from the frame height. In `Quality` mode an explicit
  ceiling is only a safety valve; switch `QualityMode` to `Bitrate` to make it binding.
- `EncoderOverride` — `Auto`, `Nvenc`, `QuickSync`, `Amf` or `X264`. A manual choice is proved with
  a real test encode; if it turns out to be unusable on this machine, the recorder says so and falls
  back to the automatic pick rather than losing the take.

### Filenames

`FilenameTemplate` accepts `{yyyy} {MM} {dd} {HH} {mm} {ss} {date} {time} {monitor} {counter}`, plus
any literal text. Anything else is left alone, so a typo is visible in the filename rather than
silently disappearing. Characters Windows forbids are stripped, and a pattern that would produce
nothing usable falls back to the default. The Settings window shows a live example.

### Everything else

`Behavior` covers start-with-Windows, what the close button does, tray notifications, and which
system events end a recording — sleep, lock, sign-out and shutdown are individually switchable, since
locking your machine and expecting a long capture to continue is a perfectly reasonable thing to
want. Turning off the shutdown case risks leaving a recording unfinalized, which the next launch
would then repair.

Invalid values are clamped rather than rejected, and a corrupt file falls back to defaults, so a bad
edit can never stop the app from starting. An older `settings.json` from before these sections
existed loads unchanged — the original keys never moved, and anything absent takes its default.

---

## How it works

```
Windows Graphics Capture (free-threaded frame pool)
   └─> D3D11 video processor: scale + BGRA→NV12 in one GPU pass
         └─> staging texture → three-buffer rotation
               └─> pacer thread @ fps ──> named pipe ─┐
                                                       ├─> ffmpeg ──> .mp4.part (fragmented)
WASAPI loopback ─> gain/mute ─┐                        │                    │
                              ├─> rings ────────────   │              remux -c copy
WASAPI mic ─> highpass ─> spectral ─> gate ─> gain/mute│               +faststart
                          subtract        └─> mixer thread @10 ms ─────┘    ▼
                                              (sum, soft-clip, s16le)  <template>.mp4
```

Four design points carry most of the weight:

**One clock drives both streams.** The frame pacer emits frame *n* when the recording clock reaches
`n / fps`, and the audio mixer emits exactly `elapsed × 48000` samples. Because both are positioned
from the same monotonic clock rather than from their own arrival rates, audio and video cannot drift
apart over a long recording. Pause is simply the clock not advancing, which is why a resumed
recording has no seam. Measured over a 19-second take: video 19.42 s, audio 19.41 s.

**Capture is variable-rate; the output is not.** Windows only delivers a frame when the screen
changes, so the pacer repeats the last captured frame when nothing is happening. It never *skips* a
frame — in a raw CFR stream every frame occupies exactly 1/fps of the timeline, so dropping one
would shorten the video against the audio. If the encoder falls behind, the pacer catches up by
writing the missed frames rather than discarding them.

**The file is playable before it is finished.** During capture ffmpeg writes a *fragmented* MP4,
which stays valid however abruptly it is truncated. A clean stop stream-copies it to a normal
faststart MP4 in about a second; a crash leaves a `.mp4.part` that the next launch finalizes the
same way.

**Noise suppression estimates the floor from a minimum, not an average.** Each frequency bin's noise
level is taken as the smallest smoothed power it has shown in the last 1.5–3 seconds. An average
would have to assume the recording opens with silence in order to learn anything trustworthy, and
this recorder cannot promise that — the chain sees its first sample the moment the timeline starts,
and the user may already be talking. A minimum needs no such assumption: no bin stays loud for three
solid seconds during speech, so the floor is found correctly whatever is happening at the start.
Suppression also fades in over the first second, so even a badly-conditioned start cannot damage the
opening words. Anything genuinely steady for longer than the window — mains hum, a fan — is treated
as noise and removed, which is the intended behaviour. The whole chain adds a *fixed* 10.6 ms of
latency that never accumulates, which is what makes it safe to put in the recording path at all.

---

## Behaviour worth knowing

- **One monitor per recording.** Windows Graphics Capture has no virtual-desktop capture item — this
  is an OS constraint, not a shortcut. Pick the monitor in Settings.
- **The app's own windows never appear in recordings.** The overlay, countdown, main and settings
  windows are excluded via `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`.
- **Recording stops by itself** on sleep, lock, log-off and shutdown, finalizing the MP4 first. Each
  of the four is individually switchable in Settings.
- **Hardware encoding is verified, not assumed.** At first launch each candidate encoder
  (NVENC → Quick Sync → AMF) is proved with a fifth-of-a-second test encode before being trusted,
  because an encoder ffmpeg was *built* with can still be unusable on a given machine — an outdated
  NVIDIA driver, a disabled GPU, a busy encoder session. The result is cached. libx264 is the floor
  and always works.
- **Audio devices are resolved when recording starts**, not tracked live. Switching your default
  microphone or speakers mid-recording has no effect until the next take.
- **Settings are snapshotted at the moment a recording starts.** Editing them while one is running
  cannot change a capture already in flight.
- **First launch does a little extra work**: ffmpeg is unpacked once (~1 s). Later launches skip it.

### Measured on a 1920×1200 display, Intel Quick Sync, 18-second take

| | |
|---|---|
| Startup to visible window | 1.0 s cold, 1.1 s warm |
| Idle memory | 159 MB working set, ~90 MB private |
| Memory while recording 1728×1080 @ 60 | ~300 MB working set |
| CPU while recording | ~0.8 of one core (hardware encoding does the rest) |
| A/V alignment | video 18.44 s, audio 18.41 s |

---

## Building

Requires the .NET 8 SDK (or newer — `global.json` rolls forward).

```powershell
.\build.ps1
```

This fetches ffmpeg and generates `app.ico` if they are missing, then publishes
`publish\Recorder.exe` — self-contained, single-file, ~202 MB. That size is the .NET runtime, WPF
and ffmpeg all travelling inside one file, which is the price of "copy it to a USB stick and it
runs".

The bundle is published **uncompressed** on purpose. A compressed single-file bundle is decompressed
into memory in its entirety at every launch; with ffmpeg embedded that measured at 371 MB of working
set and a 1.8 s start, against 159 MB and 1.0 s uncompressed. ffmpeg is gzipped on its own instead
(98 MB → 37 MB), which reclaims most of the file size without that cost, and is inflated once on
first run.

Individual steps, if you want them:

```powershell
.\tools\fetch-ffmpeg.ps1     # downloads ffmpeg and writes src\Recorder\assets\ffmpeg.exe.gz
.\tools\make-icon.ps1        # generates src\Recorder\assets\app.ico
dotnet build                 # ordinary debug build
```

`src\Recorder\assets\ffmpeg.exe.gz` is gitignored — fetch it after cloning. The app still builds
without it and will then look for `ffmpeg.exe` beside the exe or on `PATH`.

### Layout

```
src/Recorder/
  Core/       recording state machine, session, clock, frame pacer
  Capture/    Windows Graphics Capture, D3D11 interop, GPU frame conversion, audio capture + mixer
  Capture/Dsp/  microphone cleanup: high-pass, spectral subtraction, gate, gain and mute ramp
  Encoding/   ffmpeg provisioning, encoder validation, argument building, process + pipes, remux
  Hotkeys/    global hotkey registration and gesture parsing
  Overlay/    floating REC indicator, countdown
  UI/         main and settings windows
  Tray/       system tray icon and menu
  Power/      sleep / lock / shutdown handling
  Recovery/   journal and crash recovery
  Settings/   configuration model, load/save/validate
  Utils/      paths, logging, P/Invoke, capture exclusion, output folder resolution
```

---

## Troubleshooting

**Recording fails immediately.** Check `%LOCALAPPDATA%\PomtomRecorder\logs\`. The log carries
ffmpeg's own stderr, which usually names the problem directly.

**A hotkey does nothing.** Another application has claimed it. The recorder reports this in a tray
notification at startup; pick a different combination in Settings.

**Video is fine but there is no sound.** Check that the source is enabled in Settings, and that its
mute toggle is off. The Audio tab has a live level meter under each device — if it does not move,
the recording will not have sound either. A microphone that cannot be opened is logged and the
recording continues with system audio alone rather than failing; the startup message says which
source was lost.

**Speech sounds thin or watery.** Noise suppression is too aggressive for your room. Drop it to
`Light` in Settings → Audio, or `Off`. Anything held at a steady level for more than about three
seconds is treated as noise by design, so a sustained tone will be removed.

**The first second of a recording is noisier than the rest.** That is deliberate: suppression fades
in while the noise floor is being measured, because getting it wrong in the other direction would
damage your opening words. Add a countdown if you want the estimate settled before you speak.

**A recording was interrupted.** Just start the app again; it finalizes any orphaned `.mp4.part`
automatically and reports what it recovered.

**Force a re-probe of encoders.** Delete `%LOCALAPPDATA%\PomtomRecorder\bin\encoder.cache`.
