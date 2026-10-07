using Puck.Testing;
using Xunit;

namespace Puck.World.Protocol.Tests;

/// <summary>Laws for the Hello door's shape fingerprint: a hello carries the shape of the wire contract after its key, and
/// the door refuses a peer of this build's key and another shape by name, apart from a peer of another key.</summary>
public sealed class WorldHelloShapeLawTests {
    [Fact]
    public void TheWireShapeIsTheOneTheLedgerRecords() =>
        Assert.Equal(
            actual: FormatLedgerShapes.Of(id: "WorldProtocol.WireProtocolKey"),
            expected: WorldProtocol.WireShape
        );
    [Fact]
    public void ThisBuildsKeyAndShapeAreAccepted() {
        Assert.True(condition: WorldHelloDoor.TryAccept(
            offeredKey: WorldProtocol.WireProtocolKey,
            offeredShape: WorldProtocol.WireShape,
            refusal: out var refusal
        ));
        Assert.Equal(
            actual: refusal,
            expected: default
        );
    }
    [Fact]
    public void ThisBuildsKeyUnderAnotherShapeIsRefusedByTheShape() {
        var other = ((WorldProtocol.WireShape[0] == '0') ? ('1' + WorldProtocol.WireShape[1..]) : ('0' + WorldProtocol.WireShape[1..]));

        Assert.False(condition: WorldHelloDoor.TryAccept(
            offeredKey: WorldProtocol.WireProtocolKey,
            offeredShape: other,
            refusal: out var refusal
        ));
        Assert.Equal(
            actual: refusal,
            expected: WorldHelloRefusal.WireShapeMismatch
        );
    }
    [Fact]
    public void AnotherKeyIsRefusedByTheKeyWhateverShapeItOffers() {
        Assert.False(condition: WorldHelloDoor.TryAccept(
            offeredKey: (WorldProtocol.WireProtocolKey + 1UL),
            offeredShape: WorldProtocol.WireShape,
            refusal: out var refusal
        ));
        Assert.Equal(
            actual: refusal,
            expected: WorldHelloRefusal.WireProtocolKeyMismatch
        );
    }
}
