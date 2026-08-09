````markdown
# Lightweight Portable Screen Recorder - Project Specification

## Project Overview

Build a **lightweight, portable Windows desktop application** for personal use that records the entire screen along with both system audio and microphone audio.

The application should focus on:

- Fast startup
- Minimal user interface
- High reliability
- Low CPU usage
- No installation required
- Portable single executable
- Windows 10 and Windows 11 support

The application should behave like a native Windows utility rather than a full-featured video editor.

---

# Configurability Principle

Every value in this document is a **default**, not a fixed behaviour. Anything a user could
reasonably want to change is exposed in the Settings window and in `settings.json`, including the
resolution, frame rate, countdown, audio devices, per-source gain, noise suppression, encoder
quality, filename pattern, hotkeys, overlay appearance, startup behaviour, which system events end a
recording, and log verbosity.

Internal tuning that no user should have to reason about — mixer buffer sizes, the audio startup
cushion, drain timeouts, GOP length, thread priorities — stays in code as named constants with the
reasoning recorded alongside them.

Settings are validated by clamping, never by rejection: a corrupt or hand-mangled file must never
stop the application from starting.

---

# Primary Goals

- Record the entire screen
- Record system audio
- Record microphone audio
- Mix both audio sources into a single audio track
- Save recordings as MP4
- Support configurable recording quality
- Support configurable hotkeys
- Run from a single executable
- Minimize to the system tray
- Keep the application extremely lightweight

---

# Target Platform

- Windows 10 (64-bit)
- Windows 11 (64-bit)

---

# Technology Stack

## Programming Language

**C# (.NET 8)**

Reason:

- Excellent Windows API support
- Modern language
- Easy maintenance
- High reliability
- Fast development
- Native Windows integration
- Supports publishing as a single portable executable

---

## UI Framework

- WPF

Reason:

- Mature
- Stable
- Lightweight
- Excellent Windows desktop support

---

## Screen Capture

Use:

- Windows Graphics Capture API

Reason:

- Native Windows API
- High performance
- Low latency
- GPU accelerated
- Recommended by Microsoft

---

## Audio Capture

### System Audio

Use:

- WASAPI Loopback

### Microphone

Use:

- WASAPI Capture

Requirements:

- Record both simultaneously
- Mix into one audio track

---

## Video Encoding

Use:

- FFmpeg

Requirements:

- MP4 output
- Hardware acceleration when available
- High stability
- Low CPU usage

---

## Configuration Storage

Use:

- JSON

---

## Logging

Optional:

- Serilog

Only log:

- Errors
- Warnings
- Unexpected exceptions

No analytics.

No telemetry.

---

# Functional Requirements

## Recording

The application shall:

- Record the selected monitor
- Record the full desktop
- Record at configurable resolution
- Default resolution: 1080p
- Record at 60 FPS
- Save recordings as MP4
- Record indefinitely until stopped
- Record microphone
- Record system audio
- Mix both into one audio stream

---

## Countdown

When the user starts recording:

Display:

3

2

1

Recording...

Countdown duration:

3 seconds

Configurable later.

---

## Pause / Resume

Support:

- Pause recording
- Resume recording

Maintain proper audio/video synchronization.

---

## Stop Recording

Stopping shall:

- Finish encoding
- Finalize MP4
- Save automatically

No confirmation dialog.

---

# Recording Settings

## Default Resolution

1080p

User can change to:

