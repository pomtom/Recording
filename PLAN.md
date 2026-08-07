# Pomtom Recorder — Implementation Plan

## Context

`C:\pomtom\Recorder` currently contains only `context.md`, a specification for a lightweight, portable Windows screen recorder. There is no code, no solution, no git repo. This plan builds the whole application from zero.

The goal is a **single portable .exe** that records a monitor at up to 1440p/60 FPS with system audio + microphone mixed into one track, writes MP4 (H.264/AAC), runs from the tray, responds to global hotkeys, and survives sleep/lock/shutdown and crashes. **No tests are to be written** — the deliverable is a working app.

Decisions confirmed with the user:

| Decision | Choice |
|---|---|
| Packaging | Self-contained single-file exe (no .NET install needed on target) |
| FFmpeg | Embedded as a resource inside the exe, extracted to `%LOCALAPPDATA%` on first run |
| Multi-monitor | Per-monitor selection (WGC has no virtual-desktop capture item) |
| Save folder | `D:\Recordings` default, auto-fallback to `%USERPROFILE%\Videos\Recordings` when unavailable |

Environment verified: .NET SDK 8.0.423 present, NuGet + ffmpeg download hosts reachable, Windows 11 build 26200, NVIDIA RTX 2050 + Intel Arc (NVENC and QSV both available), single 1536×960 logical display, **no D: drive on this machine** — hence the fallback requirement.

---

## Architecture

### The one idea that makes it reliable

Both the video pacer and the audio mixer derive their output position from **the same monotonic recording clock** (a single `Stopwatch` minus accumulated paused time).

- Video: frame *n* is written when `clock.Elapsed >= n / fps`. The last captured frame is re-sent if nothing changed on screen.
- Audio: exactly `elapsed × 48000` samples are written; short sources are padded with silence.

Because frame count and sample count are both functions of the same clock, A/V cannot drift over multi-hour recordings, and pause/resume is just "stop advancing the clock." This is the core design constraint every module below serves.

### Pipeline

```
WGC FramePool (free-threaded)
   └─> ID3D11VideoProcessor: scale + BGRA→NV12 on GPU
         └─> staging texture → Map → latest-frame buffer  (double buffered)
               └─> PacerThread @ fps ──> named pipe ─┐
                                                      ├─> ffmpeg.exe ──> .part.mp4 (fragmented)
WASAPI loopback ─┐                                    │                       │
                 ├─> resample 48k/2ch ─> ring buffers │                   remux -c copy
WASAPI mic ──────┘         └─> MixerThread @10ms ────┘                  +faststart
                                (sum, clip, s16le)                            ▼
                                                                     YYYY-MM-DD_HH-MM-SS.mp4
```

Writing to a **fragmented MP4** (`+frag_keyframe+empty_moov+default_base_is_moof`) during capture means the file is playable even if the process is killed — that is what makes crash recovery possible. On a clean stop it is remuxed stream-copy to a normal faststart MP4 in about a second.

---

## Project layout

