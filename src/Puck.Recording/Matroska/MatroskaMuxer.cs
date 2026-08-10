using System.Buffers.Binary;

namespace Puck.Recording.Matroska;

/// <summary>
/// A hand-rolled Matroska/WebM muxer: it writes an EBML stream directly (no library) with an invariant,
/// deterministic byte layout — the same declared tracks and the same sequence of timestamped blocks always
/// produce the same file. The segment is written unknown-size while live so a crash leaves the bytes up to the
/// last flushed cluster playable; the live file carries a <c>Void</c> reservation rather than an index containing
/// unresolved offsets. A clean <see cref="Stop"/> appends <c>Cues</c> and then replaces that reservation with the
/// completed <c>SeekHead</c>, patches the <c>Duration</c>, and bounds the segment.
/// </summary>
/// <remarks>
/// <para>Emit <c>webm</c> (the doc type) for an AV1 + Opus program and <c>matroska</c> for the H.264 fallback —
/// WebM is a Matroska subset, so this is one writer with a data-chosen doc-type string and file extension.</para>
/// <para>The muxer is a single-writer: declare every track, call <see cref="Start"/>, then feed
/// <see cref="WriteBlock"/> in roughly timestamp order, then <see cref="Stop"/>. It performs no locking; the
/// recording session serializes the encode and audio threads onto it.</para>
/// <para>The timestamp scale is one millisecond. Block timestamps are stored relative to their cluster as signed
/// 16-bit values; a new cluster opens on a video keyframe or after roughly two seconds, keeping every relative
/// timestamp inside the 16-bit range.</para>
/// <para><b>Seeking.</b> A trailing <c>Cues</c> element alone does not make a file seekable: a demuxer reaching
/// an unknown-size segment with nothing advertising the index builds its index incrementally from the clusters it
/// has already parsed, so a seek before the parse arrives lands back at the start. Three things are therefore
/// written together — fixed-size space reserved as <c>Void</c> at the head of the live segment and replaced by a
/// <c>SeekHead</c> carrying the <c>Info</c>, <c>Tracks</c>, and <c>Cues</c> positions on clean stop; the
/// <c>Cues</c> element itself, one <c>CuePoint</c> per
/// video keyframe (per cluster when there is no video track); and the segment's own size, written unknown while
/// live and patched to its final value on a clean stop so the trailing index is inside a bounded element.</para>
/// </remarks>
public sealed class MatroskaMuxer : IDisposable {
    private const long MaxClusterSpanMilliseconds = 2000L;
    private const long NanosecondsPerMillisecond = 1_000_000L;
    private const int SeekPositionWidth = 8;
    private const int SegmentSizeWidth = 8;

    // The top-level elements the completed SeekHead advertises, in the order their entries are written. Every one of
    // these identifiers is four bytes, so each reserved entry has the same size and the whole SeekHead is fixed.
    private static readonly uint[] SeekTargets = [MatroskaIds.Info, MatroskaIds.Tracks, MatroskaIds.Cues];

    private sealed class TrackDeclaration {
        public int ChannelCount { get; init; }
        public long CodecDelayNanoseconds { get; init; }
        public required string CodecId { get; init; }
        public required ReadOnlyMemory<byte> CodecPrivate { get; init; }
        public int Height { get; init; }
        public required int Number { get; init; }
        public double SamplingFrequency { get; init; }
        public long SeekPreRollNanoseconds { get; init; }
        public required byte Type { get; init; }
        public required ulong Uid { get; init; }
        public int Width { get; init; }
    }
    private readonly record struct CuePoint(long TimeMilliseconds, int Track, long ClusterPosition);
    private readonly record struct PendingCue(long TimeMilliseconds, int Track);

