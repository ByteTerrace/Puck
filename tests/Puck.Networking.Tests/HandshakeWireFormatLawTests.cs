using System.Buffers.Binary;

using Xunit;

namespace Puck.Networking.Tests;

/// <summary>
/// Laws for <see cref="HandshakeWireFormat"/> — the HelloIdentity frame grammar its writer and reader share (the
/// writer never hands the reader a frame the reader would refuse, every grammar violation is a named
/// <see cref="HandshakeWireFormat.HelloIdentityReadResult.Malformed"/> reason, and a disconnect before any frame is
/// a silent <see cref="HandshakeWireFormat.HelloIdentityReadResult.Eof"/>) and the raw length-prefixed frame
/// primitive that returns the prefix and body in one buffer.
/// </summary>
public sealed class HandshakeWireFormatLawTests {
    // The chain-count byte (1) plus the claim's own u32 length prefix (4) are the only overhead an empty chain
    // adds, so this is the largest claim a frame at exactly HandshakeWireFormat.MaxHelloIdentityBytes can carry.
    private const int MaxClaimBytesWithEmptyChain = (((HandshakeWireFormat.MaxHelloIdentityBytes - sizeof(uint)) - 1) - sizeof(uint));

    private static void AssertMalformed(HandshakeWireFormat.HelloIdentityReadResult read, string reason) {
        var malformed = Assert.IsType<HandshakeWireFormat.HelloIdentityReadResult.Malformed>(@object: read);

        Assert.Equal(
            expected: reason,
            actual: malformed.Reason
        );
    }
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
    /// <summary>Builds the HelloIdentity frame carrying exactly <paramref name="body"/>, with an honest prefix.</summary>
    private static byte[] Framed(byte[] body) => Declaring(
        following: ((uint)body.Length),
        rest: body
    );
    private static Task<HandshakeWireFormat.HelloIdentityReadResult> ReadHelloIdentityAsync(byte[] wire) => HandshakeWireFormat.TryReadHelloIdentityAsync(
        ct: TestContext.Current.CancellationToken,
        stream: new MemoryStream(buffer: wire)
    );
    private static Task<byte[]?> ReadLengthPrefixedFrameAsync(byte[] wire, int maxTotalBytes) => HandshakeWireFormat.TryReadLengthPrefixedFrameAsync(
        ct: TestContext.Current.CancellationToken,
        maxTotalBytes: maxTotalBytes,
        stream: new MemoryStream(buffer: wire)
    );

    /// <summary>The control: a claim sized to land the frame exactly at the cap round-trips through the writer and
    /// the reader.</summary>
    [Fact]
    public async Task WriteHelloIdentityAsync_AtTheCap_RoundTripsThroughItsOwnReader() {
        var claim = new byte[MaxClaimBytesWithEmptyChain];

        using var stream = new MemoryStream();

        await HandshakeWireFormat.WriteHelloIdentityAsync(
            chain: [],
            claim: claim,
            ct: TestContext.Current.CancellationToken,
            stream: stream
        );

        stream.Position = 0;

        var read = await HandshakeWireFormat.TryReadHelloIdentityAsync(
            ct: TestContext.Current.CancellationToken,
            stream: stream
        );

        var ok = Assert.IsType<HandshakeWireFormat.HelloIdentityReadResult.Ok>(@object: read);

        Assert.Empty(collection: ok.Chain);
        Assert.Equal(
            expected: claim,
            actual: ok.Claim
        );
    }
    /// <summary>A claim one byte past the cap is refused at the writer, before any bytes reach the wire — the
    /// frame it would have produced is exactly the one <see cref="HandshakeWireFormat.TryReadHelloIdentityAsync"/>
    /// refuses. Falsifier: removing the writer's cap check lets this frame reach the stream, and the assertion
    /// below (nothing written) turns red.</summary>
    [Fact]
    public async Task WriteHelloIdentityAsync_OneByteOverTheCap_RefusesBeforeWritingAnything() {
        var claim = new byte[(MaxClaimBytesWithEmptyChain + 1)];

        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentException>(testCode: () => HandshakeWireFormat.WriteHelloIdentityAsync(
            chain: [],
            claim: claim,
            ct: TestContext.Current.CancellationToken,
            stream: stream
        ));

        Assert.Equal(
            expected: 0,
            actual: stream.Length
        );
    }
    /// <summary>Every chain depth the grammar admits — none, one binding, two bindings — round-trips through the
    /// writer and the reader with each envelope and the claim intact and in order.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task WriteHelloIdentityAsync_ChainOfEveryAdmittedDepth_RoundTripsThroughItsOwnReader(int depth) {
        var ct = TestContext.Current.CancellationToken;
        var chain = new byte[depth][];

