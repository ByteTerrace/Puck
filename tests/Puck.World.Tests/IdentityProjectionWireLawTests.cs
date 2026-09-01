using System.Numerics;
using System.Text;

using Xunit;

using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// Proves what a traveler discloses when it walks into another authority's world: appearance and motion-envelope
/// claims cross, and the owned identity document behind them does not. The private rows are given distinctive
/// values so the negative (their bytes are absent from the frame) is a real search rather than a search for
/// something that was never there.
/// </summary>
public sealed class IdentityProjectionWireLawTests {
    private const string PrivateChatPeer = "chat-allow-list-secret-peer";
    private const string PrivateStateRow = "crossGameProgressSecret";

    [Fact]
    public void ReservationWire_CarriesAppearanceAndRates_NeverTheOwnedDocument() {
        var defaults = Fixtures.BuildDocument().PlayerDefaults;
        var owned = OwnedIdentityDocument();
        var identity = new WorldIdentity(document: owned, defaults: defaults);
        var request = new WorldTransferReservationRequest(
            TransferId: 5UL,
            SourceAuthority: "machine-a/boot",
            SourceRateHz: 240,
            SourceTick: 0UL,
            DeadlineSourceTick: 60UL,
            Border: "east",
            BorderCapacity: null,
            PartyAllOrNothing: true,
            PeerAdmission: true,
            Members: [
                new WorldTransferReservationMember(
                    Principal: WorldPrincipal.Console,
                    PreferredSlot: 0,
                    Identity: identity,
                    Source: default,
                    BodyColor: new Vector3(x: 0.1f, y: 0.2f, z: 0.3f),
                    CatalogRig: 3,
                    Mobility: new WorldMobilityIdentity(Incarnation: new WorldEntityAddress(Authority: "machine-a/boot", Index: 0, Generation: 1), Epoch: 0UL)),
            ]);

        var encoded = WorldFederationCodec.EncodeReservation(request: request);
        var frame = Encoding.UTF8.GetString(bytes: encoded);

        Assert.DoesNotContain(expectedSubstring: PrivateChatPeer, actualString: frame, comparisonType: StringComparison.Ordinal);
        Assert.DoesNotContain(expectedSubstring: PrivateStateRow, actualString: frame, comparisonType: StringComparison.Ordinal);
        Assert.DoesNotContain(expectedSubstring: "puck.world.def.v1", actualString: frame, comparisonType: StringComparison.Ordinal);
        // The control: what the destination legitimately needs did cross.
        Assert.Contains(expectedSubstring: "traveller-one", actualString: frame, comparisonType: StringComparison.Ordinal);

        Assert.True(WorldFederationCodec.TryDecodeReservation(body: encoded, defaults: defaults, request: out var decoded, failure: out var failure), failure.ToString());
        Assert.NotNull(decoded);

        var arrived = decoded!.Members[0].Identity;

        Assert.NotNull(arrived);
        Assert.Equal(expected: identity.Id, actual: arrived!.Id);
        Assert.Equal(expected: identity.Name, actual: arrived.Name);
        Assert.Equal(expected: identity.ColorHex, actual: arrived.ColorHex);
        Assert.Equal(expected: identity.FixedMoveSpeed, actual: arrived.FixedMoveSpeed);
        Assert.Equal(expected: identity.FixedTurnSpeed, actual: arrived.FixedTurnSpeed);
        // Nothing the destination can read its way back into: no document, so no grants, no state, no bindings.
        Assert.Null(arrived.Document);
        Assert.Null(arrived.Bindings);
        Assert.Null(arrived.Hud);
        Assert.False(arrived.TryReadState(name: PrivateStateRow, row: out _));
    }

    [Fact]
    public void ReservationWire_CarriesAbsentRatesAsAbsent_KitDrivesTheArrival() {
        var defaults = Fixtures.BuildDocument().PlayerDefaults;
        var owned = OwnedIdentityDocument();
        // An identity claiming no rates: the named slots exist, their rows do not.
        var rateless = owned with {
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(Name: WorldCellName.Parse(candidate: PrivateStateRow), Kind: CellKind.Int, Cells: [new WorldStateCell(Key: WorldCellName.Parse(candidate: WorldStateRow.SlotKey), Value: 91L)]),
            ]),
        };
        var identity = new WorldIdentity(document: rateless, defaults: defaults);

        Assert.Null(identity.FixedMoveSpeed);
        Assert.Null(identity.FixedTurnSpeed);

        var request = new WorldTransferReservationRequest(
            TransferId: 6UL,
            SourceAuthority: "machine-a/boot",
            SourceRateHz: 240,
            SourceTick: 0UL,
            DeadlineSourceTick: 60UL,
            Border: "east",
            BorderCapacity: null,
            PartyAllOrNothing: true,
            PeerAdmission: true,
            Members: [
                new WorldTransferReservationMember(
                    Principal: WorldPrincipal.Console,
                    PreferredSlot: 0,
                    Identity: identity,
                    Source: default,
                    BodyColor: new Vector3(x: 0.1f, y: 0.2f, z: 0.3f),
                    CatalogRig: 3,
                    Mobility: new WorldMobilityIdentity(Incarnation: new WorldEntityAddress(Authority: "machine-a/boot", Index: 0, Generation: 1), Epoch: 0UL)),
            ]);

        var encoded = WorldFederationCodec.EncodeReservation(request: request);

        Assert.True(WorldFederationCodec.TryDecodeReservation(body: encoded, defaults: defaults, request: out var decoded, failure: out var failure), failure.ToString());

        var arrived = decoded!.Members[0].Identity;

        Assert.NotNull(arrived);
        Assert.Null(arrived!.FixedMoveSpeed);
        Assert.Null(arrived.FixedTurnSpeed);
    }

    private static WorldDefinition OwnedIdentityDocument() {
        var document = Fixtures.BuildDocument();

        return document with {
            DocumentId = "traveller-one",
            Identity = new WorldIdentityDefinition(
                Id: WorldSafeName.Parse(candidate: "traveller-one"),
                Name: "Traveller One",
                Color: "#3366cc",
                MoveSpeedState: WorldCellName.Parse(candidate: "ownMoveSpeed"),
                TurnSpeedState: WorldCellName.Parse(candidate: "ownTurnSpeed"),
                Controllers: []),
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(Name: WorldCellName.Parse(candidate: "ownMoveSpeed"), Kind: CellKind.Fixed, Cells: [new WorldStateCell(Key: WorldCellName.Parse(candidate: WorldStateRow.SlotKey), Value: FixedQ4816.FromDouble(value: 4.5).Value)]),
                new WorldStateRow(Name: WorldCellName.Parse(candidate: "ownTurnSpeed"), Kind: CellKind.Fixed, Cells: [new WorldStateCell(Key: WorldCellName.Parse(candidate: WorldStateRow.SlotKey), Value: FixedQ4816.FromDouble(value: 2.25).Value)]),
                new WorldStateRow(Name: WorldCellName.Parse(candidate: PrivateStateRow), Kind: CellKind.Int, Cells: [new WorldStateCell(Key: WorldCellName.Parse(candidate: WorldStateRow.SlotKey), Value: 91L)]),
            ]),
            GrantsRaw = [
                new WorldGrant(Principal: WorldPrincipal.Document(id: PrivateChatPeer), Capability: WorldCapability.Mutate, Subject: GrantSubject.Section(section: WorldSection.State), Exclusive: false),
            ],
        };
    }
}