    private static void WriteInfo(Stream stream, out long durationOffset) {
        using var content = new MemoryStream();

        EbmlWriter.WriteUInt(stream: content, id: MatroskaIds.TimestampScale, value: NanosecondsPerMillisecond);
        EbmlWriter.WriteAsciiString(stream: content, id: MatroskaIds.MuxingApp, value: "puck");
        EbmlWriter.WriteAsciiString(stream: content, id: MatroskaIds.WritingApp, value: "puck");

        // The Duration element sits last with a placeholder; its file offset is captured so Stop can patch it.
        EbmlWriter.WriteId(stream: content, id: MatroskaIds.Duration);
        EbmlWriter.WriteSize(stream: content, size: 8);

        var durationContentOffsetWithinInfo = content.Position;

        content.Write(buffer: stackalloc byte[8]);

        var infoContent = content.ToArray();

        EbmlWriter.WriteMasterHeader(stream: stream, id: MatroskaIds.Info, contentSize: infoContent.Length);

        var durationFileOffset = (stream.Position + durationContentOffsetWithinInfo);

        stream.Write(buffer: infoContent);
        durationOffset = durationFileOffset;
    }
    private static void WriteTrackEntry(Stream stream, TrackDeclaration track) {
        using var content = new MemoryStream();

        EbmlWriter.WriteUInt(stream: content, id: MatroskaIds.TrackNumber, value: (ulong)track.Number);
        EbmlWriter.WriteUInt(stream: content, id: MatroskaIds.TrackUid, value: track.Uid);
        EbmlWriter.WriteUInt(stream: content, id: MatroskaIds.TrackType, value: track.Type);
        EbmlWriter.WriteUInt(stream: content, id: MatroskaIds.FlagLacing, value: 0);
        EbmlWriter.WriteAsciiString(stream: content, id: MatroskaIds.CodecId, value: track.CodecId);

        if (!track.CodecPrivate.IsEmpty) {
            EbmlWriter.WriteBinary(stream: content, id: MatroskaIds.CodecPrivate, value: track.CodecPrivate.Span);
        }

        if (track.Type == MatroskaIds.TrackTypeVideo) {
            using var video = new MemoryStream();

            EbmlWriter.WriteUInt(stream: video, id: MatroskaIds.PixelWidth, value: (ulong)track.Width);
            EbmlWriter.WriteUInt(stream: video, id: MatroskaIds.PixelHeight, value: (ulong)track.Height);

            // Colour: the encoder ladder (MF AV1/H.264) produces BT.709 limited-range NV12, so signal that in-band
            // rather than leaving a player to guess — an unsignalled stream renders washed-out or over-saturated.
            using (var colour = new MemoryStream()) {
                EbmlWriter.WriteUInt(stream: colour, id: MatroskaIds.ColourRange, value: MatroskaIds.ColourRangeLimited);
                EbmlWriter.WriteUInt(stream: colour, id: MatroskaIds.ColourMatrixCoefficients, value: MatroskaIds.ColourBt709);
                EbmlWriter.WriteUInt(stream: colour, id: MatroskaIds.ColourTransferCharacteristics, value: MatroskaIds.ColourBt709);
                EbmlWriter.WriteUInt(stream: colour, id: MatroskaIds.ColourPrimaries, value: MatroskaIds.ColourBt709);

                var colourContent = colour.ToArray();

                EbmlWriter.WriteMasterHeader(stream: video, id: MatroskaIds.Colour, contentSize: colourContent.Length);
                video.Write(buffer: colourContent);
            }

            var videoContent = video.ToArray();

            EbmlWriter.WriteMasterHeader(stream: content, id: MatroskaIds.Video, contentSize: videoContent.Length);
            content.Write(buffer: videoContent);
        } else {
            using var audio = new MemoryStream();

            EbmlWriter.WriteDouble(stream: audio, id: MatroskaIds.SamplingFrequency, value: track.SamplingFrequency);
            EbmlWriter.WriteUInt(stream: audio, id: MatroskaIds.Channels, value: (ulong)track.ChannelCount);

            var audioContent = audio.ToArray();

            EbmlWriter.WriteMasterHeader(stream: content, id: MatroskaIds.Audio, contentSize: audioContent.Length);
            content.Write(buffer: audioContent);

            if (track.CodecDelayNanoseconds > 0L) {
                EbmlWriter.WriteUInt(stream: content, id: MatroskaIds.CodecDelay, value: (ulong)track.CodecDelayNanoseconds);
            }

            if (track.SeekPreRollNanoseconds > 0L) {
                EbmlWriter.WriteUInt(stream: content, id: MatroskaIds.SeekPreRoll, value: (ulong)track.SeekPreRollNanoseconds);
            }
        }

        var entry = content.ToArray();

        EbmlWriter.WriteMasterHeader(stream: stream, id: MatroskaIds.TrackEntry, contentSize: entry.Length);
        stream.Write(buffer: entry);
    }

