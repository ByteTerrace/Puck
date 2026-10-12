using Xunit;

namespace Puck.World.Protocol.Tests;

/// <summary>Laws for the Hello door: a hello carries the wire contract's key, and the door refuses a peer of another key
/// by name.</summary>
public sealed class WorldHelloLawTests {
    [Fact]
    public void ThisBuildsKeyIsAccepted() {
        Assert.True(condition: WorldHelloDoor.TryAccept(
            offeredKey: WorldProtocol.WireProtocolKey,
            refusal: out var refusal
        ));
        Assert.Equal(
            actual: refusal,
            expected: default
        );
    }
    [Fact]
    public void AnotherKeyIsRefusedByTheKey() {
        Assert.False(condition: WorldHelloDoor.TryAccept(
            offeredKey: (WorldProtocol.WireProtocolKey + 1UL),
            refusal: out var refusal
        ));
        Assert.Equal(
            actual: refusal,
            expected: WorldHelloRefusal.WireProtocolKeyMismatch
        );
    }
}