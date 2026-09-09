using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Xunit;

namespace Puck.Networking.Tests;

/// <summary>A read-only stream that serves a fixed byte sequence and then blocks — never reporting EOF — until the
/// read's own token cancels, the way a live socket idles between frames. Each read hands back at most what the
/// caller asked for and at most what is left, exactly as a socket does, and the size of the very first request is
/// recorded so a law can pin how many bytes a reader asked for before any frame was declared.</summary>
file sealed class ServeThenBlockStream(byte[] bytes) : Stream {
    private int m_offset;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    /// <summary>The length of the first read request, or <c>-1</c> before any read.</summary>
    public int FirstRequestLength { get; private set; } = -1;
    public override long Length => throw new NotSupportedException();
    public override long Position {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() {
    }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
        if (FirstRequestLength < 0) {
            FirstRequestLength = buffer.Length;
        }

        var remaining = (bytes.Length - m_offset);

        if (remaining == 0) {
            await Task.Delay(
                cancellationToken: cancellationToken,
                delay: Timeout.InfiniteTimeSpan
            ).ConfigureAwait(continueOnCapturedContext: false);

            // Unreachable — an infinite delay only ever ends by throwing — but never let a read report EOF.
            throw new OperationCanceledException(token: cancellationToken);
        }

        var count = Math.Min(
            val1: remaining,
            val2: buffer.Length
        );

        bytes.AsMemory(
            length: count,
            start: m_offset
        ).CopyTo(destination: buffer);
        m_offset += count;

        return count;
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Laws for the one frame grammar every socket shares — <see cref="FrameCodec"/> over a complete buffer and
/// <see cref="WireFrame"/> over a stream: <c>[u32 following][u8 kind][payload]</c>, little-endian, where
/// <c>following</c> counts the kind byte plus the payload and never its own prefix. Every byte here is what a peer
/// could send, so each refusal is a name, never an exception, and an oversized declaration is refused before the
/// body is read.
/// </summary>
public sealed class FrameLawTests {
    private const byte Kind = 0x2A;

    /// <summary>Builds <c>[u32 declared][rest…]</c> with a prefix that need not agree with what follows it.</summary>
    private static byte[] Declaring(uint following, byte[] rest) {
        var wire = new byte[checked((sizeof(uint) + rest.Length))];

        BinaryPrimitives.WriteUInt32LittleEndian(
            destination: wire,
            value: following
        );
        rest.CopyTo(
            array: wire,
            index: sizeof(uint)
        );

        return wire;
    }
    private static Task<WireFrameRead> ReadAsync(byte[] wire, int maxFrameBytes = 4096) => WireFrame.ReadAsync(
        ct: TestContext.Current.CancellationToken,
        maxFrameBytes: maxFrameBytes,
        stream: new MemoryStream(buffer: wire)
    );

    /// <summary>The control: a joined frame splits back to the same kind and payload, and the payload span is a
    /// window onto the frame rather than a copy of it.</summary>
    [Fact]
    public void Join_TrySplit_RoundTrips_AndThePayloadAliasesTheFrame() {
        byte[] payload = [1, 2, 3, 4, 5];
        var frame = FrameCodec.Join(
            kind: Kind,
            payload: payload
        );

        Assert.Equal(
            expected: (FrameCodec.PrefixBytes + payload.Length),
            actual: frame.Length
        );
        Assert.Equal(
            expected: ((uint)(payload.Length + sizeof(byte))),
            actual: BinaryPrimitives.ReadUInt32LittleEndian(source: frame)
        );
        Assert.True(
            condition: FrameCodec.TrySplit(
                failure: out var failure,
                frame: frame,
                kind: out var kind,
                maxPayloadBytes: payload.Length,
                payload: out var split
            ),
            userMessage: failure.ToString()
        );
        Assert.Equal(
            actual: kind,
            expected: Kind
        );
        Assert.Equal(
            expected: payload,
            actual: split.ToArray()
        );
        Assert.True(condition: split.Overlaps(other: frame));
    }
    /// <summary>A buffer shorter than the five-byte prefix cannot carry a kind, so it is refused by name before the
    /// length is even read.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void TrySplit_ShorterThanThePrefix_RefusesFrameLengthInvalid(int length) {
        var frame = new byte[length];

        Assert.False(condition: FrameCodec.TrySplit(
            failure: out var failure,
            frame: frame,
            kind: out _,
            maxPayloadBytes: 64,
            payload: out _
        ));
        Assert.Equal(
            expected: WireRefusal.FrameLengthInvalid,
            actual: failure.Refusal
        );
    }
    /// <summary>A prefix that disagrees with the bytes actually carried — declaring fewer, more, or none at all — is
    /// refused: a complete buffer must be exactly the frame its prefix declares.</summary>
    [Theory]
    [InlineData(0u)]
    [InlineData(2u)]
    [InlineData(4u)]
    public void TrySplit_PrefixDisagreesWithTheBuffer_RefusesFrameLengthInvalid(uint declared) {
        // The buffer carries three following bytes: the kind and two payload bytes.
        var frame = Declaring(
            following: declared,
            rest: [Kind, 0xAA, 0xBB]
        );

        Assert.False(condition: FrameCodec.TrySplit(
            failure: out var failure,
            frame: frame,
            kind: out _,
            maxPayloadBytes: 64,
            payload: out _
        ));
        Assert.Equal(
            expected: WireRefusal.FrameLengthInvalid,
            actual: failure.Refusal
        );
    }
    /// <summary>A well-formed frame whose payload is one byte over the caller's cap is refused as too large, while
    /// the same frame under a cap one byte wider splits. Falsifier: dropping the cap check admits both.</summary>
    [Fact]
    public void TrySplit_PayloadOverTheCallersCap_RefusesPayloadTooLarge() {
        var payload = new byte[9];
        var frame = FrameCodec.Join(
            kind: Kind,
            payload: payload
        );

        Assert.False(condition: FrameCodec.TrySplit(
            failure: out var failure,
            frame: frame,
            kind: out _,
            maxPayloadBytes: 8,
            payload: out _
        ));
        Assert.Equal(
            expected: WireRefusal.PayloadTooLarge,
            actual: failure.Refusal
        );
        Assert.True(condition: FrameCodec.TrySplit(
            failure: out _,
            frame: frame,
            kind: out _,
            maxPayloadBytes: 9,
            payload: out _
        ));
    }
    /// <summary>A peer that closes before the four-byte prefix completes — whether it sent nothing or only part of
    /// it — is a clean close: <see cref="WireRefusal.ConnectionClosed"/>, narrated as before any frame.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task ReadAsync_EofBeforeThePrefixCompletes_RefusesConnectionClosed(int bytesSent) {
        var read = await ReadAsync(wire: new byte[bytesSent]);

        Assert.False(condition: read.Ok);
        Assert.Equal(
            expected: WireRefusal.ConnectionClosed,
            actual: read.Failure.Refusal
        );
        Assert.Contains(
            actualString: read.Failure.Detail,
            expectedSubstring: "before a frame prefix arrived",
            comparisonType: StringComparison.Ordinal
        );
        Assert.True(condition: read.Body.IsEmpty);
    }
    /// <summary>A peer that closes after declaring a length but before delivering it is still
    /// <see cref="WireRefusal.ConnectionClosed"/>, but narrated as inside a frame — the caller can tell a truncated
    /// frame from a quiet close.</summary>
    [Fact]
    public async Task ReadAsync_EofInsideTheBody_RefusesConnectionClosed() {
        var read = await ReadAsync(wire: Declaring(
            following: 3,
            rest: [Kind, 0xAA]
        ));

        Assert.False(condition: read.Ok);
        Assert.Equal(
            expected: WireRefusal.ConnectionClosed,
            actual: read.Failure.Refusal
        );
        Assert.Contains(
            actualString: read.Failure.Detail,
            expectedSubstring: "inside a 3-byte frame",
            comparisonType: StringComparison.Ordinal
        );
    }
    /// <summary>A declared length one past the cap is refused by name with only the prefix consumed — the body is
    /// neither allocated nor read, so a peer cannot make this side buffer more than the cap. The same bytes with a
    /// declaration exactly at the cap read whole. Falsifier: moving the cap check after the body read leaves the
    /// stream at its end instead of at the prefix.</summary>
    [Fact]
    public async Task ReadAsync_DeclaredLengthOverTheCap_RefusesFrameLengthInvalid_WithoutReadingTheBody() {
        const int MaxFrameBytes = 64;
        const int Cap = (MaxFrameBytes - sizeof(uint));
        var ct = TestContext.Current.CancellationToken;

        using var over = new MemoryStream(buffer: Declaring(
            following: (Cap + 1),
            rest: new byte[(Cap + 1)]
        ));

        var refused = await WireFrame.ReadAsync(
            ct: ct,
            maxFrameBytes: MaxFrameBytes,
            stream: over
        );

        Assert.False(condition: refused.Ok);
        Assert.Equal(
            expected: WireRefusal.FrameLengthInvalid,
            actual: refused.Failure.Refusal
        );
        Assert.Equal(
            expected: sizeof(uint),
            actual: over.Position
        );

        using var at = new MemoryStream(buffer: Declaring(
            following: Cap,
            rest: new byte[Cap]
        ));

        var admitted = await WireFrame.ReadAsync(
            ct: ct,
            maxFrameBytes: MaxFrameBytes,
            stream: at
        );

        Assert.True(
            condition: admitted.Ok,
            userMessage: admitted.Failure.ToString()
        );
        Assert.Equal(
            expected: (Cap - sizeof(byte)),
            actual: admitted.Body.Length
        );
    }
    /// <summary>A prefix declaring zero following bytes has no room for a kind, so it is refused as an invalid
    /// length rather than read as an empty frame.</summary>
    [Fact]
    public async Task ReadAsync_ZeroDeclaredLength_RefusesFrameLengthInvalid() {
        var read = await ReadAsync(wire: Declaring(
            following: 0,
            rest: []
        ));

        Assert.False(condition: read.Ok);
        Assert.Equal(
            expected: WireRefusal.FrameLengthInvalid,
            actual: read.Failure.Refusal
        );
    }
    /// <summary>The sliced-scratch law. The smallest frame is five bytes — the prefix and a kind — and a socket that
    /// delivers exactly those and then idles must still yield the frame. The prefix is read into a pooled array that
    /// is at least sixteen bytes long, and <see cref="HandshakeWireFormat.TryReadExactAsync"/> fills whatever memory
    /// it is handed, so the read must ask for exactly four bytes. Falsifier: handing the exact read the whole rented
    /// array makes it wait for twelve bytes that never come; this law then hangs until the deadline cancels it.</summary>
    [Fact]
    public async Task ReadAsync_FiveByteFrameOnAStreamThatThenBlocks_StillReturns() {
        using var deadline = Laws.SocketDeadline();
        using var stream = new ServeThenBlockStream(bytes: Declaring(
            following: 1,
            rest: [Kind]
        ));

        var read = await WireFrame.ReadAsync(
            ct: deadline.Token,
            maxFrameBytes: 4096,
            stream: stream
        );

        Assert.True(
            condition: read.Ok,
            userMessage: read.Failure.ToString()
        );
        Assert.Equal(
            expected: Kind,
            actual: read.Kind
        );
        Assert.True(condition: read.Body.IsEmpty);
        Assert.Equal(
            expected: sizeof(uint),
            actual: stream.FirstRequestLength
        );
    }
    /// <summary>A written frame is exactly the bytes <see cref="FrameCodec.Join"/> produces, and reading it back
    /// yields the same kind and body — with the body a slice starting one byte into the frame's own buffer, never a
    /// copy. Falsifier: copying the body out (<c>frame[1..]</c>) puts it at offset zero of a buffer its own length.</summary>
    [Fact]
    public async Task WriteAsync_ReadAsync_RoundTrips_WithTheBodyAsASliceOverTheFrameBuffer() {
        var ct = TestContext.Current.CancellationToken;
        byte[] body = [0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01];

        using var stream = new MemoryStream();

        await WireFrame.WriteAsync(
            body: body,
            ct: ct,
            kind: Kind,
            stream: stream
        );

        Assert.Equal(
            expected: FrameCodec.Join(
                kind: Kind,
                payload: body
            ),
            actual: stream.ToArray()
        );

        stream.Position = 0;

        var read = await WireFrame.ReadAsync(
            ct: ct,
            maxFrameBytes: 4096,
            stream: stream
        );

        Assert.True(
            condition: read.Ok,
            userMessage: read.Failure.ToString()
        );
        Assert.Equal(
            expected: Kind,
            actual: read.Kind
        );
        Assert.Equal(
            expected: body,
            actual: read.Body.ToArray()
        );
        Assert.True(condition: MemoryMarshal.TryGetArray(
            memory: read.Body,
            segment: out var segment
        ));
        Assert.NotNull(@object: segment.Array);
        Assert.Equal(
            expected: 1,
            actual: segment.Offset
        );
        Assert.Equal(
            expected: (body.Length + sizeof(byte)),
            actual: segment.Array.Length
        );
    }
}