```
C:\pomtom\Recorder\
├── Recorder.sln
├── build.ps1                      # restore → publish single-file → open output
├── tools\fetch-ffmpeg.ps1         # one-time download of ffmpeg.exe into assets\
├── README.md
├── .gitignore
└── src\Recorder\
    ├── Recorder.csproj
    ├── app.manifest               # PerMonitorV2 DPI, asInvoker (no admin)
    ├── App.xaml / App.xaml.cs     # composition root, single-instance mutex
    ├── assets\ffmpeg.exe          # embedded resource (gitignored, fetched by script)
    ├── assets\app.ico
    ├── Core\
    │   ├── RecorderState.cs           # Idle | CountingDown | Recording | Paused | Finalizing
    │   ├── RecordingManager.cs        # state machine + orchestration, single public API
    │   ├── RecordingSession.cs        # one recording's lifetime: pipes, threads, clock
    │   └── RecordingClock.cs          # Stopwatch + paused-time accumulator
    ├── Capture\
    │   ├── MonitorEnumerator.cs       # EnumDisplayMonitors → id, name, physical bounds
    │   ├── Direct3DInterop.cs         # WinRT⇄D3D11 boilerplate
    │   ├── CaptureItemInterop.cs      # IGraphicsCaptureItemInterop (HMONITOR → item)
    │   ├── ScreenCaptureService.cs    # WGC session, cursor toggle, border suppression
    │   ├── FrameConverter.cs          # VideoProcessor scale+NV12, CPU BGRA fallback
    │   ├── AudioCaptureService.cs     # WASAPI loopback + mic, resample to 48k/2ch
    │   ├── AudioMixer.cs              # clock-driven mix with silence padding
    │   └── CountdownService.cs        # 3 → 2 → 1 → Recording…
    ├── Encoding\
    │   ├── FFmpegProvisioner.cs       # extract embedded ffmpeg, hash-verify, resolve
    │   ├── EncoderProbe.cs            # nvenc/qsv/amf/libx264 detection + cache
    │   ├── FFmpegArgumentBuilder.cs   # per-encoder args, bitrate table
    │   ├── FFmpegEncoder.cs           # process lifecycle, named pipes, stderr drain
    │   └── Mp4Finalizer.cs            # remux fragmented → faststart MP4
    ├── Hotkeys\
    │   ├── HotkeyGesture.cs           # "Ctrl+Shift+R" ⇄ modifiers/vkey, capture from UI
    │   └── GlobalHotkeyManager.cs     # RegisterHotKey on a message-only window
    ├── Overlay\
    │   ├── RecordingOverlayWindow.xaml   # 🔴 REC 00:12:36, draggable, excluded
    │   └── CountdownWindow.xaml
    ├── Tray\TrayManager.cs            # NotifyIcon + context menu + balloon tips
    ├── Power\PowerEventMonitor.cs     # suspend / lock / session-ending
    ├── Recovery\
    │   ├── RecordingJournal.cs        # pending\{id}.json sidecar
    │   └── CrashRecoveryService.cs    # startup scan + salvage
    ├── Settings\
    │   ├── AppSettings.cs
    │   └── SettingsManager.cs         # load/save/validate/migrate
    ├── UI\
    │   ├── MainWindow.xaml            # Start / Pause / Stop / Settings / Exit
    │   └── SettingsWindow.xaml
    └── Utils\
        ├── NativeMethods.cs
        ├── CaptureExclusion.cs        # SetWindowDisplayAffinity WDA_EXCLUDEFROMCAPTURE
        ├── PathUtils.cs               # output folder resolution + fallback
        └── Log.cs                     # Serilog, warnings+errors only
```

`.csproj` essentials:

```xml
<TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
<UseWPF>true</UseWPF>
<UseWindowsForms>true</UseWindowsForms>   <!-- NotifyIcon + SystemEvents only -->
<Nullable>enable</Nullable>
<ApplicationManifest>app.manifest</ApplicationManifest>
<EmbeddedResource Include="assets\ffmpeg.exe" />
```

Publish profile (in `build.ps1`): `-c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:InvariantGlobalization=true`.

Packages: `NAudio 2.3.0`, `Vortice.Direct3D11 3.8.3`, `Vortice.DXGI 3.8.3`, `Serilog 4.4.0`, `Serilog.Sinks.File 7.0.0`. WinRT projections (`Windows.Graphics.Capture`) come free from the Windows-versioned TFM — no extra package.

---

## Build order

### Phase 1 — Foundation

1. `Recorder.sln` + `src\Recorder\Recorder.csproj` with the settings above.
2. `app.manifest`: `PerMonitorV2` DPI awareness (essential — the capture path works in **physical** pixels; without this the monitor bounds and overlay placement are wrong on scaled displays), `asInvoker`, `supportedOS` Win10/11.
3. `AppSettings.cs` — exactly the JSON shape in the spec, plus `MonitorDeviceId` and `AudioBitrateKbps`:
   ```json
   { "OutputFolder": "D:\\Recordings", "Resolution": "1080p", "FPS": 60, "Countdown": 3,
     "CaptureCursor": true, "RecordMicrophone": true, "RecordSystemAudio": true,
     "MonitorDeviceId": null, "StartHotkey": "Ctrl+Shift+R",
     "PauseHotkey": "Ctrl+Shift+P", "StopHotkey": "Ctrl+Shift+S" }
   ```