    private readonly MemoryStream m_clusterBuffer = new();
    private long m_clusterBaseMilliseconds;
    private bool m_clusterOpen;
    private readonly List<CuePoint> m_cues = [];
    private long m_cuesPosition = -1L;
    private bool m_disposed;
    private readonly bool m_docTypeWebm;
    private long m_durationOffset = -1L;
    private long m_firstTimestampNanoseconds = long.MinValue;
    private long m_infoPosition = -1L;
    private long m_maxEndMilliseconds;
    private readonly Stream m_output;
    private PendingCue? m_pendingCue;
    private long m_segmentDataStart;
    private int m_seekHeadLength;
    private long m_seekHeadOffset = -1L;
    private long m_segmentSizeOffset = -1L;
    private bool m_started;
    private bool m_stopped;
    private long m_tracksPosition = -1L;
    private readonly List<TrackDeclaration> m_tracks = [];
    private int m_videoTrackNumber = -1;

    /// <summary>Initializes a new instance of the <see cref="MatroskaMuxer"/> class over a seekable stream.</summary>
    /// <param name="output">The destination stream; it must be seekable so <see cref="Stop"/> can patch the duration.</param>
    /// <param name="webmDocType"><see langword="true"/> to write the <c>webm</c> doc type (AV1), otherwise <c>matroska</c> (H.264).</param>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is <see langword="null"/>.</exception>
    public MatroskaMuxer(Stream output, bool webmDocType) {
        ArgumentNullException.ThrowIfNull(argument: output);

        m_docTypeWebm = webmDocType;
        m_output = output;
    }

    /// <summary>Gets the number of bytes committed to the output so far.</summary>
    public long BytesWritten =>
        m_output.Length;

    /// <summary>Declares the video track. Call before <see cref="Start"/>; only one video track is supported.</summary>
    /// <param name="codecId">The Matroska codec id (<c>V_AV1</c> or <c>V_MPEG4/ISO/AVC</c>).</param>
    /// <param name="codecPrivate">The <c>CodecPrivate</c> configuration payload.</param>
    /// <param name="width">The coded width in pixels.</param>
    /// <param name="height">The coded height in pixels.</param>
    /// <returns>The assigned track number.</returns>
    public int AddVideoTrack(string codecId, ReadOnlyMemory<byte> codecPrivate, int width, int height) {
        ArgumentException.ThrowIfNullOrEmpty(argument: codecId);
        ThrowIfStarted();

        if (m_videoTrackNumber >= 0) {
            throw new InvalidOperationException(message: "A video track is already declared.");
        }

        var number = (m_tracks.Count + 1);

        m_tracks.Add(item: new TrackDeclaration {
            CodecId = codecId,
            CodecPrivate = codecPrivate,
            Height = height,
            Number = number,
            Type = MatroskaIds.TrackTypeVideo,
            Uid = (ulong)number,
            Width = width,
        });
        m_videoTrackNumber = number;

        return number;
    }

