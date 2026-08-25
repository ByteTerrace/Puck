using Microsoft.Extensions.Options;
using Puck.Abstractions.Capture;
using Puck.Abstractions.Recording;
using Puck.Abstractions.Windowing;
using Puck.Commands;
using Puck.Hosting;
using Puck.Platform.Recording;
using Puck.Recording.Session;

namespace Puck.World;

/// <summary>
/// The recording console surface — <c>capture.start</c> / <c>capture.stop</c> / <c>capture.status</c>, the native-capture
/// control plane over the pipe. It resolves the boot recording document (<c>puck.recording.v1</c>, host-scope data) and
/// the platform's Media Foundation encoder ladder + WASAPI audio sources against real hardware, opening only what this
/// machine can encode and capture, and arms launcher-level frame capture. Every verb is Immediate (no simulation effect) and echoes
/// honestly: the negotiated codec, frames captured/dropped, audio track count and drops, bytes, and the output path;
/// declines are loud. A separate module keeps each class under its analyzer ceilings.
/// </summary>
/// <remarks>The launcher captures the exact root surface immediately before presentation. GPU surfaces are read back
/// through the active presenter's backend capability — synchronously, once per due captured frame, on the render thread.
/// It costs nothing until <c>capture.start</c> arms the controller; the frame counters and the <c>world.fps</c> verb reveal
/// the live impact (no cost figure is claimed without measurement).</remarks>
internal sealed class WorldRecordingCommandModule(
    FrameCaptureController capture,
    IVideoEncoderFactory videoEncoders,
    IAudioCaptureSourceFactory audioSources,
    RecordingSessionClock clock,
    RecordingDocumentSource source,
    IOptions<NativeWindowOptions> window
) : ICommandModule {
    private readonly FrameCaptureController m_capture = capture;
    private readonly IVideoEncoderFactory m_videoEncoders = videoEncoders;
    private readonly IAudioCaptureSourceFactory m_audioSources = audioSources;
    private readonly RecordingSessionClock m_clock = clock;
    private readonly RecordingDocumentSource m_source = source;
    private readonly IOptions<NativeWindowOptions> m_window = window;

    private CommandResult Start(WireArgs args) {
        if (args.Count > 1) {
            return CommandResult.Error(output: "[capture.start: expected at most one argument (an output path)]");
        }

        if (m_capture.IsArmed) {
            return CommandResult.Error(output: $"[capture.start: already recording -> {(m_capture.CurrentSink as RecordingSession)?.OutputPath}]");
        }

        var document = m_source.Document;

        if (
            (args.Count == 1) &&
            !args[0].IsWhiteSpace()
        ) {
            document = document with { Output = args[0].ToString() };
        }

        // B's coordination point: re-anchor the shared audio clock so the WASAPI sources stamp from the same instant the
        // session's own video epoch is captured (the frozen factory has no per-session hook).
        m_clock.ResetEpochToNow();

        var created = RecordingSession.TryCreate(
            options: new RecordingSessionOptions {
                AudioSourceFactory = m_audioSources,
                Document = document,
                SourceHeight = ((int)m_window.Value.Height),
                SourceWidth = ((int)m_window.Value.Width),
                VideoEncoderFactory = m_videoEncoders,
            },
            session: out var session,
            reason: out var reason
        );

        if (
            !created ||
            (session is null)
        ) {
            return CommandResult.Error(output: $"[capture.start: declined — {reason}]");
        }

        m_capture.Arm(
            sink: session,
            options: new CaptureOptions {
                FrameRate = (document.Video?.FrameRate ?? 60),
                MaxFrames = 0,
            }
        );

        var notes = (string.IsNullOrWhiteSpace(value: reason)
            ? "full"
            : reason
        );
        var status = session.Snapshot();

        return new CommandResult(Output: $"[capture.start: recording -> {session.OutputPath} | codec {session.CodecLanded} | audio tracks {status.AudioTrackCount} | {notes}]");
    }
    private CommandResult Status(WireArgs args) {
        if (CommandResult.RequireNoArguments(args: args, verb: "capture.status") is { } refusal) {
            return refusal;
        }

        var origin = m_source.SourcePath;

        if (m_capture.CurrentSink is not RecordingSession session) {
            return new CommandResult(Output: $"[capture.status: idle | document {origin} | readback synchronous presenter readback per captured GPU frame while recording]");
        }

        var status = session.Snapshot();
        var fault = ((m_capture.Fault is { } exception)
            ? $" | capture fault {exception.Message}"
            : string.Empty
        );

        return new CommandResult(Output: ((string)$"[capture.status: recording -> {status.OutputPath} | codec {status.CodecLanded} | frames {status.FramesCaptured}/{status.FramesDropped} dropped | audio tracks {status.AudioTrackCount} drops {status.AudioSamplesDropped} | bytes {status.BytesWritten} | document {origin} | readback synchronous presenter readback per captured GPU frame{fault}]"));
    }
    private CommandResult Stop(WireArgs args) {
        if (CommandResult.RequireNoArguments(args: args, verb: "capture.stop") is { } refusal) {
            return refusal;
        }

        var session = (m_capture.Disarm() as RecordingSession);

        if (session is null) {
            return CommandResult.Error(output: "[capture.stop: not recording]");
        }

        // Snapshot the counters before finalizing (Stop disposes the muxer's stream, which the byte count reads); the
        // final byte size is then taken from the closed file on disk.
        var status = session.Snapshot();

        session.Stop();

        var bytes = TryFileLength(path: session.OutputPath);

        return new CommandResult(Output: $"[capture.stop: wrote {session.OutputPath} | codec {status.CodecLanded} | frames {status.FramesCaptured}/{status.FramesDropped} dropped | audio drops {status.AudioSamplesDropped} | bytes {bytes}]");
    }
    private static long TryFileLength(string path) {
        try {
            return new FileInfo(fileName: path).Length;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            return 0L;
        }
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "capture.start",
            description: "Starts a native recording (Immediate): loads the boot recording document (or the given output path), resolves the AV1->H.264 encoder ladder and mic+loopback audio against this machine, arms frame capture, and echoes the negotiated codec and any declines.",
            handler: (_, args) => Start(args: args)
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "capture.stop",
            description: "Stops the active recording (Immediate): drains and finalizes the container (final cluster, cues, patched duration) and echoes the output path, negotiated codec, frames captured/dropped, audio drops, and byte size.",
            handler: (_, args) => Stop(args: args)
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "capture.status",
            description: "Reports the recording state (Immediate): running/idle, the negotiated codec, frames captured/dropped, audio tracks and drops, bytes written, the output path, and the source document.",
            handler: (_, args) => Status(args: args)
        );
    }

}