        for (var index = 0; (index < depth); index++) {
            // Distinct lengths and bytes per envelope, so a swapped or truncated entry cannot pass as equal.
            chain[index] = Enumerable.Repeat(
                count: (index + 3),
                element: ((byte)(0x10 * (index + 1)))
            ).ToArray();
        }

        byte[] claim = [0xC1, 0xA1, 0x00];

        using var stream = new MemoryStream();

        await HandshakeWireFormat.WriteHelloIdentityAsync(
            chain: chain,
            claim: claim,
            ct: ct,
            stream: stream
        );

        stream.Position = 0;

        var read = await HandshakeWireFormat.TryReadHelloIdentityAsync(
            ct: ct,
            stream: stream
        );

        var ok = Assert.IsType<HandshakeWireFormat.HelloIdentityReadResult.Ok>(@object: read);

        Assert.Equal(
            expected: depth,
            actual: ok.Chain.Count
        );

        for (var index = 0; (index < depth); index++) {
            Assert.Equal(
                expected: chain[index],
                actual: ok.Chain[index]
            );
        }

        Assert.Equal(
            expected: claim,
            actual: ok.Claim
        );
    }
    /// <summary>A chain three bindings deep is a caller bug the writer refuses before any bytes reach the wire —
    /// the reader would name the same depth malformed.</summary>
    [Fact]
    public async Task WriteHelloIdentityAsync_ChainOfThree_RefusesBeforeWritingAnything() {
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(testCode: () => HandshakeWireFormat.WriteHelloIdentityAsync(
            chain: [[1], [2], [3]],
            claim: [4],
            ct: TestContext.Current.CancellationToken,
            stream: stream
        ));

        Assert.Equal(
            expected: 0,
            actual: stream.Length
        );
    }
    /// <summary>A peer that closes before the four-byte prefix completes — whether it sent nothing or only part of
    /// it — is the silent <see cref="HandshakeWireFormat.HelloIdentityReadResult.Eof"/>, never a refusal, and it is
    /// the one shared instance.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task TryReadHelloIdentityAsync_EofBeforeThePrefixCompletes_IsTheSharedEof(int bytesSent) {
        var read = await ReadHelloIdentityAsync(wire: new byte[bytesSent]);

        Assert.Same(
            actual: read,
            expected: HandshakeWireFormat.HelloIdentityReadResult.Eof.Instance
        );
    }
    /// <summary>A prefix declaring one byte past the cap is malformed by name before the body is read, so a peer
    /// cannot make this side allocate more than <see cref="HandshakeWireFormat.MaxHelloIdentityBytes"/>.</summary>
    [Fact]
    public async Task TryReadHelloIdentityAsync_DeclaredLengthOverTheCap_IsMalformed() {
        const uint Cap = (HandshakeWireFormat.MaxHelloIdentityBytes - sizeof(uint));

        using var stream = new MemoryStream(buffer: Declaring(
            following: (Cap + 1),
            rest: [0]
        ));

        var read = await HandshakeWireFormat.TryReadHelloIdentityAsync(
            ct: TestContext.Current.CancellationToken,
            stream: stream
        );

        AssertMalformed(
            read: read,
            reason: "the declared frame length exceeds the HelloIdentity frame cap"
        );
        Assert.Equal(
            expected: sizeof(uint),
            actual: stream.Position
        );
    }
    /// <summary>A disconnect after a prefix already declared a frame is a truncated frame, not a clean close: the
    /// send side is still open for the named refusal.</summary>
    [Fact]
    public async Task TryReadHelloIdentityAsync_EofInsideTheDeclaredBody_IsMalformed() {
        var read = await ReadHelloIdentityAsync(wire: Declaring(
            following: 8,
            rest: [0, 1, 2]
        ));

        AssertMalformed(
            read: read,
            reason: "the connection closed before the declared frame's body completed"
        );
    }
    /// <summary>A prefix declaring zero following bytes leaves no room for the chain-count byte.</summary>
    [Fact]
    public async Task TryReadHelloIdentityAsync_ZeroDeclaredLength_IsMalformed() {
        var read = await ReadHelloIdentityAsync(wire: Framed(body: []));

        AssertMalformed(
            read: read,
            reason: "the frame carries no chain-count byte"
        );
    }
    /// <summary>A chain-count byte of three exceeds the two-binding limit and is refused before any envelope is
    /// read — the bytes that follow it are never consulted.</summary>
    [Fact]
    public async Task TryReadHelloIdentityAsync_ChainCountOfThree_IsMalformed() {
        var read = await ReadHelloIdentityAsync(wire: Framed(body: [3]));

        AssertMalformed(
            read: read,
            reason: "the chain-count byte exceeds the two-binding attestation limit"
        );
    }
    /// <summary>A chain envelope whose length prefix is cut short, or whose prefix declares more bytes than the
    /// frame carries, is a truncated envelope.</summary>
    [Theory]
    [InlineData(new byte[] { 1, 5, 0, 0 })]
    [InlineData(new byte[] { 1, 5, 0, 0, 0, 0xAA })]
    public async Task TryReadHelloIdentityAsync_TruncatedChainEnvelope_IsMalformed(byte[] body) {
        var read = await ReadHelloIdentityAsync(wire: Framed(body: body));

        AssertMalformed(
            read: read,
            reason: "a chain envelope's length prefix or body is truncated"
        );
    }
    /// <summary>A claim whose length prefix is absent or cut short, or whose prefix declares more bytes than the
    /// frame carries, is a truncated claim — with an empty chain and after a complete one alike.</summary>
    [Theory]
    [InlineData(new byte[] { 0 })]
    [InlineData(new byte[] { 0, 2, 0 })]
    [InlineData(new byte[] { 0, 2, 0, 0, 0, 0xAA })]
    [InlineData(new byte[] { 1, 1, 0, 0, 0, 0xEE, 2, 0, 0, 0, 0xAA })]
    public async Task TryReadHelloIdentityAsync_TruncatedClaim_IsMalformed(byte[] body) {
        var read = await ReadHelloIdentityAsync(wire: Framed(body: body));

        AssertMalformed(
            read: read,
            reason: "the claim attestation's length prefix or body is truncated"
        );
    }
    /// <summary>Bytes left over after a complete claim are refused: the frame must be consumed exactly.
    /// Falsifier: dropping the <c>offset != body.Length</c> check decodes this as a well-formed frame.</summary>
    [Fact]
    public async Task TryReadHelloIdentityAsync_TrailingBytesAfterTheClaim_IsMalformed() {
        var read = await ReadHelloIdentityAsync(wire: Framed(body: [0, 1, 0, 0, 0, 0xAA, 0xFF]));

        AssertMalformed(
            read: read,
            reason: "the frame carries trailing bytes after the claim attestation"
        );
    }
    /// <summary>The raw length-prefixed frame comes back as one buffer holding exactly the bytes the peer sent: the
    /// four-byte prefix in front, back-patched with the declared length, then the body. Falsifier: reading the body
    /// into its own buffer and copying it behind a fresh prefix still yields these bytes, so this law is paired
    /// with the zero-length case below — a body of no bytes must still yield the four-byte prefix alone.</summary>
    [Fact]
    public async Task TryReadLengthPrefixedFrameAsync_ReturnsThePrefixInFrontOfTheBody_InOneBuffer() {
        var wire = Framed(body: [0xAA, 0xBB, 0xCC]);

        var whole = await ReadLengthPrefixedFrameAsync(
            maxTotalBytes: 64,
            wire: wire
        );

        Assert.NotNull(@object: whole);
        Assert.Equal(
            actual: whole,
            expected: wire
        );
        Assert.Equal(
            expected: 3u,
            actual: BinaryPrimitives.ReadUInt32LittleEndian(source: whole)
        );
        Assert.Equal(
            expected: (sizeof(uint) + 3),
            actual: whole.Length
        );
    }
    /// <summary>Unlike <see cref="WireFrame.ReadAsync"/>, this primitive tolerates a zero-length body: the buffer
    /// is the four-byte prefix alone, declaring zero, for a caller that draws its own line.</summary>
    [Fact]
    public async Task TryReadLengthPrefixedFrameAsync_ZeroLengthBody_YieldsThePrefixAlone() {
        var wire = Framed(body: []);

        var whole = await ReadLengthPrefixedFrameAsync(
            maxTotalBytes: 64,
            wire: wire
        );

        Assert.NotNull(@object: whole);
        Assert.Equal(
            actual: whole,
            expected: wire
        );
    }
    /// <summary>A disconnect before the prefix, a disconnect inside the declared body, and a declaration past
    /// <c>maxTotalBytes</c> (prefix included) all yield <see langword="null"/> — this primitive names no reason;
    /// its caller does.</summary>
    [Theory]
    [InlineData(new byte[] { }, 64)]
    [InlineData(new byte[] { 2, 0 }, 64)]
    [InlineData(new byte[] { 5, 0, 0, 0, 0xAA, 0xBB }, 64)]
    [InlineData(new byte[] { 5, 0, 0, 0, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE }, 8)]
    public async Task TryReadLengthPrefixedFrameAsync_EofOrOverCap_YieldsNull(byte[] wire, int maxTotalBytes) {
        var whole = await ReadLengthPrefixedFrameAsync(
            maxTotalBytes: maxTotalBytes,
            wire: wire
        );

        Assert.Null(@object: whole);
    }
}
