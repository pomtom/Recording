using System.Globalization;

namespace Recorder.Encoding;

/// <summary>
/// Builds the ffmpeg command line for a recording and for the final remux.
/// </summary>
/// <remarks>
/// During capture the output is a <em>fragmented</em> MP4
/// (<c>frag_keyframe+empty_moov+default_base_is_moof</c>). A normal MP4 keeps its index in a moov
/// atom written at the very end, so a killed process leaves an unplayable file; a fragmented one
/// is self-describing as it goes, which is exactly what makes crash recovery possible. The cost is
/// a slightly larger file and no faststart, both of which the stream-copy remux in
/// <see cref="Mp4Finalizer"/> undoes in about a second.
/// </remarks>
public static class FFmpegArgumentBuilder
{
    public static List<string> BuildRecordArguments(EncodeSpec spec, VideoEncoder encoder)
    {
        var args = new List<string>
        {
            "-y", "-hide_banner", "-nostdin",
            "-loglevel", "warning",
        };

        // --- video input ---
        // thread_queue_size keeps ffmpeg from dropping packets when the two inputs arrive unevenly.
        args.AddRange(["-thread_queue_size", "1024"]);
        args.AddRange(["-f", "rawvideo"]);
        args.AddRange(["-pixel_format", spec.PixelFormat == FramePixelFormat.Nv12 ? "nv12" : "bgra"]);
        args.AddRange(["-video_size", $"{spec.Width}x{spec.Height}"]);
        args.AddRange(["-framerate", spec.Fps.ToString(CultureInfo.InvariantCulture)]);
        args.AddRange(["-i", PipePath(spec.VideoPipeName)]);

        // --- audio input ---
        args.AddRange(["-thread_queue_size", "1024"]);
        args.AddRange(["-f", "s16le"]);
        args.AddRange(["-ar", spec.AudioSampleRate.ToString(CultureInfo.InvariantCulture)]);
        args.AddRange(["-ac", spec.AudioChannels.ToString(CultureInfo.InvariantCulture)]);
        args.AddRange(["-i", PipePath(spec.AudioPipeName)]);

        args.AddRange(["-map", "0:v:0"]);
        args.AddRange(["-map", "1:a:0"]);

        // --- video encoding ---
        // Only the fallback capture path needs this: when the GPU could not scale, ffmpeg does.
        if (spec.NeedsScaleFilter)
            args.AddRange(["-vf", $"scale={spec.FinalWidth}:{spec.FinalHeight}:flags=bicubic"]);

        args.AddRange(["-c:v", EncoderProbe.EncoderName(encoder)]);
        args.AddRange(EncoderSpecificArguments(spec, encoder));

        // Only pin the pixel format on the BGRA fallback path, where 4:2:0 has to be forced.
        // NV12 input is already 4:2:0, and each encoder picks its own preferred layout for it
        // (nv12 for the hardware ones, yuv420p for libx264) — the H.264 bitstream is equivalent
        // either way, and forcing yuv420p here just provokes a warning and a pointless conversion.
        if (spec.PixelFormat == FramePixelFormat.Bgra)
            args.AddRange(["-pix_fmt", "yuv420p"]);

        // Tag the colour metadata explicitly. The GPU path converts RGB to limited-range BT.709,
        // and without these tags players are left to guess, which shows up as washed-out or
        // over-saturated playback.
        args.AddRange(["-colorspace", "bt709"]);
        args.AddRange(["-color_primaries", "bt709"]);
        args.AddRange(["-color_trc", "bt709"]);
        args.AddRange(["-color_range", "tv"]);
        args.AddRange(["-g", (spec.Fps * 2).ToString(CultureInfo.InvariantCulture)]);
        args.AddRange(["-bf", "0"]);                     // no B-frames: lower latency, simpler seeking

        // --- audio encoding ---
        args.AddRange(["-c:a", "aac"]);
        args.AddRange(["-b:a", $"{spec.AudioBitrateKbps}k"]);
        args.AddRange(["-ar", spec.AudioSampleRate.ToString(CultureInfo.InvariantCulture)]);
        args.AddRange(["-ac", spec.AudioChannels.ToString(CultureInfo.InvariantCulture)]);

        // --- muxing ---
        // The flag is default_base_moof — not default_base_is_moof, which the mov muxer rejects
        // outright and takes the whole movflags value down with it.
        args.AddRange(["-movflags", "+frag_keyframe+empty_moov+default_base_moof"]);
        args.AddRange(["-f", "mp4"]);
        args.Add(spec.PartPath);

        return args;
    }

    /// <summary>Stream-copy remux from the fragmented capture file to a clean faststart MP4.</summary>
    public static List<string> BuildRemuxArguments(string partPath, string finalPath) =>
    [
        "-y", "-hide_banner", "-nostdin", "-loglevel", "warning",
        "-i", partPath,
        "-c", "copy",
        "-movflags", "+faststart",
        "-f", "mp4",
        finalPath,
    ];

    private static IEnumerable<string> EncoderSpecificArguments(EncodeSpec spec, VideoEncoder encoder)
    {
        var bitrate = spec.VideoBitrate;

        return encoder switch
        {
            // p4 is NVENC's balanced preset; VBR with a quality target keeps static screens cheap
            // while still allowing the bitrate to spike during full-screen changes.
            VideoEncoder.Nvenc =>
            [
                "-preset", "p4",
                "-tune", "hq",
                "-rc", "vbr",
                "-cq", "23",
                "-b:v", "0",
                "-maxrate", bitrate.ToString(CultureInfo.InvariantCulture),
                "-bufsize", (bitrate * 2).ToString(CultureInfo.InvariantCulture),
            ],

            // global_quality alone selects Quick Sync's ICQ mode; adding a maxrate on top pushes it
            // back to plain CQP and the quality target is ignored.
            VideoEncoder.QuickSync =>
            [
                "-preset", "medium",
                "-global_quality", "23",
            ],

            VideoEncoder.Amf =>
            [
                "-quality", "balanced",
                "-rc", "cqp",
                "-qp_i", "22",
                "-qp_p", "24",
            ],

            // veryfast keeps a software fallback usable at 1080p60 on a mid-range CPU.
            _ =>
            [
                "-preset", "veryfast",
                "-crf", "21",
                "-threads", "0",
            ],
        };
    }

    public static string PipePath(string pipeName) => $@"\\.\pipe\{pipeName}";
}
