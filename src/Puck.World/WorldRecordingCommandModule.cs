using Microsoft.Extensions.Options;
using Puck.Abstractions.Recording;
using Puck.Abstractions.Windowing;
using Puck.Commands;
using Puck.Platform.Recording;
using Puck.Recording.Session;

namespace Puck.World;

/// <summary>
/// The recording console surface — <c>capture.start</c> / <c>capture.stop</c> / <c>capture.status</c>, the native-capture
/// control plane over the pipe. It resolves the boot recording document (<c>puck.recording.v1</c>, host-scope data) and
/// the platform's Media Foundation encoder ladder + WASAPI audio sources against real hardware, opening only what this
/// machine can encode and capture, and arms the render tap. Every verb is Immediate (no simulation effect) and echoes
/// honestly: the codec that landed, frames captured/dropped, audio track count and drops, bytes, and the output path;
/// declines are loud. A SEPARATE module to keep each class under its analyzer ceilings.
/// </summary>
/// <remarks>The live windowed present path hands GPU surfaces, so the capture tap reads each captured frame back to CPU
/// pixels through the SDF engine before encoding — a synchronous GPU readback per captured frame on the render thread.
/// It costs nothing until <c>capture.start</c> arms it; the frame counters and the <c>world.fps</c> verb reveal the live
/// impact (no cost figure is claimed without measurement).</remarks>
internal sealed class WorldRecordingCommandModule(
    RecordingTap tap,
    IVideoEncoderFactory videoEncoders,
    IAudioCaptureSourceFactory audioSources,
    RecordingSessionClock clock,
    RecordingDocumentSource source,
    IOptions<NativeWindowOptions> window
) : ICommandModule {
    private readonly RecordingTap m_tap = tap;
    private readonly IVideoEncoderFactory m_videoEncoders = videoEncoders;
    private readonly IAudioCaptureSourceFactory m_audioSources = audioSources;
    private readonly RecordingSessionClock m_clock = clock;
    private readonly RecordingDocumentSource m_source = source;
    private readonly IOptions<NativeWindowOptions> m_window = window;

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithTrailingArgs(
            name: "capture.start",
            description: "Starts a native recording (Immediate): loads the boot recording document (or the given output path), resolves the AV1->H.264 encoder ladder and mic+loopback audio against this machine, arms the render tap, and echoes the codec that landed and any declines.",
            handler: (_, args) => Start(args: args)
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "capture.stop",
            description: "Stops the active recording (Immediate): drains and finalizes the container (final cluster, cues, patched duration) and echoes the output path, landed codec, frames captured/dropped, audio drops, and byte size.",
            handler: (_, args) => Stop(args: args)
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "capture.status",
            description: "Reports the recording state (Immediate): running/idle, the codec landed, frames captured/dropped, audio tracks and drops, bytes written, the output path, and the source document.",
            handler: (_, args) => Status(args: args)
        );
    }

    private CommandResult Start(string[] args) {
        if (args.Length > 1) {
            return Error(text: "[capture.start: expected at most one argument (an output path)]");
        }

        if (m_tap.WantsFrames) {
            return Error(text: $"[capture.start: already recording -> {m_tap.Current?.OutputPath}]");
        }

        var document = m_source.Document;

        if ((args.Length == 1) && !string.IsNullOrWhiteSpace(value: args[0])) {
            document = document with { Output = args[0] };
        }

        // B's coordination point: re-anchor the shared audio clock so the WASAPI sources stamp from the same instant the
        // session's own video epoch is captured (the frozen factory has no per-session hook).
        m_clock.ResetEpochToNow();

        var created = RecordingSession.TryCreate(
            options: new RecordingSessionOptions {
                AudioSourceFactory = m_audioSources,
                Document = document,
                SourceHeight = (int)m_window.Value.Height,
                SourceWidth = (int)m_window.Value.Width,
                VideoEncoderFactory = m_videoEncoders,
            },
            session: out var session,
            reason: out var reason
        );

        if (!created || (session is null)) {
            return Error(text: $"[capture.start: declined — {reason}]");
        }

        m_tap.Arm(session: session);

        var notes = (string.IsNullOrWhiteSpace(value: reason) ? "full" : reason);
        var status = session.Snapshot();

        return new CommandResult(Output: $"[capture.start: recording -> {session.OutputPath} | codec {session.CodecLanded} | audio tracks {status.AudioTrackCount} | {notes}]");
    }

    private CommandResult Stop(string[] args) {
        if (args.Length > 0) {
            return Error(text: "[capture.stop: expected no arguments]");
        }

        var session = m_tap.Disarm();

        if (session is null) {
            return Error(text: "[capture.stop: not recording]");
        }

        // Snapshot the counters before finalizing (Stop disposes the muxer's stream, which the byte count reads); the
        // final byte size is then taken from the closed file on disk.
        var status = session.Snapshot();

        session.Stop();

        var bytes = TryFileLength(path: session.OutputPath);

        return new CommandResult(Output: $"[capture.stop: wrote {session.OutputPath} | codec {status.CodecLanded} | frames {status.FramesCaptured}/{status.FramesDropped} dropped | audio drops {status.AudioSamplesDropped} | bytes {bytes}]");
    }

    private CommandResult Status(string[] args) {
        if (args.Length > 0) {
            return Error(text: "[capture.status: expected no arguments]");
        }

        var origin = (m_source.SourcePath ?? "baked default");

        if (m_tap.Current is not { } session) {
            return new CommandResult(Output: $"[capture.status: idle | document {origin} | readback synchronous GPU readback per captured frame while recording]");
        }

        var status = session.Snapshot();

        return new CommandResult(Output: $"[capture.status: recording -> {status.OutputPath} | codec {status.CodecLanded} | frames {status.FramesCaptured}/{status.FramesDropped} dropped | " +
            $"audio tracks {status.AudioTrackCount} drops {status.AudioSamplesDropped} | bytes {status.BytesWritten} | document {origin} | readback synchronous per captured frame]");
    }

    private static long TryFileLength(string path) {
        try {
            return new FileInfo(fileName: path).Length;
        } catch (Exception) {
            return 0L;
        }
    }

    private static CommandResult Error(string text) => new(Output: text) { IsError = true };
}