    /// <summary>Declares an audio track. Call before <see cref="Start"/>; the default topology mixes into one.</summary>
    /// <param name="codecId">The Matroska codec id (<c>A_OPUS</c>).</param>
    /// <param name="codecPrivate">The <c>CodecPrivate</c> payload (the <c>OpusHead</c> block).</param>
    /// <param name="channelCount">The channel count.</param>
    /// <param name="samplingFrequency">The sampling frequency in hertz.</param>
    /// <param name="codecDelayNanoseconds">The codec delay (Opus pre-skip) in nanoseconds, or zero.</param>
    /// <param name="seekPreRollNanoseconds">The seek pre-roll in nanoseconds, or zero.</param>
    /// <returns>The assigned track number.</returns>
    public int AddAudioTrack(string codecId, ReadOnlyMemory<byte> codecPrivate, int channelCount, double samplingFrequency, long codecDelayNanoseconds, long seekPreRollNanoseconds) {
        ArgumentException.ThrowIfNullOrEmpty(argument: codecId);
        ThrowIfStarted();

        var number = (m_tracks.Count + 1);

        m_tracks.Add(item: new TrackDeclaration {
            ChannelCount = channelCount,
            CodecDelayNanoseconds = codecDelayNanoseconds,
            CodecId = codecId,
            CodecPrivate = codecPrivate,
            Number = number,
            SamplingFrequency = samplingFrequency,
            SeekPreRollNanoseconds = seekPreRollNanoseconds,
            Type = MatroskaIds.TrackTypeAudio,
            Uid = (ulong)number,
        });

        return number;
    }

    /// <summary>Writes the EBML header, the unknown-size Segment, a Void reservation for the eventual SeekHead,
    /// Info, and Tracks. Call once, after declaring tracks.</summary>
    /// <exception cref="InvalidOperationException">No track was declared, or the muxer already started.</exception>
    public void Start() {
        ThrowIfStarted();

        if (m_tracks.Count == 0) {
            throw new InvalidOperationException(message: "At least one track must be declared before Start.");
        }

        WriteEbmlHeader();
        EbmlWriter.WriteId(stream: m_output, id: MatroskaIds.Segment);

        // Unknown size while live (a crash leaves every flushed cluster playable); Stop patches the final size into
        // these same eight bytes, which is what lets a demuxer treat the trailing Cues as part of a bounded segment.
        m_segmentSizeOffset = m_output.Position;

        EbmlWriter.WriteUnknownSize(stream: m_output);

        m_segmentDataStart = m_output.Position;

        ReserveSeekHead();

        m_infoPosition = (m_output.Position - m_segmentDataStart);

        WriteInfo(stream: m_output, durationOffset: out m_durationOffset);

        m_tracksPosition = (m_output.Position - m_segmentDataStart);

        using (var tracks = new MemoryStream()) {
            foreach (var track in m_tracks) {
                WriteTrackEntry(stream: tracks, track: track);
            }

            var content = tracks.ToArray();

            EbmlWriter.WriteMasterHeader(stream: m_output, id: MatroskaIds.Tracks, contentSize: content.Length);
            m_output.Write(buffer: content);
        }

        m_started = true;
    }

