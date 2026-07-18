using System.Diagnostics;
using Puck.Abstractions.Capture;
using Puck.Abstractions.Recording;
using Puck.Recording.Audio;
using Puck.Recording.Document;
using Puck.Recording.Matroska;
using Puck.Recording.Overlay;

namespace Puck.Recording.Session;

/// <summary>
/// The recording graph's session: it implements the engine's <see cref="ICaptureSink"/> frame tap and drives the
/// whole pipeline — a copy-and-enqueue on the render thread, one encode thread (overlay composite then the video
/// encoder then the muxer), and one audio thread (drain, mix, Opus, then the muxer). All muxer calls are serialized
/// through one lock; the render thread never blocks (a full queue drops the newest frame and counts it). Every
/// buffer is rented at start-up, so steady state does not allocate.
/// </summary>
/// <remarks>
/// Build one through <see cref="TryCreate"/>: it resolves the document's codec ladder and audio rows against the
/// supplied platform factories, opening only what this machine can encode and capture, and reports a loud reason
/// when the ladder or a device declines. Wire the returned sink into the engine's capturing render node; call
/// <see cref="Stop"/> (or dispose) to finalize the container.
/// </remarks>
public sealed class RecordingSession : ICaptureSink, IAudioPacketSink {
    private const int AudioBitrateBitsPerSecond = 160_000;
    private const long EngineTicksPerSecond = 50_400L;
    private const long NanosecondsPerSecond = 1_000_000_000L;

    /// <summary>Attempts to create a recording session for a document against the platform's capture factories.</summary>
    /// <param name="options">The document, factories, and source resolution.</param>
    /// <param name="session">The created session on success; otherwise <see langword="null"/>.</param>
    /// <param name="reason">A human-readable note on what landed and what declined (empty on a clean full-fat start).</param>
    /// <returns><see langword="true"/> when at least one lane (video or audio) is recording.</returns>
    public static bool TryCreate(RecordingSessionOptions options, out RecordingSession? session, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: options);

        session = null;

        if (!RecordingDocumentValidator.TryValidate(document: options.Document, reason: out reason)) {
            return false;
        }

        var notes = new List<string>();
        var document = options.Document;
        var videoEncoder = ResolveVideoEncoder(options: options, notes: notes);
        var audioLane = ResolveAudioLane(options: options, notes: notes);

        if ((videoEncoder is null) && (audioLane is null)) {
            reason = $"nothing to record: {string.Join(separator: "; ", values: notes)}";

            return false;
        }

        var docTypeWebm = ((videoEncoder is not null) && string.Equals(a: videoEncoder.CodecId, b: "V_AV1", comparisonType: StringComparison.Ordinal));
        var outputPath = ResolveOutputPath(options: options, hasVideo: (videoEncoder is not null), webm: docTypeWebm);

        _ = Directory.CreateDirectory(path: (Path.GetDirectoryName(path: outputPath) ?? "."));

        var output = new FileStream(path: outputPath, mode: FileMode.Create, access: FileAccess.ReadWrite, share: FileShare.Read);
        var muxer = new MatroskaMuxer(output: output, webmDocType: docTypeWebm);

        if (audioLane is not null) {
            foreach (var encoder in audioLane.Encoders) {
                encoder.TrackNumber = muxer.AddAudioTrack(
                    channelCount: 2,
                    codecDelayNanoseconds: encoder.CodecDelayNanoseconds,
                    codecId: "A_OPUS",
                    codecPrivate: encoder.CodecPrivate,
                    samplingFrequency: 48000.0,
                    seekPreRollNanoseconds: OpusAudioLane.SeekPreRoll
                );
            }
        }

        session = new RecordingSession(
            audioLane: audioLane,
            codecLanded: (videoEncoder?.CodecId ?? "(audio only)"),
            document: document,
            muxer: muxer,
            options: options,
            output: output,
            outputPath: outputPath,
            videoEncoder: videoEncoder
        );
        reason = string.Join(separator: "; ", values: notes);

