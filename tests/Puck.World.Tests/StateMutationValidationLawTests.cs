using Puck.Assets.Documents;
using Puck.World.Protocol;
using Xunit;
using static Puck.World.Tests.SolitaireFixtures;

namespace Puck.World.Tests;

/// <summary>Pins <see cref="WorldDefinitionValidator.TryValidateTouchedStateRows"/>: a state mutation is refused
/// for exactly the row-local and cross-row reasons the whole-document walk would refuse it for, checking only the
/// rows it touched, and a real deal allocates a small, bounded amount per mutation rather than the whole document's
/// worth.</summary>
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
    // Baseline measured on this same fixture and deal with routing reverted to whole-document validation for every
    // mutation kind (WorldDefinitionValidator.TryValidateLocally unconditionally): 972,343 bytes/mutation over 140
    // applied mutations. Touched-row validation drops that to roughly 141,500 bytes/mutation — most of a Klondike
    // deal's applied mutations are rule-fired StateTransform.Transfer effects, and WorldServer.Install recompiles
    // the whole rules/interactions/tables/search section on every one of those regardless of mutation kind (only a
    // value-only UpsertStateCell against an unchanged StateCatalog takes the no-recompile InstallRuntimeStateValue
    // path) — carrying validated compilation into installation is deferred, so that recompile, not validation, is
    // what the remaining allocation buys. GC.GetTotalAllocatedBytes is process-wide, so it is unusable under this
    // suite's parallel test run (a sibling test's concurrent allocation on another thread lands in the same
    // window); GetAllocatedBytesForCurrentThread charges only the calling thread, which this synchronous body never
    // leaves.
    [Fact]
    public void KlondikeDealAllocatesFarLessThanWholeDocumentValidation() {
        using var fixture = Fixtures.FreshServer(definition: Game(game: "solitaireKlondike"));
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.Console, Row: "solitaireKlondike", Key: "option", Value: 1, Kind: WorldDocumentWriteKind.Set));
        fixture.Step();

        var applied = 0;
        fixture.Server.MutationJournalTap = (_, _) => applied++;

        var before = GC.GetAllocatedBytesForCurrentThread();
        Request(f: fixture, game: "solitaireKlondike", action: 1);
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(expected: 1, actual: Value(f: fixture, game: "solitaireKlondike", key: "result"));
        Assert.True(condition: (applied > 0), userMessage: "the deal must apply at least one mutation to measure a per-mutation average");

        var allocated = (after - before);
        var perMutation = (allocated / (double)applied);

        output.WriteLine(message: $"solitaireKlondike deal: {applied} applied mutations, {allocated} bytes allocated, {perMutation:F0} bytes/mutation (whole-document baseline: 972,343 bytes/mutation)");

        Assert.True(condition: (perMutation < (250 * 1024)), userMessage: $"expected well under the 972,343 bytes/mutation whole-document baseline; measured {perMutation:F0} bytes/mutation over {applied} mutations");
    }

    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static StateCell Cell(string key, long value = 1) => new(Key: Name(value: key), Value: value);
    private static WorldDefinition Document(WorldStateRow[] rows, LatticeTopology[]? lattices = null) => Fixtures.BuildDocument() with {
        StateRaw = new WorldStateSection(World: rows, Lattices: (lattices ?? [])),
    };
}