    /// <summary>Writes one media block, opening a new cluster on a video keyframe or after the cluster span cap.</summary>
    /// <param name="trackNumber">The destination track number.</param>
    /// <param name="data">The compressed frame payload.</param>
    /// <param name="timestampNanoseconds">The presentation timestamp in nanoseconds on the session clock (which clock
    /// that is, is the recording document's choice). The first block written defines zero and every later timestamp is
    /// stored relative to it, so the container's timeline is rebased even when the caller's clock is not.</param>
    /// <param name="isKeyframe">Whether a decoder can start at this block. It decides where a cluster may open and
    /// where a cue may point, so it must come from the bitstream, not from an encoder's optional report.</param>
    /// <exception cref="InvalidOperationException">The muxer has not started or has stopped.</exception>
    public void WriteBlock(int trackNumber, ReadOnlySpan<byte> data, long timestampNanoseconds, bool isKeyframe) {
        if (!m_started || m_stopped) {
            throw new InvalidOperationException(message: "WriteBlock requires a started, unstopped muxer.");
        }

        if (m_firstTimestampNanoseconds == long.MinValue) {
            m_firstTimestampNanoseconds = timestampNanoseconds;
        }

        var relativeNs = (timestampNanoseconds - m_firstTimestampNanoseconds);

        if (relativeNs < 0L) {
            relativeNs = 0L;
        }

        var blockMilliseconds = ((relativeNs + (NanosecondsPerMillisecond / 2L)) / NanosecondsPerMillisecond);
        var isVideo = (trackNumber == m_videoTrackNumber);
        var mustBreak =
            (!m_clusterOpen ||
            (isVideo && isKeyframe) ||
            ((blockMilliseconds - m_clusterBaseMilliseconds) >= MaxClusterSpanMilliseconds));

        if (mustBreak) {
            FlushCluster();
            OpenCluster(baseMilliseconds: blockMilliseconds);
        }

        // A cue is a promise that a decode can start here, so it names a video keyframe — cueing every cluster
        // regardless of its content points a seek at a mid-GOP block that no decoder can start on. With no video
        // track every block is a start point, so the cluster's first one carries the cue.
        var isCuePoint = ((m_videoTrackNumber < 0) || (isVideo && isKeyframe));

        if (isCuePoint && (m_pendingCue is null)) {
            m_pendingCue = new PendingCue(TimeMilliseconds: blockMilliseconds, Track: trackNumber);
        }

        var relative = (blockMilliseconds - m_clusterBaseMilliseconds);

        relative = Math.Clamp(value: relative, min: short.MinValue, max: short.MaxValue);

        var payloadLength = (((1 + 2) + 1) + data.Length);

        EbmlWriter.WriteId(stream: m_clusterBuffer, id: MatroskaIds.SimpleBlock);
        EbmlWriter.WriteSize(stream: m_clusterBuffer, size: payloadLength);

        Span<byte> prefix = stackalloc byte[4];

        prefix[0] = (byte)(0x80 | trackNumber);
        BinaryPrimitives.WriteInt16BigEndian(destination: prefix[1..3], value: (short)relative);
        prefix[3] = (byte)(isKeyframe
            ? 0x80
            : 0x00);
        m_clusterBuffer.Write(buffer: prefix);
        m_clusterBuffer.Write(buffer: data);

        var endMilliseconds = blockMilliseconds;

        if (endMilliseconds > m_maxEndMilliseconds) {
            m_maxEndMilliseconds = endMilliseconds;
        }
    }

    /// <summary>Flushes the final cluster and appends Cues, then patches the Info duration, replaces the reserved
    /// Void with SeekHead, and patches the segment size. Idempotent.</summary>
    public void Stop() {
        if (!m_started || m_stopped) {
            return;
        }

        FlushCluster();
        WriteCues();

        // Everything after this point rewinds to overwrite a reserved field, so the end of the stream is captured
        // once and restored last — BytesWritten and any later append stay honest.
        var end = m_output.Position;

        if (m_output.CanSeek) {
            PatchDuration();
            PatchSeekHead();
            PatchSegmentSize(segmentEnd: end);

            m_output.Position = end;
        }

        m_stopped = true;
        m_output.Flush();
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        Stop();
        m_clusterBuffer.Dispose();
        m_disposed = true;
    }

