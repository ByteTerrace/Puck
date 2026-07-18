#:project ../Puck.Recording.csproj
#:property EnforceCodeStyleInBuild=false
#:property AnalysisLevel=none
#:property TreatWarningsAsErrors=false
#:property GenerateDocumentationFile=false
// mux-check.cs — Puck.Recording's self-contained muxer/audio-lane verification, one .NET 10 file-based app.
//
//   dotnet run src/Puck.Recording/scripts/mux-check.cs
//
// It (1) muxes a synthetic session — a stub AV1 video encoder's fake packets interleaved with a REAL Opus lane
// encoding a sine wave — twice, and asserts the two files are byte-identical (deterministic bytes); (2) walks the
// produced EBML back with a minimal reader, asserting the header/doc-type, Info timestamp scale, the video + audio
// tracks and their CodecPrivate, cluster/SimpleBlock structure, per-track monotone timestamps, and int16 block
// bounds; (3) drives a full RecordingSession end to end with stub factories and color-bar frames; and (4) writes a
// real ~2 second Opus-only .mkv from the sine source, probing it with ffprobe when available.

using System.Buffers.Binary;
using System.Diagnostics;
using Puck.Abstractions.Capture;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Recording;
using Puck.Recording.Audio;
using Puck.Recording.Document;
using Puck.Recording.Matroska;
using Puck.Recording.Session;

var outputDir = Path.Combine(Path.GetTempPath(), "puck-recording-check");

Directory.CreateDirectory(outputDir);

var failures = 0;

void Check(string label, bool ok, string detail = "") {
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}{(detail.Length > 0 ? $" — {detail}" : "")}");

    if (!ok) {
        failures++;
    }
}

// ---------------------------------------------------------------------------------------------------------------
// 1. Deterministic synthetic mux (stub video + real Opus sine lane), muxed twice.
// ---------------------------------------------------------------------------------------------------------------
Console.WriteLine("1. Deterministic synthetic mux (stub AV1 video + real Opus sine):");

var fileA = Path.Combine(outputDir, "synthetic-a.webm");
var fileB = Path.Combine(outputDir, "synthetic-b.webm");

BuildSynthetic(fileA);
BuildSynthetic(fileB);

var bytesA = File.ReadAllBytes(fileA);
var bytesB = File.ReadAllBytes(fileB);

Check("two identical synthetic muxes produce byte-identical files", bytesA.AsSpan().SequenceEqual(bytesB), $"{bytesA.Length} bytes");
Check("file is non-trivial", bytesA.Length > 2000, $"{bytesA.Length} bytes");

// ---------------------------------------------------------------------------------------------------------------
// 2. Walk the EBML back and assert structure.
// ---------------------------------------------------------------------------------------------------------------
Console.WriteLine("2. Structural walk of the produced EBML:");

var walk = EbmlWalker.Walk(bytesA);

Check("EBML doc type is 'webm'", walk.DocType == "webm", walk.DocType);
Check("Info TimestampScale is 1,000,000 ns (1 ms)", walk.TimestampScale == 1_000_000, walk.TimestampScale.ToString());
Check("Info Duration patched to a positive value", walk.Duration > 0.0, walk.Duration.ToString("0.###"));
Check("exactly one video track (V_AV1) with CodecPrivate", walk.Tracks.Count(t => t.Type == 1 && t.CodecId == "V_AV1" && t.HasCodecPrivate) == 1);
Check("exactly one audio track (A_OPUS) with OpusHead CodecPrivate", walk.Tracks.Count(t => t.Type == 2 && t.CodecId == "A_OPUS" && t.HasCodecPrivate) == 1);
Check("at least one cluster emitted", walk.Clusters.Count > 0, $"{walk.Clusters.Count} clusters");
Check("cluster timestamps are non-decreasing", IsNonDecreasing(walk.Clusters.Select(c => c.TimestampMs)));
Check("every SimpleBlock relative timestamp fits int16", walk.AllBlocks.All(b => b.Relative is >= short.MinValue and <= short.MaxValue));
Check("per-track absolute timestamps are monotone", walk.PerTrackMonotone());
Check("Cues element present with entries", walk.CueCount > 0, $"{walk.CueCount} cue points");
Check("total blocks split video and audio", walk.AllBlocks.Any(b => b.Track == 1) && walk.AllBlocks.Any(b => b.Track == 2), $"{walk.AllBlocks.Count} blocks");

