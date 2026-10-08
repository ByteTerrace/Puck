using System.Buffers.Binary;
using System.Numerics;
using Puck.Maths;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>
/// THE LAW: a document the federation wire delivers carries a well-formed <see cref="WorldDocumentVersion"/> or is
/// refused at the decoder. A document leaf with a negative sequence, or with a sequence and no activation, is malformed;
/// the empty version, whole, is a reservation's preview and decodes; a route, which is always a live delivery, refuses
/// the preview version too. Each refusal has a red leg: the same leaf, well formed, decodes.
/// </summary>
public sealed class DocumentVersionWireLawTests {
    private static readonly WorldDocumentVersion Delivered = new(Activation: Guid.Parse(input: "11223344-5566-7788-99aa-bbccddeeff00"), Sequence: 12L);

    private static byte[] Leaf(WorldDocumentVersion version) => WorldFederationCodec.EncodeDocument(
        authority: "127.0.0.1:5000",
        definition: Fixtures.BuildDocument(),
        revision: 0,
        tier: WorldDisclosureTier.Replica,
        time: ArenaTime.At(engineTick: 0UL, tick: 0UL),
        version: version
    );
    private static bool Decodes(byte[] leaf, out WorldDocumentVersion version) => WorldFederationCodec.TryDecodeDocument(
        body: leaf,
        definition: out _,
        failure: out _,
        tier: out _,
        version: out version
    );
    private static WorldAuthorityRouteDescription Route(WorldDocumentVersion version) => new(
        BodyColor: Vector3.One,
        CatalogRig: 0,
        Definition: Fixtures.BuildDocument(),
        Endpoint: "127.0.0.1:42001",
        Entity: new WorldEntityAddress(Authority: "world/corner-sw", Generation: 1, Index: 0),
        Kit: 0,
        Look: 0,
        Orientation: FixedQuaternion.Identity,
        PlacementId: null,
        Position: FixedVector3.Zero,
        Tick: 1UL,
        TravelTurn: FixedQ4816.Zero,
        Version: version
    );
    private static bool RouteDecodes(WorldDocumentVersion version) => WorldFederationCodec.TryDecodeRoute(
        body: WorldFederationCodec.EncodeRoute(
            authority: "world/corner-sw",
            revision: 0,
            route: Route(version: version),
            tier: WorldDisclosureTier.Replica,
            time: ArenaTime.At(engineTick: 0UL, tick: 0UL)
        ),
        failure: out _,
        route: out _
    );

    [Fact]
    public void ADocumentLeafCarriesAWellFormedVersionOrIsRefused() {
        var leaf = Leaf(version: Delivered);

        // Red leg: the leaf as encoded decodes, with its version.
        Assert.True(condition: Decodes(leaf: leaf, version: out var decoded));
        Assert.Equal(actual: decoded, expected: Delivered);

        var negative = leaf.ToArray();

        BinaryPrimitives.WriteInt64LittleEndian(destination: negative.AsSpan(start: 17), value: -1L);
        Assert.False(condition: Decodes(leaf: negative, version: out _));

        var unnamed = leaf.ToArray();

        unnamed.AsSpan(length: 16, start: 1).Clear();
        Assert.False(condition: Decodes(leaf: unnamed, version: out _));

        // A reservation's preview carries the empty version, whole, and decodes as one.
        Assert.True(condition: Decodes(leaf: Leaf(version: default), version: out var preview));
        Assert.Equal(actual: preview, expected: default);
    }
    [Fact]
    public void ALiveRouteRefusesADocumentWithNoDeliveredVersion() {
        // Red leg: the same route with a delivered version decodes.
        Assert.True(condition: RouteDecodes(version: Delivered));
        Assert.False(condition: RouteDecodes(version: default));
    }
}