        return true;
    }

    private static IVideoEncoder? ResolveVideoEncoder(RecordingSessionOptions options, List<string> notes) {
        if ((options.VideoEncoderFactory is null) || (options.Document.Video is not { } video)) {
            return null;
        }

        var width = (video.Width ?? options.SourceWidth);
        var height = (video.Height ?? options.SourceHeight);
        var encoder = options.VideoEncoderFactory.Create(
            bitrateKilobitsPerSecond: video.BitrateKbps,
            codecLadder: (video.CodecLadder ?? ["av1", "h264"]),
            frameRate: video.FrameRate,
            height: height,
            reason: out var reason,
            width: width
        );

        if (encoder is null) {
            notes.Add(item: $"video declined ({reason})");
        }

        return encoder;
    }

    private static OpusAudioLane? ResolveAudioLane(RecordingSessionOptions options, List<string> notes) {
        if ((options.AudioSourceFactory is null) || (options.Document.Audio is not { Count: > 0 } rows)) {
            return null;
        }

        var bindings = new List<AudioLaneSource>();

        foreach (var row in rows) {
            var source = ((row.Kind == RecordingAudioKind.Loopback)
                ? options.AudioSourceFactory.CreateLoopback(reason: out var reason)
                : options.AudioSourceFactory.CreateMicrophone(deviceId: row.Device, reason: out reason));

            if (source is null) {
                notes.Add(item: $"audio '{row.Id}' declined ({reason})");

                continue;
            }

            bindings.Add(item: new AudioLaneSource(Gain: row.Gain, Isolated: (row.Track == RecordingAudioTrackMode.Isolated), Source: source));
        }

        return ((bindings.Count == 0)
            ? null
            : new OpusAudioLane(bitrateBitsPerSecond: AudioBitrateBitsPerSecond, sources: bindings));
    }

    private static string ResolveOutputPath(RecordingSessionOptions options, bool hasVideo, bool webm) {
        if (!string.IsNullOrWhiteSpace(value: options.Document.Output)) {
            return options.Document.Output!;
        }

        var extension = ((hasVideo && webm)
            ? ".webm"
            : ".mkv");

        return Path.Combine(path1: options.OutputDirectory, path2: (options.FileNamePrefix + extension));
    }

    private readonly OpusAudioLane? m_audioLane;
    private readonly Thread? m_audioThread;
    private byte[] m_av1Scratch = [];
    private readonly OverlayCompositor m_compositor;
    private bool m_disposed;
    private readonly Thread? m_encodeThread;
    private readonly double m_epochNanosecondsPerStopwatchTick;
    private readonly long m_epochStopwatch;
    private long m_firstTick;
    private readonly AutoResetEvent m_frameAvailable = new(initialState: false);
    private long m_framesCaptured;
    private long m_framesDropped;
    private readonly FrameSlotQueue? m_frameQueue;
    private bool m_haveFirstTick;
    private readonly MatroskaMuxer m_muxer;
    private volatile bool m_muxerStarted;
    private readonly object m_muxSync = new();
    private readonly FileStream m_output;
    private readonly bool m_stripAv1Td;
    private volatile bool m_running = true;
    private readonly RecordingClock m_clock;
    private readonly IVideoEncoder? m_videoEncoder;
    private int m_videoHeight;
    private bool m_videoTrackRegistered;
    private int m_videoTrackNumber = -1;
    private int m_videoWidth;

    /// <summary>Gets the resolved output file path.</summary>
    public string OutputPath { get; }

    /// <summary>Gets the codec id that landed for the video lane (or an audio-only marker).</summary>
    public string CodecLanded { get; }

    private RecordingSession(
        OpusAudioLane? audioLane,
        string codecLanded,
        RecordingDocument document,
        MatroskaMuxer muxer,
        RecordingSessionOptions options,
        FileStream output,
        string outputPath,
        IVideoEncoder? videoEncoder
    ) {
        m_audioLane = audioLane;
        m_clock = document.Clock;
        m_compositor = new OverlayCompositor(overlays: (document.Overlays ?? []));
        m_epochStopwatch = Stopwatch.GetTimestamp();
        m_epochNanosecondsPerStopwatchTick = ((double)NanosecondsPerSecond / Stopwatch.Frequency);
        m_muxer = muxer;
        m_output = output;
        m_videoEncoder = videoEncoder;
        // The MF AV1 MFT emits a leading temporal-delimiter OBU per frame; strip it from the block payload for a
        // clean AV1-in-WebM stream (see Av1TemporalDelimiterFilter). H.264 payloads pass through untouched.
        m_stripAv1Td = ((videoEncoder is not null) && string.Equals(a: videoEncoder.CodecId, b: "V_AV1", comparisonType: StringComparison.Ordinal));
        m_videoWidth = (document.Video?.Width ?? options.SourceWidth);
        m_videoHeight = (document.Video?.Height ?? options.SourceHeight);
        CodecLanded = codecLanded;
        OutputPath = outputPath;

        if (videoEncoder is not null) {
            m_frameQueue = new FrameSlotQueue(
                capacity: options.EncodeQueueCapacity,
                slotBytes: checked(options.SourceWidth * options.SourceHeight * 4)
            );
        } else {
            // Audio only: no frames arrive, so the muxer can start immediately (the OpusHead is already known).
            m_muxer.Start();
            m_muxerStarted = true;
        }

        if (audioLane is not null) {
            m_audioThread = new Thread(start: RunAudio) {
                IsBackground = true,
                Name = "puck-recording-audio",
            };
        }

        if (videoEncoder is not null) {
            m_encodeThread = new Thread(start: RunEncode) {
                IsBackground = true,
                Name = "puck-recording-encode",
            };
        }

        audioLane?.Start();
        m_audioThread?.Start();
        m_encodeThread?.Start();
    }

    /// <inheritdoc/>
    public void Consume(in CaptureFrame frame) {
        if (!m_running || (m_frameQueue is null)) {
            return;
        }

        var surface = frame.Surface;

        if (!surface.IsCpuPixels) {
            return;
        }

        var pixels = surface.Pixels.Span;
        var slot = m_frameQueue.TryAcquire();

        if ((slot is null) || (pixels.Length > slot.Pixels.Length)) {
            _ = Interlocked.Increment(location: ref m_framesDropped);

            return;
        }

        var (sessionNs, simNs) = ComputeTimestamps(frame: in frame);

        pixels.CopyTo(destination: slot.Pixels);
        slot.Format = surface.Format;
        slot.Height = (int)surface.Height;
        slot.Length = pixels.Length;
        slot.SessionTimeNanoseconds = sessionNs;
        slot.SimTimeNanoseconds = simNs;
        slot.TimestampNanoseconds = ((m_clock == RecordingClock.Sim)
            ? simNs
            : sessionNs);
        slot.Width = (int)surface.Width;
        m_frameQueue.Publish();
        _ = m_frameAvailable.Set();
    }

    /// <inheritdoc/>
    public void WriteAudioPacket(int trackNumber, ReadOnlySpan<byte> data, long timestampNanoseconds) {
        lock (m_muxSync) {
            m_muxer.WriteBlock(data: data, isKeyframe: true, timestampNanoseconds: timestampNanoseconds, trackNumber: trackNumber);
        }
    }

    /// <summary>Takes an honest snapshot of the session's progress for a status echo.</summary>
    /// <returns>The current counters.</returns>
    public RecordingStatus Snapshot() {
        long bytesWritten;

        lock (m_muxSync) {
            bytesWritten = m_muxer.BytesWritten;
        }

        return new RecordingStatus(
            AudioSamplesDropped: (m_audioLane?.DroppedSampleCount ?? 0L),
            AudioTrackCount: (m_audioLane?.Encoders.Count ?? 0),
            BytesWritten: bytesWritten,
            CodecLanded: CodecLanded,
            FramesCaptured: Interlocked.Read(location: ref m_framesCaptured),
            FramesDropped: Interlocked.Read(location: ref m_framesDropped),
            OutputPath: OutputPath,
            VideoEnabled: (m_videoEncoder is not null)
        );
    }

    /// <summary>Stops capture, finalizes the container (final cluster, cues, patched duration), and releases resources.</summary>
    public void Stop() {
        if (!m_running) {
            return;
        }

        m_running = false;
        _ = m_frameAvailable.Set();
        m_encodeThread?.Join();
        m_audioThread?.Join();
        m_audioLane?.Stop();

        lock (m_muxSync) {
            m_muxer.Stop();
        }

        m_muxer.Dispose();
        m_output.Dispose();
        m_videoEncoder?.Dispose();
        m_audioLane?.Dispose();
        m_frameAvailable.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        Stop();
        m_disposed = true;
    }

    private (long SessionNanoseconds, long SimNanoseconds) ComputeTimestamps(in CaptureFrame frame) {
        var sessionNs = (long)((Stopwatch.GetTimestamp() - m_epochStopwatch) * m_epochNanosecondsPerStopwatchTick);

        if (!m_haveFirstTick) {
            m_haveFirstTick = true;
            m_firstTick = (long)frame.TimestampTicks;
        }

        var tickDelta = ((long)frame.TimestampTicks - m_firstTick);
        var simNs = ((tickDelta * NanosecondsPerSecond) / EngineTicksPerSecond);

        return (sessionNs, simNs);
    }

    private void RunEncode() {
        while (true) {
            var slot = m_frameQueue!.TryTake();

            if (slot is not null) {
                ProcessSlot(slot: slot);
                m_frameQueue.Release();

                continue;
            }

            if (!m_running) {
                break;
            }

            _ = m_frameAvailable.WaitOne(millisecondsTimeout: 5);
        }

        lock (m_muxSync) {
            if (m_videoTrackRegistered) {
                foreach (var packet in m_videoEncoder!.Drain()) {
                    WriteVideoBlock(packet: packet);
                }
            }
        }
    }

    // Writes one video packet, stripping AV1 temporal-delimiter OBUs first (H.264 passes through). Called only on the
    // encode thread under m_muxSync, so the reused scratch buffer needs no synchronization of its own.
    private void WriteVideoBlock(RecordedPacket packet) {
        var data = packet.Data.Span;

        if (m_stripAv1Td) {
            var stripped = Av1TemporalDelimiterFilter.Strip(temporalUnit: data, destination: ref m_av1Scratch);

            if (stripped >= 0) {
                data = m_av1Scratch.AsSpan(start: 0, length: stripped);
            }
        }

        m_muxer.WriteBlock(data: data, isKeyframe: packet.IsKeyframe, timestampNanoseconds: packet.TimestampNanoseconds, trackNumber: m_videoTrackNumber);
    }

    private void ProcessSlot(FrameSlotQueue.Slot slot) {
        var pixels = slot.Pixels.AsSpan(start: 0, length: slot.Length);

        if (m_compositor.HasOverlays) {
            m_compositor.Composite(
                format: slot.Format,
                height: slot.Height,
                pixels: pixels,
                sessionTimeNanoseconds: slot.SessionTimeNanoseconds,
                simTimeNanoseconds: slot.SimTimeNanoseconds,
                width: slot.Width
            );
        }

        var packets = m_videoEncoder!.EncodeFrame(
            format: slot.Format,
            height: slot.Height,
            pixels: pixels,
            timestampNanoseconds: slot.TimestampNanoseconds,
            width: slot.Width
        );

        lock (m_muxSync) {
            foreach (var packet in packets) {
                if (!m_videoTrackRegistered) {
                    RegisterVideoTrack();
                }

                WriteVideoBlock(packet: packet);
            }
        }

        _ = Interlocked.Increment(location: ref m_framesCaptured);
    }

    private void RegisterVideoTrack() {
        m_videoTrackNumber = m_muxer.AddVideoTrack(
            codecId: m_videoEncoder!.CodecId,
            codecPrivate: m_videoEncoder.CodecPrivate,
            height: m_videoHeight,
            width: m_videoWidth
        );
        m_muxer.Start();
        m_videoTrackRegistered = true;
        m_muxerStarted = true;
    }

    private void RunAudio() {
        while (m_running) {
            if (!m_muxerStarted) {
                Thread.Sleep(millisecondsTimeout: 2);

                continue;
            }

            m_audioLane!.Pump(sink: this);
            Thread.Sleep(millisecondsTimeout: 10);
        }

        if (m_muxerStarted) {
            m_audioLane!.Flush(sink: this);
        }
    }
}