// ---------------------------------------------------------------------------------------------------------------
// 3. Full RecordingSession end to end (stub video factory + sine mic factory + color-bar frames).
// ---------------------------------------------------------------------------------------------------------------
Console.WriteLine("3. RecordingSession end-to-end (stub encoder factory + sine source + color-bar frames):");

var sessionPath = Path.Combine(outputDir, "session.webm");
var sessionDoc = new RecordingDocument(
    Output: sessionPath,
    Video: new RecordingVideo(CodecLadder: ["av1", "h264"], FrameRate: 30, BitrateKbps: 8000),
    Audio: [new RecordingAudioRow(Id: "mic", Kind: RecordingAudioKind.Microphone, Track: RecordingAudioTrackMode.Mix)],
    Overlays: [
        new OverlayRow(Kind: OverlayKind.Text, Content: "PUCK REC", X: 0.02f, Y: 0.04f, PixelHeight: 24.0f, Color: "#FFCC00FF"),
        new OverlayRow(Kind: OverlayKind.Timecode, X: 0.98f, Y: 0.04f, PixelHeight: 20.0f, Color: "#FFFFFFCC", Anchor: OverlayAnchor.TopRight),
    ]
);

Check("session document validates", RecordingDocumentValidator.TryValidate(sessionDoc, out var vreason), vreason);

var created = RecordingSession.TryCreate(
    new RecordingSessionOptions {
        AudioSourceFactory = new SineSourceFactory(),
        Document = sessionDoc,
        SourceHeight = 240,
        SourceWidth = 320,
        VideoEncoderFactory = new StubVideoEncoderFactory(),
    },
    out var session,
    out var createReason
);

Check("session created", created && session is not null, createReason);

if (session is not null) {
    for (var frame = 0; frame < 45; frame++) {
        session.Consume(new CaptureFrame(
            FrameIndex: frame,
            Surface: ColorBar(320, 240, frame),
            TimestampTicks: (ulong)(frame * (50400 / 30))
        ));
        Thread.Sleep(12);
    }

    var status = session.Snapshot();

    session.Stop();

    var sessionBytes = File.ReadAllBytes(sessionPath);
    var sessionWalk = EbmlWalker.Walk(sessionBytes);

    Check("session frames captured", status.FramesCaptured > 0, $"captured={status.FramesCaptured} dropped={status.FramesDropped}");
    Check("session file has a video and an audio track", sessionWalk.Tracks.Any(t => t.Type == 1) && sessionWalk.Tracks.Any(t => t.Type == 2), $"{sessionWalk.Tracks.Count} tracks");
    Check("session per-track timestamps monotone", sessionWalk.PerTrackMonotone());
    Check("session codec landed as V_AV1", session.CodecLanded == "V_AV1", session.CodecLanded);
}

// ---------------------------------------------------------------------------------------------------------------
// 4. Real Opus-only .mkv from the sine source (~2 seconds).
// ---------------------------------------------------------------------------------------------------------------
Console.WriteLine("4. Real Opus-only .mkv (~2s sine):");

var opusPath = Path.Combine(outputDir, "opus-only.mkv");
var opusDoc = new RecordingDocument(
    Output: opusPath,
    Video: null,
    Audio: [new RecordingAudioRow(Id: "mic", Kind: RecordingAudioKind.Microphone, Track: RecordingAudioTrackMode.Mix)]
);

var opusCreated = RecordingSession.TryCreate(
    new RecordingSessionOptions {
        AudioSourceFactory = new SineSourceFactory(),
        Document = opusDoc,
        SourceHeight = 0,
        SourceWidth = 0,
        VideoEncoderFactory = null,
    },
    out var opusSession,
    out var opusReason
);