    private void OpenCluster(long baseMilliseconds) {
        m_clusterBuffer.SetLength(value: 0);
        EbmlWriter.WriteUInt(stream: m_clusterBuffer, id: MatroskaIds.Timestamp, value: (ulong)baseMilliseconds);

        m_clusterBaseMilliseconds = baseMilliseconds;
        m_clusterOpen = true;
        m_pendingCue = null;
    }
    private void FlushCluster() {
        if (!m_clusterOpen) {
            return;
        }

        var content = m_clusterBuffer.GetBuffer().AsSpan(start: 0, length: (int)m_clusterBuffer.Length);
        var clusterPosition = (m_output.Position - m_segmentDataStart);

        EbmlWriter.WriteMasterHeader(stream: m_output, id: MatroskaIds.Cluster, contentSize: content.Length);
        m_output.Write(buffer: content);

        // The cluster's position is only known now that it has landed, so the cue this cluster earned resolves here.
        if (m_pendingCue is { } pending) {
            m_cues.Add(item: new CuePoint(
                ClusterPosition: clusterPosition,
                TimeMilliseconds: pending.TimeMilliseconds,
                Track: pending.Track
            ));
            m_pendingCue = null;
        }

        m_clusterOpen = false;
    }
    private static byte[] BuildSeekHead(long infoPosition, long tracksPosition, long cuesPosition) {
        // Every entry uses the same fixed-width SeekPosition, so the completed SeekHead is exactly the same size
        // whether Cues exists or its entry is replaced by an equal-length Void.
        using var content = new MemoryStream();
        ReadOnlySpan<long> positions = [infoPosition, tracksPosition, cuesPosition];

        for (var index = 0; (index < SeekTargets.Length); index++) {
            var entry = BuildSeekEntry(target: SeekTargets[index], position: Math.Max(val1: 0L, val2: positions[index]));

            if (positions[index] < 0L) {
                WriteVoid(stream: content, totalLength: entry.Length);
            } else {
                content.Write(buffer: entry);
            }
        }

        var seekHeadContent = content.ToArray();
        using var seekHead = new MemoryStream();

        EbmlWriter.WriteMasterHeader(stream: seekHead, id: MatroskaIds.SeekHead, contentSize: seekHeadContent.Length);
        seekHead.Write(buffer: seekHeadContent);

        return seekHead.ToArray();
    }
    private static byte[] BuildSeekEntry(uint target, long position) {
        using var entryContent = new MemoryStream();
        Span<byte> targetId = stackalloc byte[4];
        Span<byte> encodedPosition = stackalloc byte[SeekPositionWidth];

        BinaryPrimitives.WriteUInt32BigEndian(destination: targetId, value: target);
        BinaryPrimitives.WriteUInt64BigEndian(destination: encodedPosition, value: (ulong)position);
        EbmlWriter.WriteBinary(stream: entryContent, id: MatroskaIds.SeekId, value: targetId);
        EbmlWriter.WriteBinary(stream: entryContent, id: MatroskaIds.SeekPosition, value: encodedPosition);

        var content = entryContent.ToArray();
        using var entry = new MemoryStream();

        EbmlWriter.WriteMasterHeader(stream: entry, id: MatroskaIds.Seek, contentSize: content.Length);
        entry.Write(buffer: content);

        return entry.ToArray();
    }
    private static void WriteVoid(Stream stream, int totalLength) {
        // One-byte Void id + one-byte fixed size; the SeekHead and each Seek entry are deliberately small enough for
        // that representation. Keeping the total exact is what lets clean Stop overwrite the reservation in place.
        var contentLength = (totalLength - 2);

        if ((contentLength < 0) || (contentLength >= 127)) {
            throw new InvalidOperationException(message: $"A fixed Matroska Void reservation of {totalLength} bytes cannot use the one-byte size form.");
        }

        EbmlWriter.WriteId(stream: stream, id: MatroskaIds.Void);
        EbmlWriter.WriteSizeFixed(stream: stream, size: contentLength, length: 1);

        Span<byte> padding = stackalloc byte[contentLength];

        padding.Clear();
        stream.Write(buffer: padding);
    }
    private void ReserveSeekHead() {
        // Do NOT publish Seek entries whose targets do not exist yet. A live/crash-recovered file must expose only
        // skippable padding here so a demuxer continues into Info/Tracks/Clusters instead of following placeholder
        // offsets. The all-present shape supplies the final byte count; clean Stop writes the real index once.
        var completedShape = BuildSeekHead(infoPosition: 0L, tracksPosition: 0L, cuesPosition: 0L);

        m_seekHeadOffset = m_output.Position;
        m_seekHeadLength = completedShape.Length;
        WriteVoid(stream: m_output, totalLength: m_seekHeadLength);
    }
    private void WriteCues() {
        if (m_cues.Count == 0) {
            return;
        }

        m_cuesPosition = (m_output.Position - m_segmentDataStart);

        using var content = new MemoryStream();

        foreach (var cue in m_cues) {
            using var positions = new MemoryStream();

            EbmlWriter.WriteUInt(stream: positions, id: MatroskaIds.CueTrack, value: (ulong)cue.Track);
            EbmlWriter.WriteUInt(stream: positions, id: MatroskaIds.CueClusterPosition, value: (ulong)cue.ClusterPosition);

            var positionsContent = positions.ToArray();

            using var point = new MemoryStream();

            EbmlWriter.WriteUInt(stream: point, id: MatroskaIds.CueTime, value: (ulong)cue.TimeMilliseconds);
            EbmlWriter.WriteMasterHeader(stream: point, id: MatroskaIds.CueTrackPositions, contentSize: positionsContent.Length);
            point.Write(buffer: positionsContent);

            var pointContent = point.ToArray();

            EbmlWriter.WriteMasterHeader(stream: content, id: MatroskaIds.CuePoint, contentSize: pointContent.Length);
            content.Write(buffer: pointContent);
        }

        var cuesContent = content.ToArray();

        EbmlWriter.WriteMasterHeader(stream: m_output, id: MatroskaIds.Cues, contentSize: cuesContent.Length);
        m_output.Write(buffer: cuesContent);
    }
    private void PatchDuration() {
        if (m_durationOffset < 0L) {
            return;
        }

        m_output.Position = m_durationOffset;

        Span<byte> buffer = stackalloc byte[8];

        BinaryPrimitives.WriteDoubleBigEndian(destination: buffer, value: m_maxEndMilliseconds);
        m_output.Write(buffer: buffer);
    }
    private void PatchSeekHead() {
        if (m_seekHeadOffset < 0L) {
            return;
        }

        var completed = BuildSeekHead(infoPosition: m_infoPosition, tracksPosition: m_tracksPosition, cuesPosition: m_cuesPosition);

        if (completed.Length != m_seekHeadLength) {
            throw new InvalidOperationException(message: $"The completed Matroska SeekHead is {completed.Length} bytes, but its live Void reservation is {m_seekHeadLength} bytes.");
        }

        m_output.Position = m_seekHeadOffset;
        m_output.Write(buffer: completed);
    }
    private void PatchSegmentSize(long segmentEnd) {
        if (m_segmentSizeOffset < 0L) {
            return;
        }

        m_output.Position = m_segmentSizeOffset;
        EbmlWriter.WriteSizeFixed(stream: m_output, size: (segmentEnd - m_segmentDataStart), length: SegmentSizeWidth);
    }
    private void WriteEbmlHeader() {
        using var content = new MemoryStream();

        EbmlWriter.WriteUInt(stream: content, id: MatroskaIds.EbmlVersion, value: 1);
        EbmlWriter.WriteUInt(stream: content, id: MatroskaIds.EbmlReadVersion, value: 1);
        EbmlWriter.WriteUInt(stream: content, id: MatroskaIds.EbmlMaxIdLength, value: 4);
        EbmlWriter.WriteUInt(stream: content, id: MatroskaIds.EbmlMaxSizeLength, value: 8);
        EbmlWriter.WriteAsciiString(stream: content, id: MatroskaIds.DocType, value: (m_docTypeWebm
            ? "webm"
            : "matroska"));
        EbmlWriter.WriteUInt(stream: content, id: MatroskaIds.DocTypeVersion, value: 4);
        EbmlWriter.WriteUInt(stream: content, id: MatroskaIds.DocTypeReadVersion, value: 2);

        var header = content.ToArray();

        EbmlWriter.WriteMasterHeader(stream: m_output, id: MatroskaIds.Ebml, contentSize: header.Length);
        m_output.Write(buffer: header);
    }
    private void ThrowIfStarted() {
        if (m_started) {
            throw new InvalidOperationException(message: "The muxer has already started.");
        }
    }
}
