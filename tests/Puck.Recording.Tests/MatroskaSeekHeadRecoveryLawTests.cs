using Puck.Recording.Matroska;
using Xunit;

namespace Puck.Recording.Tests;

public sealed class MatroskaSeekHeadRecoveryLawTests {
    private static ReadOnlySpan<byte> SegmentId => [0x18, 0x53, 0x80, 0x67];
    private static ReadOnlySpan<byte> SeekHeadId => [0x11, 0x4D, 0x9B, 0x74];

    [Fact]
    public void LiveFileReservesVoidAndCleanStopPublishesSeekHeadAtTheSameOffset() {
        using var output = new MemoryStream();
        using var muxer = new MatroskaMuxer(output: output, webmDocType: true);

        var track = muxer.AddAudioTrack(
            codecId: "A_OPUS",
            codecPrivate: ReadOnlyMemory<byte>.Empty,
            channelCount: 2,
            samplingFrequency: 48_000d,
            codecDelayNanoseconds: 0L,
            seekPreRollNanoseconds: 0L);

        muxer.Start();
        muxer.WriteBlock(trackNumber: track, data: [0x01, 0x02, 0x03], timestampNanoseconds: 0L, isKeyframe: true);

        var live = output.ToArray();
        var reservationOffset = SegmentPayloadOffset(bytes: live);

        Assert.Equal(expected: 0xEC, actual: live[reservationOffset]);
        Assert.False(condition: live.AsSpan(start: reservationOffset).StartsWith(value: SeekHeadId), userMessage: "a live file must not advertise unresolved Seek entries");

        muxer.Stop();

        var completed = output.ToArray();

        Assert.True(condition: completed.AsSpan(start: reservationOffset).StartsWith(value: SeekHeadId), userMessage: "clean Stop must replace the live Void reservation with SeekHead at the identical offset");
    }

    private static int SegmentPayloadOffset(ReadOnlySpan<byte> bytes) {
        var segmentOffset = bytes.IndexOf(value: SegmentId);

        Assert.True(condition: segmentOffset >= 0, userMessage: "the muxer did not write a Segment element");

        // Segment is a four-byte id followed by the writer's fixed eight-byte unknown-size field while live.
        return (segmentOffset + SegmentId.Length + 8);
    }
}