4. `SettingsManager` — **portable-first resolution**: if `settings.json` sits next to the exe, use it; otherwise `%LOCALAPPDATA%\PomtomRecorder\settings.json`. Atomic write (temp + `File.Replace`). Unknown/invalid values fall back to defaults rather than throwing.
5. `PathUtils.ResolveOutputFolder()` — try configured folder, create if missing, write-probe it; on any failure fall back to `%USERPROFILE%\Videos\Recordings`, persist the change, and log a warning. This is what makes the spec's `D:\Recordings` default safe on machines without a D: drive.
6. `Log.cs` — Serilog rolling file at `%LOCALAPPDATA%\PomtomRecorder\logs\recorder-.log`, minimum level **Warning**, 7-day retention. No analytics, no telemetry.
7. `App.xaml.cs` — single-instance `Mutex`; on second launch, signal the first instance to show its window and exit. Global `DispatcherUnhandledException` / `AppDomain.UnhandledException` handlers that log, try to salvage an in-flight recording, and show a friendly message.
8. `TrayManager` — WinForms `NotifyIcon`, context menu (Start / Pause / Stop / Open / Exit) with items enabled per `RecorderState`. Window close → `e.Cancel = true` + hide to tray. Exit only via tray/menu Exit, and if a recording is active it finalizes first.

### Phase 2 — Encoding backend (built before capture so the pipeline has a sink)