- 720p
- 1080p
- 1440p
- Native Resolution
- Custom height (240–4320; width follows the monitor's aspect ratio)

---

## Frame Rate

Default:

60 FPS

Allow any rate from 10 to 240 FPS. The Settings dropdown offers the common choices
(24, 25, 30, 48, 50, 60, 120, 144); `settings.json` accepts any value in range.

---

## Encoder Quality

Default:

Quality index 23, constant-quality mode, automatic encoder selection.

User can change:

- Quality index (15 best — 35 smallest)
- Quality mode (constant quality or target bitrate)
- Bitrate ceiling (0 derives one from the frame height)
- Encoder (Auto, NVENC, Quick Sync, AMF, x264)

A manually chosen encoder is validated with a real test encode; if it cannot run on this machine the
application reports it and falls back to the automatic choice rather than losing the recording.

---

## File Format

MP4

Codec:

H.264

Audio:

AAC

---

# Audio Requirements

Capture:

✔ System Audio

✔ Microphone

Mix into:

Single audio track

Configurable:

- Which device each source uses (default: follow the Windows default endpoint)
- Per-source gain, −24 to +24 dB
- Sample rate (44.1 or 48 kHz) and channels (mono or stereo)
- Bitrate (64–320 kbps)

---

# Noise Cancellation

Applied to the **microphone only**. System audio is program material; suppressing it would damage
the recording.

Chain, in order:

1. High-pass filter — removes rumble below the speech band
2. Spectral subtraction — removes steady broadband noise (fans, hiss, hum, room tone)
3. Noise gate (downward expander) — finishes the job in the gaps between phrases
4. Gain and ceiling — the user's level, the mute ramp, and a soft limiter

Presets: Off · Light · Standard (default) · Strong · Custom

Choosing a named preset writes its values into the individual parameters, so the configuration file
always shows the numbers actually in effect and there is never a second, hidden source of truth.
Every parameter is individually editable, which is what "Custom" means.

Requirements:

- Constant latency. The whole chain must add a fixed delay that never accumulates, or audio and
  video would drift apart over a long recording.
- Must never damage speech that begins the instant recording starts. The noise floor is therefore
  estimated from a rolling minimum rather than an average, and suppression fades in over the first
  second.
- Must degrade to a no-op when disabled, with no measurable effect on the signal.

---

# Muting

Microphone and system audio mute independently.

Requirements:

- Controllable from the main window, the tray menu and a global hotkey
- All surfaces show the same state, owned in one place rather than by whichever control was used
- Transitions are faded, not switched, so there is no click in the recording
- Capture keeps running while muted, so unmuting is instant
- Both sources start unmuted on every new recording

---

# Default Save Location

```
D:\Recordings\
```

The application remembers the last folder.

---

# File Naming

Default format:

```
YYYY-MM-DD_HH-MM-SS.mp4
```

Example:

```
2026-08-07_09-35-20.mp4
```

Configurable through a filename template accepting `{yyyy} {MM} {dd} {HH} {mm} {ss} {date} {time}
{monitor} {counter}` plus literal text. Unknown tokens are left verbatim so a typo is visible rather
than silently dropped; characters Windows forbids are stripped; a template that would produce
nothing usable falls back to the default.

---

# User Interface

Minimal interface.

Buttons:

- Start Recording
- Pause
- Stop
- Mute microphone (toggle)
- Mute system audio (toggle)
- Settings
- Open folder
- Exit

The mute toggles show their state visually — colour and label, not only a caption swap — along with
the hotkey assigned to each, and explain why they are unavailable when they are.

The Settings window is organised into tabs: **Capture · Audio · Video · Output · Hotkeys · General**.
The Audio tab includes a live level meter under each device picker, so a wrongly-chosen or silent
device can be spotted before recording rather than after.

---

# Floating Recording Indicator

Display while recording:

```
🔴 REC 00:12:36
```

Requirements:

- Always on top
- Small
- Draggable
- Transparent background
- Does not appear in recording
- Shows when the microphone is muted

Configurable: shown or hidden, elapsed time shown or hidden, mute badge shown or hidden, opacity,
size, pulse, and click-through. Click-through necessarily makes the indicator undraggable, since it
never receives the mouse.

---

# System Tray

Closing the window should:

Minimize to tray

NOT exit — though this is configurable, and a user who wants the close button to really close can
say so.

Tray menu:

- Start Recording
- Pause
- Stop
- Mute microphone (checkable)
- Mute system audio (checkable)
- Open
- Exit

Notifications can be switched off, and their duration set.

---

# Startup

Do NOT start with Windows, by default.

Configurable. When enabled, the application registers itself under the per-user `Run` key — never
HKLM, so no administrator rights are ever required. Because the application is portable, the
registration is re-checked on every launch and corrected if the executable has moved.

Starting minimised to the tray is separately configurable.

---

# Recording History

No recording history.

The application simply saves recordings.

---

# Hotkeys

Global hotkeys.

Defaults:

| Action | Default |
|---|---|
| Start | `Ctrl + Shift + R` |
| Pause / Resume | `Ctrl + Shift + P` |
| Stop | `Ctrl + Shift + S` |
| Mute microphone | `Ctrl + Shift + M` |
| Mute system audio | *unassigned* |

Requirements:

- Configurable
- Work globally
- Work while minimized
- The mute hotkeys may be left unassigned. Every global hotkey takes a combination away from every
  other application on the machine, so the rarely-wanted one is opt-in.

---

# Power Events

By default, automatically stop recording when:

- Windows sleeps
- Windows locks
- Windows signs out
- Windows shuts down

Gracefully finalize recording.

Each of the four is individually switchable. They are not equally compelling: a shutdown genuinely
has to be handled or the file is left unfinalized, whereas locking the screen is a judgement call and
plenty of people expect a long capture to keep running.

---

# Configuration File

JSON, hand-editable, with the full surface documented in `README.md`.

Top-level keys carry the original settings; everything added since lives in a nested section
(`Audio`, `NoiseSuppression`, `Video`, `Naming`, `Overlay`, `Behavior`, `Logging`). That split is not
cosmetic — it is what lets an older configuration file load without a migration step, because the
keys someone actually took the trouble to change never move, and a missing section simply takes its
defaults.

```json
{
  "SettingsVersion": 2,
  "OutputFolder": "D:\\Recordings",
  "Resolution": "1080p",
  "FPS": 60,
  "Countdown": 3,
  "CaptureCursor": true,
  "RecordMicrophone": true,
  "RecordSystemAudio": true,
  "StartHotkey": "Ctrl+Shift+R",
  "PauseHotkey": "Ctrl+Shift+P",
  "StopHotkey": "Ctrl+Shift+S",
  "MuteMicHotkey": "Ctrl+Shift+M",
  "MuteSystemHotkey": "",

  "Audio":            { "MicrophoneDeviceId": null, "MicrophoneGainDb": 0.0, "Channels": 2 },
  "NoiseSuppression": { "Enabled": true, "Preset": "Standard" },
  "Video":            { "Quality": 23, "EncoderOverride": "Auto" },
  "Naming":           { "FilenameTemplate": "{yyyy}-{MM}-{dd}_{HH}-{mm}-{ss}" },
  "Overlay":          { "Enabled": true, "Opacity": 0.95 },
  "Behavior":         { "StartWithWindows": false, "StopOnLock": true },
  "Logging":          { "MinimumLevel": "Warning", "RetainedDays": 7 }
}
```

---

# Recommended Project Structure

```
ScreenRecorder/

│
├── App/
│
├── Capture/
│     ScreenCaptureService.cs
│     AudioCaptureService.cs
│     AudioDeviceEnumerator.cs
│     AudioMixer.cs
│     Dsp/
│       HighPassProcessor.cs
│       SpectralSubtractor.cs
│       NoiseGate.cs
│       GainStage.cs
│       AudioProcessorChain.cs
│
├── Recording/
│     RecordingManager.cs
│     Encoder.cs
│     RecorderState.cs
│
├── Hotkeys/
│     GlobalHotkeyManager.cs
│
├── Overlay/
│     RecordingOverlay.xaml
│
├── Tray/
│     TrayManager.cs
│
├── Settings/
│     SettingsManager.cs
│     Settings.json
│
├── UI/
│     MainWindow.xaml
│     SettingsWindow.xaml
│
├── Utils/
│
├── Program.cs
│
└── README.md
```

---

# Recording Workflow

```
Launch Application

        │

        ▼

Load Settings

        │

        ▼

Create Tray Icon

        │

        ▼

Wait for Hotkey

        │

        ▼

Ctrl + Shift + R

        │

        ▼

3 Second Countdown

        │

        ▼

Start Screen Capture

        │

        ▼

Capture System Audio

        │

        ▼

Capture Microphone

        │

        ▼

Mix Audio

        │

        ▼

Encode Video

        │

        ▼

Write MP4

        │

        ▼

User Stops Recording

        │

        ▼

Finalize MP4

        │

        ▼

Save File

        │

        ▼

Return to Idle
```

---

# Non-Functional Requirements

## Performance

Startup:

< 2 seconds

Idle RAM:

< 150 MB

Recording CPU:

As low as possible

Use GPU hardware encoding when available.

---

## Reliability

The application should support:

- Several hours of continuous recording
- Stable recording
- No audio drift
- No frame corruption
- No freezing

---

# Error Handling

If recording fails:

- Display user-friendly message
- Save partial recording if possible
- Log error
- Return to idle state

---

# Crash Recovery

If the application crashes during recording:

On next startup:

- Detect unfinished recording
- Recover temporary files
- Finalize MP4 if possible
- Notify user

This feature is highly recommended.

---

# Packaging

Publish as:

Single-file executable

Requirements:

- No installer
- Portable
- No administrator privileges required
- Can run from USB drive

---

# Future Extensibility

Although Version 1 is intentionally lightweight, the architecture should support future features without major redesign.

Potential future enhancements:

- Neural noise suppression (RNNoise via ffmpeg's `arnndn`, or a Windows Voice Clarity capture path)
- Acoustic echo cancellation
- Webcam recording
- OCR (searchable screen text)
- Speech-to-text transcription
- AI meeting summaries
- Automatic bookmarks
- Keyboard/mouse activity timeline
- Export to Markdown
- Export to PDF
- Multiple audio tracks
- Region recording
- Window recording
- Scheduled recording

These features should not be implemented now, but the architecture should allow them to be added later.

---

# Development Roadmap

## Phase 1

Project foundation

- Solution setup
- Portable executable
- Configuration system
- System tray integration

---

## Phase 2

Core recording engine

- Screen capture
- Audio capture
- MP4 encoding
- Countdown timer

---

## Phase 3

Recording controls

- Global hotkeys
- Pause/Resume
- Stop recording
- Floating recording overlay

---

## Phase 4

Settings

- Resolution selection
- Save location
- Custom hotkeys
- General settings UI

---

## Phase 5

Production readiness

- Error handling
- Crash recovery
- Performance optimization
- Long-duration recording tests
- Memory leak testing
- Final polishing

---

# Success Criteria

The application is considered complete when it:

- Runs as a single portable executable
- Requires no installation
- Starts in under 2 seconds
- Records the selected monitor at up to 1080p/60 FPS
- Captures both system audio and microphone audio, from user-selected devices
- Produces synchronized MP4 recordings
- Cleans up microphone noise without damaging speech or A/V sync
- Lets either audio source be muted mid-recording without a click in the output
- Supports customizable global hotkeys
- Exposes every user-facing behaviour in Settings, with nothing important hard-coded
- Minimizes to the system tray
- Uses minimal CPU and memory
- Handles sleep, lock, and shutdown events gracefully
- Recovers from unexpected interruptions where possible
- Provides a simple, reliable, distraction-free recording experience
````
