using Puck.Assets.Documents;
using Puck.World.Protocol;
using Xunit;
using static Puck.World.Tests.SolitaireFixtures;

namespace Puck.World.Tests;

/// <summary>Pins <see cref="WorldDefinitionValidator.TryValidateTouchedStateRows"/>: a state mutation is refused
/// for exactly the row-local and cross-row reasons the whole-document walk would refuse it for, checking only the
/// rows it touched, and a real deal allocates a small, bounded amount per rule-written cell rather than the whole
/// document's worth.</summary>
[Collection(AllocationCollection.Name)]
public sealed class StateMutationValidationLawTests(ITestOutputHelper output) {
    [Fact]
    public void AKeysOfZoneRefusesAKeyOutsideItsTokenDomain() {
        // A Transfer can never produce this shape (WorldStateTransforms refuses two zones over different domains
        // outright), so the live door a state mutation actually reaches this through is a direct cell upsert — the
        // one write compose never checks against the zone's own domain.
        var domain = new WorldStateRow(Name("cards"), CellKind.Int, Capacity: 2, Cells: [Cell("c1"), Cell("c2")]);
        var zone = new WorldStateRow(Name("zone"), CellKind.Bool, Domain: new StateDomain.KeysOf(Name("cards"), Ordered: true));
        using var fixture = Fixtures.FreshServer(definition: Document([domain, zone]));
        var before = fixture.DefinitionBytes();
        var refusals = new List<string>();
        fixture.Server.EchoTap = echo => { if (echo.Rejected) { refusals.Add(item: echo.Message); } };

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.Console, Row: "zone", Key: "ghost", Value: 1, Kind: WorldDocumentWriteKind.Set));
        fixture.Step();

        Assert.Contains(collection: refusals, filter: reason => reason.Contains(value: "outside token domain", comparisonType: StringComparison.Ordinal));
        Assert.Equal(expected: before, actual: fixture.DefinitionBytes());
    }
    [Fact]
    public void RemovingATokenStillReferencedByAKeysOfZoneIsRefused() {
        // The zone is never named by the mutation — only its domain row 'cards' is — so the touched-row walk must
        // queue 'zone' itself once it sees 'cards' is touched, or the removal leaves 'zone' holding a key outside
        // its own domain, a shape the whole-document walk would have refused.
        var domain = new WorldStateRow(Name("cards"), CellKind.Int, Capacity: 2, Cells: [Cell("c1"), Cell("c2")]);
        var zone = new WorldStateRow(Name("zone"), CellKind.Bool, Domain: new StateDomain.KeysOf(Name("cards"), Ordered: true), Cells: [Cell("c1", 1)]);
        using var fixture = Fixtures.FreshServer(definition: Document([domain, zone]));
        var before = fixture.DefinitionBytes();
        var refusals = new List<string>();
        fixture.Server.EchoTap = echo => { if (echo.Rejected) { refusals.Add(item: echo.Message); } };

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.RemoveStateCell(Principal: WorldPrincipal.Console, Row: "cards", Key: "c1"));
        fixture.Step();

        Assert.Contains(collection: refusals, filter: reason => reason.Contains(value: "outside token domain", comparisonType: StringComparison.Ordinal));
        Assert.Equal(expected: before, actual: fixture.DefinitionBytes());
    }
    [Fact]
    public void ABoolCellRefusesAValueOtherThanZeroOrOne() {
        var flag = new WorldStateRow(Name("flag"), CellKind.Bool, Cells: [new StateCell(WorldStateRow.SlotKey, 0)]);
        using var fixture = Fixtures.FreshServer(definition: Document([flag]));
        var before = fixture.DefinitionBytes();
        var refusals = new List<string>();
        fixture.Server.EchoTap = echo => { if (echo.Rejected) { refusals.Add(item: echo.Message); } };

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.Console, Row: "flag", Key: WorldStateRow.SlotKey.Value, Value: 2, Kind: WorldDocumentWriteKind.Set));
        fixture.Step();

        Assert.Contains(collection: refusals, filter: reason => reason.Contains(value: "must be 0 or 1", comparisonType: StringComparison.Ordinal));
        Assert.Equal(expected: before, actual: fixture.DefinitionBytes());
    }
    [Fact]
    public void AnUpsertPastARowsCapacityIsRefused() {
        var limited = new WorldStateRow(Name("limited"), CellKind.Int, Capacity: 1, Cells: [Cell("a", 5)]);
        using var fixture = Fixtures.FreshServer(definition: Document([limited]));
        var before = fixture.DefinitionBytes();
        var refusals = new List<string>();
        fixture.Server.EchoTap = echo => { if (echo.Rejected) { refusals.Add(item: echo.Message); } };

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.Console, Row: "limited", Key: "b", Value: 1, Kind: WorldDocumentWriteKind.Set));
        fixture.Step();

        Assert.Contains(collection: refusals, filter: reason => reason.Contains(value: "exceeds its capacity", comparisonType: StringComparison.Ordinal));
        Assert.Equal(expected: before, actual: fixture.DefinitionBytes());
    }
    [Fact]
    public void ABoardCellOutsideItsTopologyIsRefused() {
        var board = new WorldStateRow(Name("board"), CellKind.Int, Domain: new StateDomain.CellsOf("map"));
        using var fixture = Fixtures.FreshServer(definition: Document([board], lattices: [new LatticeTopology.Grid("map", new DocumentVector3(0, 0, 0), 1, 2, 2)]));
        var before = fixture.DefinitionBytes();
        var refusals = new List<string>();
        fixture.Server.EchoTap = echo => { if (echo.Rejected) { refusals.Add(item: echo.Message); } };

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.Console, Row: "board", Key: "9", Value: 1, Kind: WorldDocumentWriteKind.Set));
        fixture.Step();

        Assert.Contains(collection: refusals, filter: reason => reason.Contains(value: "canonical topology keys", comparisonType: StringComparison.Ordinal));
        Assert.Equal(expected: before, actual: fixture.DefinitionBytes());
    }
    [Fact]
    public void ATextCellOverTheMaxLengthIsRefused() {
        var notes = new WorldStateRow(Name("notes"), CellKind.Text, Cells: [new StateCell(WorldStateRow.SlotKey, 0, Text: "")]);
        using var fixture = Fixtures.FreshServer(definition: Document([notes]));
        var before = fixture.DefinitionBytes();
        var refusals = new List<string>();
        fixture.Server.EchoTap = echo => { if (echo.Rejected) { refusals.Add(item: echo.Message); } };

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.Console, Row: "notes", Key: WorldStateRow.SlotKey.Value, Value: 0, Kind: WorldDocumentWriteKind.Set, Text: new string(c: 'x', count: (StateCapacity.MaxTextValueLength + 1))));
        fixture.Step();

        Assert.Contains(collection: refusals, filter: reason => reason.Contains(value: "exceeds the maximum of", comparisonType: StringComparison.Ordinal));
        Assert.Equal(expected: before, actual: fixture.DefinitionBytes());
    }
    [Fact]
    public void ADuplicateKeyInAnOrderedZoneIsRefusedTheSameWayByBothWalks() {
        // Compose never mints this shape (an upsert always replaces an existing key in place — StateCellWriter),
        // so this targets TryValidateTouchedStateRows directly with a candidate a state mutation could never
        // legitimately compose, proving it refuses the same malformed row the whole-document walk refuses.
        var domain = new WorldStateRow(Name("cards"), CellKind.Int, Capacity: 1, Cells: [Cell("c1")]);
        var zone = new WorldStateRow(Name("zone"), CellKind.Bool, Domain: new StateDomain.KeysOf(Name("cards"), Ordered: true), Cells: [Cell("c1", 1), Cell("c1", 1)]);
        var definition = Document([domain, zone]);

        Assert.False(condition: WorldDefinitionValidator.TryValidateTouchedStateRows(definition: definition, rowNames: ["zone"], reason: out var touchedReason));
        Assert.Contains(expectedSubstring: "is duplicated", actualString: touchedReason);
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var wholeDocumentReason));
        Assert.Contains(expectedSubstring: "is duplicated", actualString: wholeDocumentReason);
    }
    // Charges the calling thread only: the suite runs tests in parallel, so a process-wide counter would fold a
    // sibling test into this window. The denominator is rule-written cells, not journal entries, so a tick that
    // folds many effects into one entry does not divide by a shrinking count and hide what this law pins. Klondike's
    // own authored rules never queue more than one cross-row write per tick (a deal spends most of its ticks on one
    // or two cells apiece), so this bound is dominated by the once-per-tick install pipeline's own fixed cost for a
    // document this size, not by how many members one cross-row replay composes — ManyCrossRowWritesInOneTickShareOneWorkspaceCopy
    // below isolates that cost directly, and CellsFoldIntoOneInstallWithBoundedPerCellCost isolates the batch
    // compose's own cost on a tick that writes many frame-fast cells at once.
    [Fact]
    public void KlondikeDealAllocatesFarLessThanWholeDocumentValidation() {
        using var fixture = Fixtures.FreshServer(definition: Game(game: "solitaireKlondike"));
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.Console, Row: "solitaireKlondike", Key: "option", Value: 1, Kind: WorldDocumentWriteKind.Set));
        fixture.Step();

        var cells = 0;
        fixture.Server.MutationJournalTap = (_, mutation) => cells += CountCells(mutation: mutation);

        var before = GC.GetAllocatedBytesForCurrentThread();
        Request(f: fixture, game: "solitaireKlondike", action: 1);
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(expected: 1, actual: Value(f: fixture, game: "solitaireKlondike", key: "result"));
        Assert.True(condition: (cells > 0), userMessage: "the deal must apply at least one rule-written cell to measure a per-cell average");

        var allocated = (after - before);
        var perCell = (allocated / (double)cells);

        output.WriteLine(message: $"solitaireKlondike deal: {cells} rule-written cells, {allocated} bytes allocated, {perCell:F0} bytes/cell");

        Assert.True(condition: (perCell < (20 * 1024)), userMessage: $"expected under 20 KiB per rule-written cell; measured {perCell:F0} bytes/cell over {cells} cells");
    }
    // A tick whose rule writes many independent cells through separate top-level effects (never a transaction —
    // proving the fold applies to ordinary standalone effects too) folds into one install: one admission, one
    // touched-row validation, one journal entry, one delivery, and one batch compose that copies the row list and
    // the definition once and walks the document graph once for the rows anything is bound to. The bound holds
    // the whole tick — rule evaluation, the frame, the fold, validation, journal, echo — to under 4 KiB per cell.
    [Fact]
    public void CellsFoldIntoOneInstallWithBoundedPerCellCost() {
        const int rowCount = 32;
        var rows = new WorldStateRow[rowCount];
        var effects = new ActionEffect[rowCount];

        for (var index = 0; index < rowCount; index++) {
            rows[index] = new WorldStateRow(Name($"counter{index}"), CellKind.Int, Min: 0, Max: 1_000_000, Cells: [new StateCell(WorldStateRow.SlotKey, 0)]);
            effects[index] = new ActionEffect.SetState(State: $"counter{index}", Expression: new ValueExpression([new ValueToken.Constant(1m)]));
        }

        var definition = Document(rows) with { Rules = [new WorldRule(Name("advance"), effects, Mode: ActionTriggerMode.Level)] };
        using var fixture = Fixtures.FreshServer(definition: definition);

        var installs = 0;
        var cells = 0;
        fixture.Server.MutationJournalTap = (_, mutation) => { installs++; cells += CountCells(mutation: mutation); };

        var before = GC.GetAllocatedBytesForCurrentThread();
        fixture.Step();
        var allocated = (GC.GetAllocatedBytesForCurrentThread() - before);

        Assert.Equal(expected: 1, actual: installs);
        Assert.Equal(expected: rowCount, actual: cells);

        var perCell = (allocated / (double)cells);

        output.WriteLine(message: $"{rowCount} independent cells, one tick: {installs} install, {allocated} bytes allocated, {perCell:F0} bytes/cell");

        Assert.True(condition: (perCell < (4 * 1024)), userMessage: $"expected under 4 KiB per rule-written cell; measured {perCell:F0} bytes/cell over {cells} cells");
    }
    // The shape KlondikeDealAllocatesFarLessThanWholeDocumentValidation's own comment describes but does not itself
    // reach: many cross-row writes (text, so each mints through TryApplyCrossRowStateMutation rather than the
    // frame's numeric array) queued in the SAME tick. Before routing that replay through the batch workspace, the
    // Nth cross-row write recomposed the whole document once per already-queued member — quadratic in the tick's own
    // cross-row count; the workspace makes it one shared row-list copy per replay instead.
    [Fact]
    public void ManyCrossRowWritesInOneTickShareOneWorkspaceCopy() {
        const int rowCount = 32;
        var rows = new WorldStateRow[rowCount];
        var effects = new ActionEffect[rowCount];

        for (var index = 0; index < rowCount; index++) {
            rows[index] = new WorldStateRow(Name($"text{index}"), CellKind.Text, Cells: [new StateCell(WorldStateRow.SlotKey, 0, Text: "")]);
            effects[index] = new ActionEffect.SetState(State: $"text{index}", Text: "written");
        }

        var definition = Document(rows) with { Rules = [new WorldRule(Name("advance"), effects, Mode: ActionTriggerMode.Level)] };
        using var fixture = Fixtures.FreshServer(definition: definition);

        var installs = 0;
        var cells = 0;
        fixture.Server.MutationJournalTap = (_, mutation) => { installs++; cells += CountCells(mutation: mutation); };

        var before = GC.GetAllocatedBytesForCurrentThread();
        fixture.Step();
        var allocated = (GC.GetAllocatedBytesForCurrentThread() - before);

        Assert.Equal(expected: 1, actual: installs);
        Assert.Equal(expected: rowCount, actual: cells);
        for (var index = 0; index < rowCount; index++) {
            Assert.Equal(expected: "written", actual: Row(f: fixture, name: $"text{index}").Cells!.Single().Text);
        }

        var perCell = (allocated / (double)cells);

        output.WriteLine(message: $"{rowCount} cross-row text cells, one tick: {installs} install, {allocated} bytes allocated, {perCell:F0} bytes/cell");

        Assert.True(condition: (perCell < (80 * 1024)), userMessage: $"expected under 80 KiB per cross-row cell; measured {perCell:F0} bytes/cell over {cells} cells");
    }
    private static int CountCells(WorldMutation mutation) => (mutation is WorldMutation.Batch batch ? batch.Mutations.Sum(selector: CountCells) : 1);

    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static StateCell Cell(string key, long value = 1) => new(Key: Name(value: key), Value: value);
    private static WorldDefinition Document(WorldStateRow[] rows, LatticeTopology[]? lattices = null) => Fixtures.BuildDocument() with {
        StateRaw = new WorldStateSection(World: rows, Lattices: (lattices ?? [])),
    };
}
