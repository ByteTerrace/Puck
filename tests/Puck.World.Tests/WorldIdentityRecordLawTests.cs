using Puck.Commands;
using System.Numerics;
using System.Text;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldIdentityRecordLawTests {
    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static WorldIdentity Identity() {
        var basis = Fixtures.BuildDocument();
        var document = basis with {
            Identity = new WorldIdentityDefinition(Id: SafeName.Parse(candidate: "record-owner"), Name: "Owner", Color: "#ffffff",
                MoveSpeedState: Name(value: "move"), TurnSpeedState: Name(value: "turn"), Records: [Name(value: "stats")]),
            StateRaw = new WorldStateSection(
                World: [new WorldStateRow(Name: Name(value: "private-secret-row"), Kind: CellKind.Text,
                    Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Text(value: "private-payload"))])],
                Records: [new StateRecord(Name: Name(value: "Stats"), Fields: [
                    new StatePoolField(Name: Name(value: "score"), Default: CellValue.Int(value: 0), Min: 0, Max: 100),
                    new StatePoolField(Name: Name(value: "badge"), Kind: CellKind.Text, Default: CellValue.Text(value: "visitor"))
                ])],
                Pools: [new StatePool(Name: Name(value: "stats"), Record: Name(value: "Stats"), Capacity: 1, Initial: [new StatePoolSeed(Slot: 0)])]
            ),
        };

        return new WorldIdentity(document: document, defaults: basis.PlayerDefaults);
    }

    [Fact]
    public void RecordWritesAreTypedDurableAndRefusalsDoNotChangeTheDocument() {
        var identity = Identity();

        Assert.True(condition: identity.TryWriteRecord(Name(value: "stats"), Name(value: "score"), CellValue.Int(value: 42), out var reason), userMessage: reason);
        Assert.True(condition: identity.TryWriteRecord(Name(value: "stats"), Name(value: "badge"), CellValue.Text(value: "champion"), out reason), userMessage: reason);
        var before = identity.Document;

        Assert.False(condition: identity.TryWriteRecord(Name(value: "stats"), Name(value: "score"), CellValue.Int(value: 101), out _));
        Assert.False(condition: identity.TryWriteRecord(Name(value: "stats"), Name(value: "score"), CellValue.Text(value: "wrong-kind"), out _));
        Assert.Same(before, identity.Document);
        Assert.True(condition: identity.TryReadRecord(Name(value: "stats"), Name(value: "score"), out var score));
        Assert.Equal(42L, score.AsInt);
        Assert.False(condition: identity.TryReadRecord(Name(value: "stats"), Name(value: "missing"), out _));
        var reopened = new WorldIdentity(document: identity.Document!, defaults: Fixtures.BuildDocument().PlayerDefaults);

        Assert.Equal(42L, reopened.RecordState!.Pools![0].Snapshot!.Live![0].Values![0].Value.AsInt);
        Assert.Equal("champion", reopened.RecordState.Pools[0].Snapshot!.Live![0].Values![1].Value.AsText);
    }
    [Fact]
    public void FederationCarriesSelectedTypedRecordsAndNoPrivateRows() {
        var identity = Identity();

        Assert.True(condition: identity.TryWriteRecord(Name(value: "stats"), Name(value: "badge"), CellValue.Text(value: "champion"), out var reason), userMessage: reason);
        var address = new WorldEntityAddress(Authority: "source", Generation: 1, Index: 0);
        var request = new WorldTransferReservationRequest(TransferId: 1, SourceAuthority: "source", SourceRateHz: 240,
            SourceTick: 0, DeadlineSourceTick: 60, Border: "east", BorderCapacity: null,
            PartyAllOrNothing: true, PeerAdmission: true, Members: [new WorldTransferReservationMember(
                Principal: Principal.Console, PreferredSlot: 0, Identity: identity, Source: default,
                BodyColor: Vector3.One, CatalogRig: 0,
                Mobility: new WorldMobilityIdentity(DepartedFrom: address, Epoch: 0, Incarnation: address)
            )]);
        var bytes = WorldFederationCodec.EncodeReservation(request: request);

        Assert.DoesNotContain("private-secret-row", Encoding.UTF8.GetString(bytes: bytes));
        Assert.DoesNotContain("private-payload", Encoding.UTF8.GetString(bytes: bytes));
        Assert.True(condition: WorldFederationCodec.TryDecodeReservation(body: bytes, defaults: Fixtures.BuildDocument().PlayerDefaults,
            request: out var decoded, failure: out var failure), userMessage: failure.ToString());
        var arrived = decoded!.Members[0].Identity!;

        Assert.Null(@object: arrived.Document);
        Assert.True(condition: arrived.TryReadRecord(Name(value: "stats"), Name(value: "badge"), out var badge));
        Assert.Equal("champion", badge.AsText);
        Assert.Equal("champion", arrived.RecordState!.Pools![0].Snapshot!.Live![0].Values![1].Value.AsText);
        Assert.True(condition: arrived.TryWriteRecord(Name(value: "stats"), Name(value: "score"), CellValue.Int(value: 9), out reason), userMessage: reason);
        var onward = WorldIdentity.FromProjection(arrived.Project(), Fixtures.BuildDocument().PlayerDefaults);

        Assert.Equal(9L, onward.RecordState!.Pools![0].Snapshot!.Live![0].Values![0].Value.AsInt);
    }
}