9. `tools\fetch-ffmpeg.ps1` — downloads `ffmpeg-release-essentials.zip` from gyan.dev, extracts **only** `bin\ffmpeg.exe` into `src\Recorder\assets\`, prints its SHA-256. Run once; `assets\ffmpeg.exe` is gitignored.
10. `FFmpegProvisioner` — resolve order: (a) `ffmpeg.exe` next to the exe, (b) `%LOCALAPPDATA%\PomtomRecorder\bin\ffmpeg.exe` if its SHA-256 matches the embedded copy, (c) extract the embedded resource there, (d) `ffmpeg` on PATH. Extraction is once-ever and takes well under a second, so it does not affect steady-state startup.
11. `EncoderProbe` — run `ffmpeg -hide_banner -encoders` once, cache the result in `%LOCALAPPDATA%`, and rank: `h264_nvenc` → `h264_qsv` → `h264_amf` → `libx264`. Availability in the list is necessary but not sufficient (a driver can still refuse), so `FFmpegEncoder` treats an ffmpeg exit within the first ~2 seconds as an encoder failure and **retries with the next candidate down the list**, ending at `libx264` which always works.
12. `FFmpegArgumentBuilder`:
    ```
    -y -hide_banner -nostdin -loglevel warning
    -thread_queue_size 1024 -f rawvideo -pixel_format nv12 -video_size {W}x{H} -framerate {fps} -i \\.\pipe\pomrec-v-{id}
    -thread_queue_size 1024 -f s16le -ar 48000 -ac 2 -i \\.\pipe\pomrec-a-{id}
    -map 0:v -map 1:a
    -c:v {encoder} {encoderArgs} -pix_fmt yuv420p -g {fps*2}
    -c:a aac -b:a 192k
    -movflags +frag_keyframe+empty_moov+default_base_is_moof -f mp4 "{path}.part.mp4"
    ```
    Encoder args: nvenc `-preset p4 -tune hq -rc vbr -cq 23 -b:v 0 -maxrate {br} -bufsize {2×br}`; qsv `-preset medium -global_quality 23`; amf `-quality balanced -rc cqp -qp_i 22 -qp_p 24`; libx264 `-preset veryfast -crf 21`. Bitrate ceiling by height: 720p 8 Mbps, 1080p 14, 1440p 26, 2160p 45; ×0.7 at 30 FPS.
13. `FFmpegEncoder` — creates two `NamedPipeServerStream`s (async, 4 MB buffers), starts ffmpeg, waits for both clients to connect, and continuously drains stderr into the log (**never skip this** — a full stderr buffer deadlocks the child process). Exposes `WriteVideoFrame(ReadOnlySpan<byte>)`, `WriteAudio(ReadOnlySpan<byte>)`, and `CompleteAsync()` which closes pipes and waits for a clean exit with a timeout.
14. `Mp4Finalizer` — `ffmpeg -y -i "{part}" -c copy -movflags +faststart "{final}"`, then delete the `.part`. If remux fails for any reason, rename the `.part` to the final name instead — a fragmented MP4 still plays everywhere, so the user never loses a recording.

### Phase 3 — Capture

15. `MonitorEnumerator` — `EnumDisplayMonitors` + `GetMonitorInfoW` + `EnumDisplayDevicesW` for a friendly name; returns HMONITOR, stable device id, physical bounds, primary flag.
16. `Direct3DInterop` + `CaptureItemInterop` — the standard WinRT bridge: `D3D11CreateDevice` (BgraSupport | VideoSupport) via Vortice, `CreateDirect3D11DeviceFromDXGIDevice` to get `IDirect3DDevice`; `IGraphicsCaptureItemInterop.CreateForMonitor(hmon)`; `IDirect3DDxgiInterfaceAccess.GetInterface` to get `ID3D11Texture2D` out of each frame.
17. `ScreenCaptureService` — guard on `GraphicsCaptureSession.IsSupported()`; `Direct3D11CaptureFramePool.CreateFreeThreaded(device, B8G8R8A8UIntNormalized, 2, size)` so frames arrive off the UI thread; set `IsCursorCaptureEnabled` from settings and `IsBorderRequired = false` inside a `try/catch` (only present on Win11 22000+). Handle `FramePool.Recreate` on resolution change and `GraphicsCaptureItem.Closed` (monitor unplugged) by stopping gracefully.
18. `FrameConverter` — **primary path**: `ID3D11VideoProcessor` `VideoProcessorBlt` does scale *and* BGRA→NV12 in one GPU pass, then `CopyResource` into an NV12 staging texture, `Map`, and copy into one of two alternating managed buffers. NV12 is 1.5 bytes/px versus BGRA's 4, so a 1080p60 stream through the pipe drops from ~500 MB/s to ~186 MB/s — this is the main CPU win. **Fallback**: if the video device or processor can't be created, copy BGRA to staging and let ffmpeg do `-vf scale` with `-pixel_format bgra`; the encoder args switch accordingly.
    Target size: `720p`/`1080p`/`1440p` set the height and derive width from source aspect (rounded to even); `Native` uses source size; never upscale beyond source.
19. `AudioCaptureService` — `WasapiLoopbackCapture` (system) and `WasapiCapture` (default mic), each resampled to **48 kHz / 2ch / float32** and pushed into its own ring buffer. Either source can be disabled by settings or absent from the machine; a missing/failing mic logs a warning and records system-only rather than aborting.
20. `AudioMixer` — a thread that every 10 ms computes how many samples *should* exist by now from `RecordingClock`, pulls that many from each ring, **pads silence for whatever is short** (WASAPI loopback can go quiet when nothing is playing — this padding is what prevents audio from running short and desyncing over hours), sums with soft clipping to [-1, 1], converts to `s16le`, and writes to the audio pipe.
21. `RecordingClock` — `Stopwatch` plus accumulated paused duration; `Elapsed` excludes paused time. The video pacer thread and the mixer both read it, which is the whole sync mechanism.
22. `CountdownService` + `CountdownWindow` — centred, borderless, transparent, topmost, click-through (`WS_EX_TRANSPARENT`), showing 3 → 2 → 1 → "Recording…". Capture-excluded. Skipped when `Countdown == 0`.

### Phase 4 — Orchestration and controls

23. `RecordingManager` — the single public surface (`StartAsync`, `PauseResume`, `StopAsync`, `State`, `Elapsed`, `StateChanged`). Enforces legal transitions under a lock so hotkey, tray, UI, and power events can't race. Start sequence: resolve output folder → build filename `YYYY-MM-DD_HH-MM-SS.mp4` → write journal → start ffmpeg → connect pipes → start capture and audio → countdown → start clock, pacer, mixer.
    - **Pacer thread**: for frame `n`, wait until `clock.Elapsed >= n/fps` (spin-wait for the last ~1 ms after a coarse sleep, with `timeBeginPeriod(1)` for the session), then write the current latest-frame buffer. Duplicates on no-change, so output is exact CFR.
    - **Pause**: stop the clock, stop the pacer and mixer from emitting, and drain-and-discard incoming audio. Nothing is written for paused time, so the resumed recording is seamless.
24. `GlobalHotkeyManager` — a message-only `HwndSource`; `RegisterHotKey`/`UnregisterHotKey` with `MOD_NOREPEAT`. Re-registers when settings change. A conflict (another app owns the combo) surfaces as a tray balloon naming the failed hotkey rather than a silent no-op.
25. `RecordingOverlayWindow` — `WindowStyle=None`, `AllowsTransparency=true`, `Topmost=true`, `ShowInTaskbar=false`, showing `🔴 REC 00:12:36` (a red dot that dims while paused). Draggable via `DragMove`, position persisted. **Capture-excluded** via `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)` — applied to the overlay, countdown, main, and settings windows so none of them appear in the recording.
26. `MainWindow` — Start / Pause / Stop / Settings / Exit, elapsed time, current state, resolution+encoder line. Buttons enable/disable off `RecorderState`.

### Phase 5 — Settings UI, power, recovery

27. `SettingsWindow` — monitor dropdown, resolution, FPS (30/60), countdown seconds, output folder + browse, cursor / mic / system-audio checkboxes, and three hotkey capture boxes (`PreviewKeyDown` builds a `HotkeyGesture`, validating that a modifier is present). Save applies live: re-register hotkeys, persist, and if a recording is in progress, defer resolution/FPS changes to the next recording rather than disrupting the current one.
28. `PowerEventMonitor` — `SystemEvents.PowerModeChanged` (`Suspend`), `SystemEvents.SessionSwitch` (`SessionLock`, `SessionLogoff`), `SystemEvents.SessionEnding`, plus WPF `Application.SessionEnding`. Each triggers a graceful `StopAsync`; `SessionEnding` blocks briefly so the MP4 gets finalized before Windows terminates the process.
29. `RecordingJournal` + `CrashRecoveryService` — at session start write `%LOCALAPPDATA%\PomtomRecorder\pending\{id}.json` holding the part path, final path, start time, and PID; delete it on clean finalize. On startup, scan `pending\`; for each entry whose PID is dead, run the remux to recover the MP4 and show a tray balloon ("Recovered an interrupted recording"). Unrecoverable entries are logged and their journals removed so they don't nag forever.
30. Error handling throughout: any recording failure logs, attempts to finalize whatever was written, shows one plain-language message, and returns to `Idle` — never a stuck state, never a stack trace in the user's face.
31. `README.md` — build, publish, hotkeys, settings reference, where logs and recovered files live.

---

## Known behaviours worth stating up front

- **One monitor per recording.** Windows Graphics Capture has no virtual-desktop capture item; this is an OS constraint, not an implementation shortcut. Monitor is selectable in Settings.
- **First launch is slower.** A self-contained single-file exe self-extracts once (~1 s) and extracts ffmpeg once. Subsequent launches hit the spec's < 2 s target comfortably.
- **The exe is large** (~110–150 MB) because it carries the .NET runtime, WPF, and ffmpeg. That is the cost of "copy one file to a USB stick and it runs anywhere."
- **Loopback captures what the default output device plays.** Switching output devices mid-recording is not followed; that would need device-change re-initialisation and is out of V1 scope.

The architecture leaves clear seams for the spec's future items: `FrameConverter` is where a webcam composite or region crop would go, `AudioMixer` already has per-source rings for multi-track output, and `ScreenCaptureService` takes a `GraphicsCaptureItem` so window-capture is a constructor change rather than a redesign.

---

## Verification

No automated tests (per your instruction). Manual end-to-end checks after implementation:

1. **Build**: `dotnet build -c Release` → clean. Then `.\build.ps1` → single `Recorder.exe` in `publish\`.
2. **Cold start**: run from `publish\`, time to visible window on the second launch (< 2 s). Check idle working set in Task Manager (< 150 MB).
3. **Short recording**: press `Ctrl+Shift+R`, confirm the 3-2-1 countdown, play audio and speak into the mic for ~30 s, press `Ctrl+Shift+S`. Confirm `D:\Recordings` fallback landed the file in `%USERPROFILE%\Videos\Recordings\YYYY-MM-DD_HH-MM-SS.mp4`.
4. **Inspect the output** with the extracted ffmpeg (`%LOCALAPPDATA%\PomtomRecorder\bin\ffmpeg.exe -i <file>`): H.264 + AAC, correct resolution, ~60 fps, duration matching the overlay timer within a frame. Play it back — verify both audio sources are audible and lip-sync is right at the end of the file, not just the start.
5. **Overlay exclusion**: confirm the REC overlay and countdown are absent from the recorded video.
6. **Pause/resume**: record 10 s → pause 10 s → resume 10 s → stop. File is ~20 s with no gap, no glitch, and audio still in sync after the resume.
7. **Hotkeys while minimized**: close to tray, drive a full start/pause/stop cycle from hotkeys only, then repeat from the tray menu.
8. **Long run**: a 60+ minute recording. Check A/V sync at the very end, that memory is flat in Task Manager, and CPU is low (expect NVENC to be selected — confirm in the log or the main window's encoder line).
9. **Power events**: start recording, `Win+L`, unlock → recording stopped and the MP4 finalized and playable.
10. **Crash recovery**: start recording, kill `Recorder.exe` from Task Manager, relaunch → balloon reports recovery and the salvaged MP4 plays.
11. **Settings**: change to 720p/30 and a different output folder, record, confirm the new resolution and location; rebind a hotkey and confirm the new combo works and the old one doesn't.
