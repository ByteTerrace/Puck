using Puck.Commands;
using Xunit;

namespace Puck.World.Testing;

/// <summary>The owned identity a crossing carries, with private rows whose absence from bytes is a real search.</summary>
internal static class CrossingIdentityFixtures {
    // An owned identity carrying one fact and one written record field beside its private rows.
    internal static WorldIdentity Owned(WorldIdentity? identity = null) {
        var owned = (identity ?? new WorldIdentity(
            defaults: Fixtures.BuildDocument().PlayerDefaults,
            document: OwnedDocument()
        ));

        Assert.True(condition: owned.TrySetFact(key: Name(value: "homeFact"), value: 3, changed: out _, reason: out var reason), userMessage: reason);
        Assert.True(condition: owned.TryWriteRecord(record: Name(value: "stats"), field: Name(value: "badge"), value: CellValue.Text(value: "champion"), reason: out reason), userMessage: reason);
        owned.Bindings = new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [],
            Chords: []
        );
        return owned;
    }
    internal static WorldDefinition OwnedDocument() {
        var basis = Fixtures.BuildDocument();

        return basis with {
            Identity = new WorldIdentityDefinition(
                Id: SafeName.Parse(candidate: OwnerId),
                Name: "Owner",
                Color: "#3366cc",
                MoveSpeedState: Name(value: "move"),
                TurnSpeedState: Name(value: "turn"),
                Travel: [new WorldIdentityPool(Pool: Name(value: "stats"), Class: WorldIdentityTravel.Crossing)]
            ),
            HudRaw = new WorldHudSection(
                Defaults: new WorldHudDefaults(Enabled: true),
                Panels: [new WorldHudPanel(
                    Id: PrivatePanel,
                    Rect: new WorldHudRect(Height: 1f, Width: 1f, X: 0f, Y: 0f),
                    Layer: WorldHudLayer.Over,
                    Style: WorldHudPanelStyle.Chip,
                    Elements: []
                )]
            ),
            StateRaw = new WorldStateSection(
                World: [new WorldStateRow(
                    Name: Name(value: PrivateRow),
                    Kind: CellKind.Text,
                    Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Text(value: PrivatePayload))]
                )],
                Records: [new StateRecord(Name: Name(value: "Stats"), Fields: [
                    new StatePoolField(Name: Name(value: "score"), Default: CellValue.Int(value: 0), Min: 0, Max: 100),
                    new StatePoolField(Name: Name(value: "badge"), Kind: CellKind.Text, Default: CellValue.Text(value: "visitor")),
                ])],
                Pools: [new StatePool(Name: Name(value: "stats"), Record: Name(value: "Stats"), Capacity: 1, Initial: [new StatePoolSeed(Slot: 0)])]
            ),
        };
    }
    internal static CellName Name(string value) => CellName.Parse(candidate: value);

    internal const string OwnerId = "privacy-owner";
    internal const string PrivatePanel = "private-hud-panel";
    internal const string PrivatePayload = "private-payload";
    internal const string PrivateRow = "private-secret-row";

    internal static readonly string[] Markers = [PrivateRow, PrivatePayload, PrivatePanel];

    // A fact the identity's own owner writes, as the console does (WorldIdentityWriter.Home).
    internal static bool TrySetFact(this WorldIdentity identity, CellName key, long value, out bool changed, out string reason) =>
        identity.TrySetFact(changed: out changed, key: key, reason: out reason, value: value, writer: WorldIdentityWriter.Home);
    // A fact the identity's own owner writes through the account's door.
    internal static bool TrySetFact(this Puck.World.Server.WorldOwnedWorlds catalog, WorldIdentity identity, CellName key, long value, out bool changed, out string reason) =>
        catalog.TrySetFact(changed: out changed, durability: out _, identity: identity, key: key, reason: out reason, value: value, writer: WorldIdentityWriter.Home);
    // One field of the instance in slot zero of a pool, written by the owner through a stage and installed.
    internal static bool TryWriteRecord(this WorldIdentity identity, CellName record, CellName field, CellValue value, out string reason) {
        if (!identity.TryOpenStage(reason: out reason, stage: out var stage) ||
            !stage.TryWrite(field: field, generation: null, pool: record, reason: out reason, slot: 0, value: value, writer: WorldIdentityWriter.Home)) {
            return false;
        }

        return identity.Apply(reason: out reason, stage: stage);
    }
}
