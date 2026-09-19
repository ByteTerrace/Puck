using Puck.Assets.Documents;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the two doors a state mutation is refused at: the arena its cell write composes through, which
/// decides the row's own envelope, capacity and addresses, and
/// <see cref="WorldDefinitionValidator.TryValidateTouchedStateRows"/>, which refuses the cross-row reasons the
/// whole-document walk would refuse for, checking only the rows the mutation touched.</summary>
public sealed class StateMutationValidationLawTests {
    [Fact]
    public void AKeysOfZoneRefusesAKeyOutsideItsTokenDomain() {
        // A Transfer can never produce this shape (the transform compiler refuses two zones over different domains
        // outright), so the live door a state mutation actually reaches this through is a direct cell upsert — the
        // one write compose never checks against the zone's own domain.
        var domain = new WorldStateRow(
            Name(value: "cards"),
            CellKind.Int,
            Capacity: 2,
            Cells: [Cell("c1"), Cell("c2")]
        );
        var zone = new WorldStateRow(
            Name(value: "zone"),
            CellKind.Bool,
            Domain: new StateDomain.KeysOf(
                Name(value: "cards"),
                Ordered: true
            )
        );
        using var fixture = Fixtures.FreshServer(definition: Document([domain, zone]));
        var before = fixture.DefinitionBytes();
        var refusals = new List<string>();

        fixture.Server.EchoTap = echo => { if (echo.Rejected) { refusals.Add(item: echo.Message); } };

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: WorldPrincipal.Console,
            Row: "zone",
            Key: "ghost",
            Value: 1,
            Kind: WorldDocumentWriteKind.Set
        ));
        fixture.Step();

        Assert.Contains(
            collection: refusals,
            filter: reason => reason.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "outside token domain"
            )
        );
        Assert.Equal(
            expected: before,
            actual: fixture.DefinitionBytes()
        );
    }
    [Fact]
    public void RemovingATokenStillReferencedByAKeysOfZoneIsRefused() {
        // The zone is never named by the mutation — only its domain row 'cards' is — so the touched-row walk must
        // queue 'zone' itself once it sees 'cards' is touched, or the removal leaves 'zone' holding a key outside
        // its own domain, a shape the whole-document walk would have refused.
        var domain = new WorldStateRow(
            Name(value: "cards"),
            CellKind.Int,
            Capacity: 2,
            Cells: [Cell("c1"), Cell("c2")]
        );
        var zone = new WorldStateRow(
            Name(value: "zone"),
            CellKind.Bool,
            Domain: new StateDomain.KeysOf(
                Name(value: "cards"),
                Ordered: true
            ),
            Cells: [Cell(
                    key: "c1",
                    value: 1,
                    kind: CellKind.Bool
                )]
        );
        using var fixture = Fixtures.FreshServer(definition: Document([domain, zone]));
        var before = fixture.DefinitionBytes();
        var refusals = new List<string>();

        fixture.Server.EchoTap = echo => { if (echo.Rejected) { refusals.Add(item: echo.Message); } };

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.RemoveStateCell(
            Principal: WorldPrincipal.Console,
            Row: "cards",
            Key: "c1"
        ));
        fixture.Step();

        Assert.Contains(
            collection: refusals,
            filter: reason => reason.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "outside token domain"
            )
        );
        Assert.Equal(
            expected: before,
            actual: fixture.DefinitionBytes()
        );
    }
    [Fact]
    public void ABoolCellRefusesAValueOtherThanZeroOrOne() {
        var flag = new WorldStateRow(
            Name(value: "flag"),
            CellKind.Bool,
            Cells: [new StateCell(
                    WorldStateRow.SlotKey,
                    CellValue.Bool(value: false)
                )]
        );
        using var fixture = Fixtures.FreshServer(definition: Document([flag]));
        var before = fixture.DefinitionBytes();
        var refusals = new List<string>();

        fixture.Server.EchoTap = echo => { if (echo.Rejected) { refusals.Add(item: echo.Message); } };

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: WorldPrincipal.Console,
            Row: "flag",
            Key: WorldStateRow.SlotKey.Value,
            Value: 2,
            Kind: WorldDocumentWriteKind.Set
        ));
        fixture.Step();

        Assert.Contains(
            collection: refusals,
            filter: reason => reason.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "would leave the row's envelope"
            )
        );
        Assert.Equal(
            expected: before,
            actual: fixture.DefinitionBytes()
        );
    }
    [Fact]
    public void AnUpsertPastARowsCapacityIsRefused() {
        var limited = new WorldStateRow(
            Name(value: "limited"),
            CellKind.Int,
            Capacity: 1,
            Cells: [Cell(
                    key: "a",
                    value: 5
                )]
        );
        using var fixture = Fixtures.FreshServer(definition: Document([limited]));
        var before = fixture.DefinitionBytes();
        var refusals = new List<string>();

        fixture.Server.EchoTap = echo => { if (echo.Rejected) { refusals.Add(item: echo.Message); } };

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: WorldPrincipal.Console,
            Row: "limited",
            Key: "b",
            Value: 1,
            Kind: WorldDocumentWriteKind.Set
        ));
        fixture.Step();

        Assert.Contains(
            collection: refusals,
            filter: reason => reason.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "would mint past capacity"
            )
        );
        Assert.Equal(
            expected: before,
            actual: fixture.DefinitionBytes()
        );
    }
    [Fact]
    public void ABoardCellOutsideItsTopologyIsRefused() {
        var board = new WorldStateRow(
            Name(value: "board"),
            CellKind.Int,
            Domain: new StateDomain.CellsOf("map")
        );
        using var fixture = Fixtures.FreshServer(definition: Document(
            [board],
            lattices: [new LatticeTopology.Grid(
                    "map",
                    new DocumentVector3(
                        x: 0,
                        y: 0,
                        z: 0
                    ),
                    1,
                    2,
                    2
                )]
        ));
        var before = fixture.DefinitionBytes();
        var refusals = new List<string>();

        fixture.Server.EchoTap = echo => { if (echo.Rejected) { refusals.Add(item: echo.Message); } };

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: WorldPrincipal.Console,
            Row: "board",
            Key: "9",
            Value: 1,
            Kind: WorldDocumentWriteKind.Set
        ));
        fixture.Step();

        Assert.Contains(
            collection: refusals,
            filter: reason => reason.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "row 'board' holds no cell '9'; a write never mints a key"
            )
        );
        Assert.Equal(
            expected: before,
            actual: fixture.DefinitionBytes()
        );
    }
    [Fact]
    public void ATextCellOverTheMaxLengthIsRefused() {
        var notes = new WorldStateRow(
            Name(value: "notes"),
            CellKind.Text,
            Cells: [new StateCell(
                    WorldStateRow.SlotKey,
                    CellValue.Text(value: "")
                )]
        );
        using var fixture = Fixtures.FreshServer(definition: Document([notes]));
        var before = fixture.DefinitionBytes();
        var refusals = new List<string>();

        fixture.Server.EchoTap = echo => { if (echo.Rejected) { refusals.Add(item: echo.Message); } };

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: WorldPrincipal.Console,
            Row: "notes",
            Key: WorldStateRow.SlotKey.Value,
            Value: 0,
            Kind: WorldDocumentWriteKind.Set,
            Text: new string(
                c: 'x',
                count: (StateCapacity.MaxTextValueLength + 1)
            )
        ));
        fixture.Step();

        Assert.Contains(
            collection: refusals,
            filter: reason => reason.Contains(
                comparisonType: StringComparison.Ordinal,
                value: $"row 'notes' cell '{WorldStateRow.SlotKey.Value}' would store {(StateCapacity.MaxTextValueLength + 1)} characters, past the {StateCapacity.MaxTextValueLength}-character limit"
            )
        );
        Assert.Equal(
            expected: before,
            actual: fixture.DefinitionBytes()
        );
    }
    [Fact]
    public void ADuplicateKeyInAnOrderedZoneIsRefusedTheSameWayByBothWalks() {
        // Compose never mints this shape (an upsert always replaces an existing key in place),
        // so this targets TryValidateTouchedStateRows directly with a candidate a state mutation could never
        // legitimately compose, proving it refuses the same malformed row the whole-document walk refuses.
        var domain = new WorldStateRow(
            Name(value: "cards"),
            CellKind.Int,
            Capacity: 1,
            Cells: [Cell("c1")]
        );
        var zone = new WorldStateRow(
            Name(value: "zone"),
            CellKind.Bool,
            Domain: new StateDomain.KeysOf(
                Name(value: "cards"),
                Ordered: true
            ),
            Cells: [Cell(
                    key: "c1",
                    value: 1,
                    kind: CellKind.Bool
                ), Cell(
                    key: "c1",
                    value: 1,
                    kind: CellKind.Bool
                )]
        );
        var definition = Document([domain, zone]);

        Assert.False(condition: WorldDefinitionValidator.TryValidateTouchedStateRows(
            definition: definition,
            reason: out var touchedReason,
            rowNames: ["zone"]
        ));
        Assert.Contains(
            actualString: touchedReason,
            expectedSubstring: "is duplicated"
        );
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: definition,
            reason: out var wholeDocumentReason
        ));
        Assert.Contains(
            actualString: wholeDocumentReason,
            expectedSubstring: "is duplicated"
        );
    }
    [Fact]
    public void AddonVectorWrite_RefusedByName() {
        var payload = System.Text.Encoding.UTF8.GetBytes(s: """{"name":"memories","kind":"vector","value":"AQID"}""");
        var decoded = Puck.World.Addons.WorldAddonMutationDecoder.TryDecode(
            kindOrdinal: 46,
            section: WorldSection.State,
            payload: payload,
            principal: WorldPrincipal.Addon(name: "guest"),
            mutation: out var mutation,
            error: out var error
        );

        Assert.False(condition: decoded);
        Assert.Null(@object: mutation);
        Assert.Contains(
            actualString: error,
            comparisonType: StringComparison.OrdinalIgnoreCase,
            expectedSubstring: "vector"
        );
    }
    [Fact]
    public void AddonVectorKeyedWrite_RefusedByName() {
        var payload = System.Text.Encoding.UTF8.GetBytes(s: """{"name":"memories","kind":"vector","cells":[{"key":"v1","value":"AQID"}]}""");
        var decoded = Puck.World.Addons.WorldAddonMutationDecoder.TryDecode(
            kindOrdinal: 46,
            section: WorldSection.State,
            payload: payload,
            principal: WorldPrincipal.Addon(name: "guest"),
            mutation: out var mutation,
            error: out var error
        );

        Assert.False(condition: decoded);
        Assert.Null(@object: mutation);
        Assert.Contains(
            actualString: error,
            comparisonType: StringComparison.OrdinalIgnoreCase,
            expectedSubstring: "vector"
        );
    }

    private static int CountCells(WorldMutation mutation) => ((mutation is WorldMutation.Batch batch)
        ? batch.Mutations.Sum(selector: CountCells)
        : 1
    );
    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static StateCell Cell(string key, long value = 1, CellKind kind = CellKind.Int) => new(
        Key: Name(value: key),
        Value: ((kind == CellKind.Bool) ? CellValue.Bool(value: (value != 0L)) : CellValue.Int(value: value))
    );
    // A submitted row declaration composes through an arena over the document it declares, and reaches it before
    // any validator has seen it. A board over a topology the document does not declare is a section the arena cannot
    // lay out at all, so the apply door names the row rather than throwing out of the tick.
    [Fact]
    public void AWholeRowDeclarationTheArenaCannotLayOutIsRefusedByName() {
        using var fixture = Fixtures.FreshServer(definition: Document([new WorldStateRow(
                Name(value: "keep"),
                CellKind.Int,
                Cells: [new StateCell(
                        WorldStateRow.SlotKey,
                        CellValue.Int(value: 0)
                    )]
            )]));
        var before = fixture.DefinitionBytes();
        var refusals = new List<string>();

        fixture.Server.EchoTap = echo => { if (echo.Rejected) { refusals.Add(item: echo.Message); } };

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateRow(
            Principal: WorldPrincipal.Console,
            Row: new WorldStateRow(
                Name(value: "board"),
                CellKind.Int,
                Domain: new StateDomain.CellsOf(
                    "absent",
                    Empty: -1
                )
            )
        ));
        fixture.Step();

        Assert.Contains(
            collection: refusals,
            filter: reason => reason.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "which the section does not declare"
            )
        );
        Assert.Equal(
            expected: before,
            actual: fixture.DefinitionBytes()
        );
    }
    /// <summary>A lattice row's cells are its topology's, so removing one is refused by name on the host rather than
    /// leaving the board a position short. CONTROL: the same removal against a keyed row applies.</summary>
    [Fact]
    public void RemovingACellFromALatticeRowIsRefusedByName() {
        var board = new WorldStateRow(
            Name(value: "board"),
            CellKind.Int,
            Domain: new StateDomain.CellsOf("map"),
            Cells: [new StateCell(
                    Key: Name(value: "0"),
                    Value: CellValue.Int(value: 0L)
                )]
        );
        var pile = new WorldStateRow(
            Name(value: "pile"),
            CellKind.Int,
            Domain: new StateDomain.Keys(),
            Capacity: 4,
            Cells: [new StateCell(
                    Key: Name(value: "a"),
                    Value: CellValue.Int(value: 0L)
                )]
        );

        using var fixture = Fixtures.FreshServer(definition: Document(
            [board, pile],
            lattices: [new LatticeTopology.Grid(
                    "map",
                    new DocumentVector3(
                        x: 0,
                        y: 0,
                        z: 0
                    ),
                    1,
                    2,
                    2
                )]
        ));
        var refusals = new List<string>();

        fixture.Server.EchoTap = echo => { if (echo.Rejected) { refusals.Add(item: echo.Message); } };

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.RemoveStateCell(
            Principal: WorldPrincipal.Console,
            Row: "board",
            Key: "0"
        ));
        fixture.Step();

        Assert.Contains(
            collection: refusals,
            filter: static reason => reason.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "row 'board' is not a keyed or ordered row"
            )
        );

        // The control: the same mutation against a keyed row is admitted, so the refusal is about the row's shape.
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.RemoveStateCell(
            Principal: WorldPrincipal.Console,
            Row: "pile",
            Key: "a"
        ));
        fixture.Step();

        Assert.Empty(collection: (WorldDefinitionRows.FindStateRow(
            rows: fixture.Server.Definition.State,
            name: "pile"
        )!.Cells ?? []));
    }
    private static WorldDefinition Document(WorldStateRow[] rows, LatticeTopology[]? lattices = null) => Fixtures.BuildDocument() with {
        StateRaw = new WorldStateSection(
        World: rows,
        Lattices: (lattices ?? [])
    ),
    };
}
