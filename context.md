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

---

## Frame Rate

Default:

60 FPS

Allow:

30 FPS

60 FPS

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

---

# Default Save Location

```
D:\Recordings\
```

The application remembers the last folder.

---

# File Naming

Format:

```
YYYY-MM-DD_HH-MM-SS.mp4
```

Example:

```
2026-08-07_09-35-20.mp4
```

---

# User Interface

Minimal interface.

Buttons:

- Start Recording
- Pause
- Stop
- Settings
- Exit

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

---

# System Tray

Closing the window should:

Minimize to tray

NOT exit.

Tray menu:

- Start Recording
- Pause
- Stop
- Open
- Exit

---

# Startup

Do NOT start with Windows.

---

# Recording History

No recording history.

The application simply saves recordings.

---

# Hotkeys

Global hotkeys.

Default:

Start

```
Ctrl + Shift + R
```

Pause / Resume

```
Ctrl + Shift + P
```

Stop

```
Ctrl + Shift + S
```

Requirements:

- Configurable
- Work globally
- Work while minimized

---

# Power Events

Automatically stop recording when:

- Windows sleeps
- Windows locks
- Windows shuts down

Gracefully finalize recording.

---

# Configuration File

Example:

```json
{
  "OutputFolder": "D:\\Recordings",
  "Resolution": "1080p",
  "FPS": 60,
  "Countdown": 3,
  "CaptureCursor": true,
  "RecordMicrophone": true,
  "RecordSystemAudio": true,
  "StartHotkey": "Ctrl+Shift+R",
  "PauseHotkey": "Ctrl+Shift+P",
  "StopHotkey": "Ctrl+Shift+S"
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
│     CountdownService.cs
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
- Captures both system audio and microphone audio
- Produces synchronized MP4 recordings
- Supports customizable global hotkeys
- Minimizes to the system tray
- Uses minimal CPU and memory
- Handles sleep, lock, and shutdown events gracefully
- Recovers from unexpected interruptions where possible
- Provides a simple, reliable, distraction-free recording experience
````
