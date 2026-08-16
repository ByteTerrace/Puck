using Xunit;

namespace Puck.Networking.Tests;

/// <summary>
/// Laws for <see cref="HandshakeWireFormat.WriteHelloIdentityAsync"/>'s frame-size boundary — its own
/// <see cref="HandshakeWireFormat.TryReadHelloIdentityAsync"/> refuses a frame over
/// <see cref="HandshakeWireFormat.MaxHelloIdentityBytes"/>, so the writer must never hand its reader a frame the
/// reader itself would refuse.
/// </summary>
public sealed class HandshakeWireFormatLawTests {
    // The chain-count byte (1) plus the claim's own u32 length prefix (4) are the only overhead an empty chain
    // adds, so this is the largest claim a frame at exactly HandshakeWireFormat.MaxHelloIdentityBytes can carry.
    private const int MaxClaimBytesWithEmptyChain = (((HandshakeWireFormat.MaxHelloIdentityBytes - sizeof(uint)) - 1) - sizeof(uint));

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
}