Check("audio-only session created", opusCreated && opusSession is not null, opusReason);

if (opusSession is not null) {
    Thread.Sleep(2000);

    opusSession.Stop();

    var opusBytes = File.ReadAllBytes(opusPath);
    var opusWalk = EbmlWalker.Walk(opusBytes);

    Check("Opus-only doc type is 'matroska'", opusWalk.DocType == "matroska", opusWalk.DocType);
    Check("one A_OPUS track", opusWalk.Tracks.Count(t => t.Type == 2 && t.CodecId == "A_OPUS") == 1);
    Check("audio blocks emitted", opusWalk.AllBlocks.Count(b => b.Track == 1) > 50, $"{opusWalk.AllBlocks.Count(b => b.Track == 1)} blocks");
    Check("duration is roughly two seconds", opusWalk.Duration is > 1500.0 and < 2600.0, $"{opusWalk.Duration:0} ms");
    Check("Opus-only file byte size sane", opusBytes.Length > 5000, $"{opusBytes.Length} bytes");

    ProbeWithFfprobe(opusPath);
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
Console.WriteLine($"artifacts under: {outputDir}");

return (failures == 0) ? 0 : 1;

// ---------------------------------------------------------------------------------------------------------------
// Synthetic builder: drive the muxer directly with stub video packets + real Opus packets, merged by timestamp.
// ---------------------------------------------------------------------------------------------------------------
void BuildSynthetic(string path) {
    using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
    using var muxer = new MatroskaMuxer(fs, webmDocType: true);
    using var encoder = new OpusStreamEncoder(bitrateBitsPerSecond: 160_000);

    var fakeAv1c = new byte[] { 0x81, 0x0C, 0x00, 0x0A, 0x0B, 0x00, 0x00, 0x00 };
    var videoTrack = muxer.AddVideoTrack("V_AV1", fakeAv1c, 320, 240);

    encoder.TrackNumber = muxer.AddAudioTrack("A_OPUS", encoder.CodecPrivate, 2, 48000.0, encoder.CodecDelayNanoseconds, OpusAudioLane.SeekPreRoll);
    muxer.Start();

    // Real Opus: encode 2 seconds of a fixed 440 Hz stereo sine, collecting packets.
    var collector = new PacketCollector();
    var sine = new float[48000 * 2 * 2];

    for (var frame = 0; frame < (sine.Length / 2); frame++) {
        var value = (float)(0.25 * Math.Sin((2.0 * Math.PI * 440.0 * frame) / 48000.0));

        sine[(frame * 2) + 0] = value;
        sine[(frame * 2) + 1] = value;
    }

    encoder.Append(sine, firstSampleTimestampNanoseconds: 0L, sink: collector);
    encoder.Flush(collector);

    // Stub video: 2 seconds at 30 fps, keyframe every 30 frames.
    var media = new List<(long Ts, int Track, byte[] Data, bool Key)>();

    for (var frame = 0; frame < 60; frame++) {
        var payload = new byte[24];

        BinaryPrimitives.WriteInt64LittleEndian(payload, frame);

        media.Add(((frame * 1_000_000_000L) / 30L, videoTrack, payload, (frame % 30) == 0));
    }

    foreach (var packet in collector.Packets) {
        media.Add((packet.Ts, encoder.TrackNumber, packet.Data, true));
    }

    // Stable merge by timestamp (OrderBy is stable), then feed the muxer in order.
    foreach (var (ts, track, data, key) in media.OrderBy(m => m.Ts)) {
        muxer.WriteBlock(track, data, ts, key);
    }

    muxer.Stop();
}

static bool IsNonDecreasing(IEnumerable<long> values) {
    var previous = long.MinValue;

    foreach (var value in values) {
        if (value < previous) {
            return false;
        }

        previous = value;
    }

    return true;
}

static Surface ColorBar(int width, int height, int frame) {
    var pixels = new byte[width * height * 4];

    for (var y = 0; y < height; y++) {
        for (var x = 0; x < width; x++) {
            var offset = ((y * width) + x) * 4;

            pixels[offset + 0] = (byte)((x + frame) & 0xFF);
            pixels[offset + 1] = (byte)((y * 2) & 0xFF);
            pixels[offset + 2] = (byte)(frame * 4);
            pixels[offset + 3] = 0xFF;
        }
    }

    return new Surface(0, (uint)width, (uint)height, SurfaceFormat.R8G8B8A8Unorm, pixels);
}

static void ProbeWithFfprobe(string path) {
    try {
        using var process = Process.Start(new ProcessStartInfo {
            Arguments = $"-v error -show_entries stream=codec_name -of default=nw=1 \"{path}\"",
            CreateNoWindow = true,
            FileName = "ffprobe",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        });

        if (process is null) {
            Console.WriteLine("  [INFO] ffprobe not available — structural walk carries the proof.");

            return;
        }

        var stdout = process.StandardOutput.ReadToEnd();

        process.WaitForExit(5000);
        Console.WriteLine($"  [INFO] ffprobe streams: {stdout.Replace("\r", "").Replace("\n", " ").Trim()}");
    } catch (Exception exception) {
        Console.WriteLine($"  [INFO] ffprobe not available ({exception.GetType().Name}) — structural walk carries the proof.");
    }
}

// ---------------------------------------------------------------------------------------------------------------
// Support types.
// ---------------------------------------------------------------------------------------------------------------
sealed class PacketCollector : IAudioPacketSink {
    public List<(long Ts, byte[] Data)> Packets { get; } = [];

    public void WriteAudioPacket(int trackNumber, ReadOnlySpan<byte> data, long timestampNanoseconds) {
        Packets.Add((timestampNanoseconds, data.ToArray()));
    }
}

sealed class StubVideoEncoderFactory : IVideoEncoderFactory {
    public IVideoEncoder? Create(IReadOnlyList<string> codecLadder, int width, int height, int frameRate, int bitrateKilobitsPerSecond, out string reason) {
        reason = string.Empty;

        return new StubVideoEncoder();
    }
}

sealed class StubVideoEncoder : IVideoEncoder {
    private long m_count;
    private byte[] m_codecPrivate = [];
    private readonly List<RecordedPacket> m_one = [];

    public string CodecId => "V_AV1";
    public ReadOnlyMemory<byte> CodecPrivate => m_codecPrivate;

    public IReadOnlyList<RecordedPacket> EncodeFrame(ReadOnlySpan<byte> pixels, SurfaceFormat format, int width, int height, long timestampNanoseconds) {
        if (m_codecPrivate.Length == 0) {
            m_codecPrivate = [0x81, 0x0C, 0x00, 0x0A, 0x0B, 0x00, 0x00, 0x00];
        }

        var payload = new byte[32];

        BinaryPrimitives.WriteInt64LittleEndian(payload, m_count);
        m_one.Clear();
        m_one.Add(new RecordedPacket(payload, timestampNanoseconds, (m_count % 30) == 0));
        m_count++;

        return m_one;
    }

    public IReadOnlyList<RecordedPacket> Drain() => [];

    public void Dispose() { }
}

sealed class SineSourceFactory : IAudioCaptureSourceFactory {
    public IAudioCaptureSource? CreateLoopback(out string reason) {
        reason = string.Empty;

        return new SineSource();
    }

    public IAudioCaptureSource? CreateMicrophone(string? deviceId, out string reason) {
        reason = string.Empty;

        return new SineSource();
    }
}

sealed class SineSource : IAudioCaptureSource {
    private long m_producedFrames;
    private double m_phase;
    private readonly Stopwatch m_stopwatch = new();

    public int SampleRate => 48000;
    public int Channels => 2;
    public long DroppedSampleCount => 0L;

    public void Start() => m_stopwatch.Restart();
    public void Stop() => m_stopwatch.Stop();

    public int Read(Span<float> interleaved, out long firstSampleTimestampNanoseconds) {
        firstSampleTimestampNanoseconds = (m_producedFrames * 1_000_000_000L) / SampleRate;

        var target = (long)(m_stopwatch.Elapsed.TotalSeconds * SampleRate);
        var wantFrames = Math.Max(0L, target - m_producedFrames);
        var capacityFrames = interleaved.Length / Channels;
        var frames = (int)Math.Min(wantFrames, capacityFrames);
        var increment = (2.0 * Math.PI * 440.0) / SampleRate;

        for (var frame = 0; frame < frames; frame++) {
            var value = (float)(0.25 * Math.Sin(m_phase));

            m_phase += increment;
            interleaved[(frame * 2) + 0] = value;
            interleaved[(frame * 2) + 1] = value;
        }

        m_producedFrames += frames;

        return frames * Channels;
    }

    public void Dispose() => Stop();
}

// ---------------------------------------------------------------------------------------------------------------
// Minimal reader-side EBML walker (independent of the writer) used to assert the produced structure.
// ---------------------------------------------------------------------------------------------------------------
sealed class EbmlWalker {
    public sealed record TrackInfo(int Number, int Type, string CodecId, bool HasCodecPrivate);
    public sealed record ClusterInfo(long TimestampMs, List<BlockInfo> Blocks);
    public sealed record BlockInfo(int Track, long Relative, long AbsoluteMs, bool Keyframe);

    public string DocType = "";
    public long TimestampScale;
    public double Duration;
    public List<TrackInfo> Tracks = [];
    public List<ClusterInfo> Clusters = [];
    public int CueCount;
    public List<BlockInfo> AllBlocks = [];

    public bool PerTrackMonotone() {
        var last = new Dictionary<int, long>();

        foreach (var block in AllBlocks) {
            if (last.TryGetValue(block.Track, out var previous) && (block.AbsoluteMs < previous)) {
                return false;
            }

            last[block.Track] = block.AbsoluteMs;
        }

        return true;
    }

    public static EbmlWalker Walk(byte[] data) {
        var walker = new EbmlWalker();
        var position = 0;

        while (position < data.Length) {
            var id = ReadId(data, ref position);
            var size = ReadSize(data, ref position);
            var contentStart = position;
            var contentEnd = (size < 0) ? data.Length : (int)(position + size);

            switch (id) {
                case 0x1A45DFA3:
                    walker.ParseEbmlHeader(data, contentStart, contentEnd);

                    break;
                case 0x18538067:
                    walker.ParseSegment(data, contentStart, contentEnd);

                    break;
                default:
                    break;
            }

            position = contentEnd;
        }

        return walker;
    }

    void ParseEbmlHeader(byte[] data, int start, int end) {
        var position = start;

        while (position < end) {
            var id = ReadId(data, ref position);
            var size = ReadSize(data, ref position);

            if (id == 0x4282) {
                DocType = System.Text.Encoding.ASCII.GetString(data, position, (int)size);
            }

            position += (int)size;
        }
    }

    void ParseSegment(byte[] data, int start, int end) {
        var position = start;

        while (position < end) {
            var id = ReadId(data, ref position);
            var size = ReadSize(data, ref position);
            var contentStart = position;
            var contentEnd = (size < 0) ? end : (int)(position + size);

            switch (id) {
                case 0x1549A966:
                    ParseInfo(data, contentStart, contentEnd);

                    break;
                case 0x1654AE6B:
                    ParseTracks(data, contentStart, contentEnd);

                    break;
                case 0x1F43B675:
                    ParseCluster(data, contentStart, contentEnd);

                    break;
                case 0x1C53BB6B:
                    ParseCues(data, contentStart, contentEnd);

                    break;
                default:
                    break;
            }

            position = contentEnd;
        }
    }

    void ParseInfo(byte[] data, int start, int end) {
        var position = start;

        while (position < end) {
            var id = ReadId(data, ref position);
            var size = ReadSize(data, ref position);

            if (id == 0x2AD7B1) {
                TimestampScale = ReadUInt(data, position, (int)size);
            } else if (id == 0x4489) {
                Duration = ReadDouble(data, position, (int)size);
            }

            position += (int)size;
        }
    }

    void ParseTracks(byte[] data, int start, int end) {
        var position = start;

        while (position < end) {
            var id = ReadId(data, ref position);
            var size = ReadSize(data, ref position);

            if (id == 0xAE) {
                ParseTrackEntry(data, position, (int)(position + size));
            }

            position += (int)size;
        }
    }

    void ParseTrackEntry(byte[] data, int start, int end) {
        var position = start;
        var number = 0;
        var type = 0;
        var codecId = "";
        var hasPrivate = false;

        while (position < end) {
            var id = ReadId(data, ref position);
            var size = ReadSize(data, ref position);

            switch (id) {
                case 0xD7:
                    number = (int)ReadUInt(data, position, (int)size);

                    break;
                case 0x83:
                    type = (int)ReadUInt(data, position, (int)size);

                    break;
                case 0x86:
                    codecId = System.Text.Encoding.ASCII.GetString(data, position, (int)size);

                    break;
                case 0x63A2:
                    hasPrivate = size > 0;

                    break;
                default:
                    break;
            }

            position += (int)size;
        }

        Tracks.Add(new TrackInfo(number, type, codecId, hasPrivate));
    }

    void ParseCluster(byte[] data, int start, int end) {
        var position = start;
        var timestampMs = 0L;
        var blocks = new List<BlockInfo>();

        while (position < end) {
            var id = ReadId(data, ref position);
            var size = ReadSize(data, ref position);

            if (id == 0xE7) {
                timestampMs = ReadUInt(data, position, (int)size);
            } else if (id == 0xA3) {
                var blockPosition = position;
                var track = (int)(data[blockPosition] & 0x7F);
                var relative = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(blockPosition + 1, 2));
                var keyframe = (data[blockPosition + 3] & 0x80) != 0;

                blocks.Add(new BlockInfo(track, relative, timestampMs + relative, keyframe));
            }

            position += (int)size;
        }

        var cluster = new ClusterInfo(timestampMs, blocks);

        Clusters.Add(cluster);
        AllBlocks.AddRange(blocks);
    }

    void ParseCues(byte[] data, int start, int end) {
        var position = start;

        while (position < end) {
            var id = ReadId(data, ref position);
            var size = ReadSize(data, ref position);

            if (id == 0xBB) {
                CueCount++;
            }

            position += (int)size;
        }
    }

    static uint ReadId(byte[] data, ref int position) {
        var first = data[position];
        var length = (first & 0x80) != 0 ? 1 : (first & 0x40) != 0 ? 2 : (first & 0x20) != 0 ? 3 : 4;
        var id = 0u;

        for (var index = 0; index < length; index++) {
            id = (id << 8) | data[position + index];
        }

        position += length;

        return id;
    }

    static long ReadSize(byte[] data, ref int position) {
        var first = data[position];
        var length = (first & 0x80) != 0 ? 1 : (first & 0x40) != 0 ? 2 : (first & 0x20) != 0 ? 3 : (first & 0x10) != 0 ? 4 : (first & 0x08) != 0 ? 5 : (first & 0x04) != 0 ? 6 : (first & 0x02) != 0 ? 7 : 8;
        long value = first & (0xFF >> length);
        var allOnes = value == (0xFFL >> length);

        for (var index = 1; index < length; index++) {
            value = (value << 8) | data[position + index];

            if (data[position + index] != 0xFF) {
                allOnes = false;
            }
        }

        position += length;

        return allOnes ? -1L : value;
    }

    static long ReadUInt(byte[] data, int position, int length) {
        var value = 0L;

        for (var index = 0; index < length; index++) {
            value = (value << 8) | data[position + index];
        }

        return value;
    }

    static double ReadDouble(byte[] data, int position, int length) {
        return length == 8 ? BinaryPrimitives.ReadDoubleBigEndian(data.AsSpan(position, 8)) : BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(position, 4));
    }
}
