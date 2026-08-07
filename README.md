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

They work globally, including while the window is hidden. All three are configurable in Settings.
If another application already owns a combination, the recorder says so in a tray notification
rather than failing silently.

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

Everything is editable in the Settings window, or by hand in `settings.json`:

```json
{
  "OutputFolder": "D:\\Recordings",
  "Resolution": "1080p",
  "FPS": 60,
  "Countdown": 3,
  "CaptureCursor": true,
  "RecordMicrophone": true,
  "RecordSystemAudio": true,
  "MonitorDeviceId": null,
  "StartHotkey": "Ctrl+Shift+R",
  "PauseHotkey": "Ctrl+Shift+P",
  "StopHotkey": "Ctrl+Shift+S",
  "AudioBitrateKbps": 192
}
```

- `Resolution` — `720p`, `1080p`, `1440p` or `Native`. The width follows the monitor's aspect ratio,
  and the source is never upscaled: asking for 1440p on a 1080p display records 1080p.
- `FPS` — `30` or `60`.
- `Countdown` — seconds, or `0` to start immediately.
- `MonitorDeviceId` — e.g. `\\.\DISPLAY1`. `null` follows the primary display.

Invalid values are clamped rather than rejected, and a corrupt file falls back to defaults, so a bad
edit can never stop the app from starting.

---

## How it works

```
Windows Graphics Capture (free-threaded frame pool)
   └─> D3D11 video processor: scale + BGRA→NV12 in one GPU pass
         └─> staging texture → three-buffer rotation
               └─> pacer thread @ fps ──> named pipe ─┐
                                                       ├─> ffmpeg ──> .mp4.part (fragmented)
WASAPI loopback ─┐                                     │                    │
                 ├─> resample 48 kHz stereo ─> rings   │              remux -c copy
WASAPI mic ──────┘        └─> mixer thread @10 ms ────┘               +faststart
                              (sum, soft-clip, s16le)                       ▼
                                                              YYYY-MM-DD_HH-MM-SS.mp4
```

Three design points carry most of the weight:

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

---

## Behaviour worth knowing

- **One monitor per recording.** Windows Graphics Capture has no virtual-desktop capture item — this
  is an OS constraint, not a shortcut. Pick the monitor in Settings.
- **The app's own windows never appear in recordings.** The overlay, countdown, main and settings
  windows are excluded via `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`.
- **Recording stops by itself** on sleep, lock, log-off and shutdown, finalizing the MP4 first.
- **Hardware encoding is verified, not assumed.** At first launch each candidate encoder
  (NVENC → Quick Sync → AMF) is proved with a fifth-of-a-second test encode before being trusted,
  because an encoder ffmpeg was *built* with can still be unusable on a given machine — an outdated
  NVIDIA driver, a disabled GPU, a busy encoder session. The result is cached. libx264 is the floor
  and always works.
- **Loopback follows the default playback device** as it was when recording started. Switching
  output devices mid-recording is not tracked.
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

**Video is fine but there is no sound.** Check that the source is enabled in Settings. A microphone
that cannot be opened is logged and the recording continues with system audio alone rather than
failing — the startup message says which source was lost.

**A recording was interrupted.** Just start the app again; it finalizes any orphaned `.mp4.part`
automatically and reports what it recovered.

**Force a re-probe of encoders.** Delete `%LOCALAPPDATA%\PomtomRecorder\bin\encoder.cache`.
