using System.Text.Json;
using System.Numerics;
using Puck.Assets.Documents;
using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>A recipient's write into a row a document value binds is validated only as far as the written row
/// reaches: a unit it does not name, left unable to read its own row by an authority write that skipped binding
/// validation, answers nothing to it (decision 3a's H2). The hidden units are found in the document by the same walk
/// the server's binding validation uses (<see cref="WorldStateDocumentValues.CollectReferencedRows(object, ISet{string})"/>),
/// so a bound unit the fixture gains is probed without a list of its own.</summary>
public sealed class BoundWriteValidationNoninterferenceLawTests {
    private static readonly Principal Writer = Principal.Seat(slot: 1);

    // A public text row a placement binds, and a row withheld from the writer that another placement binds.
    private static WorldDefinition Document() {
        var rows = new List<WorldStateRow> {
            new(CellName.Parse(candidate: "spot"), CellKind.Text, Cells: [new StateCell(StateRow.SlotKey, CellValue.Text(value: "[0,0,0]"))]),
            new(CellName.Parse(candidate: "vault"), CellKind.Text, Visibility: new StateVisibility(Readers: []),
                Cells: [new StateCell(StateRow.SlotKey, CellValue.Text(value: "[1,0,0]"))]),
        };
        var document = Fixtures.BuildDocument().WithWorldState(rows) with {
            CreationsRaw = [CreationFixtures.Sphere(id: "piece", scale: 0.2f)],
            PlacementsRaw = new WorldPlacementsSection([
                new WorldPlacement("marker", "piece", Bound("state.spot"), 0, 1),
                new WorldPlacement("ghost", "piece", Bound("state.vault"), 0, 1),
            ], Policy: Fixtures.StandardAuthoring),
        };
        Assert.True(WorldStateDocumentValues.TryResolve(document, out document, out var reason), reason);
        return document;
    }
    private static DocumentVector3 Bound(string reference) => JsonSerializer.Deserialize<DocumentVector3>($"\"{reference}\"", DocumentJsonOptions.Shared)!;

    // The rows hidden units bind: every row a placement's values bind that the writer cannot read.
    private static IReadOnlyList<string> HiddenUnitRows(WorldDefinition document) {
        var view = WorldRecipientView.ForComposition(document, Writer);
        var rows = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var placement in document.Placements) {
            var bound = new HashSet<string>(StringComparer.Ordinal);
            WorldStateDocumentValues.CollectReferencedRows(placement, bound);
            rows.UnionWith(bound.Where(row => !view.Admits(row)));
        }
        return [.. rows];
    }

    // What the writer, and then the operator, are told for one write into the public bound row, with the hidden row
    // left holding text its binding reads or text it cannot.
    private static (string Recipient, string Operator) Answers(WorldDefinition document, string hidden, bool readable) {
        using var fixture = Fixtures.FreshServer(document);
        fixture.Server.Grant(new WorldGrant(Grantee: Writer, Capability: WorldCapability.Mutate, Subject: GrantSubject.Section(WorldSection.State),
            Exclusive: false, Budget: 64, KindMask: WorldMutationKindCatalog.KindsOf(WorldSection.State)), Principal.Console);
        fixture.Server.Grant(new WorldGrant(Grantee: Writer, Capability: WorldCapability.Edit, Subject: GrantSubject.State("spot"),
            Exclusive: false, KindMask: WorldMutationKindCatalog.KindsOf(WorldSection.State)), Principal.Console);
        if (!readable) {
            // An authority write that does not revalidate bindings: what a path that skips it leaves behind.
            var arena = fixture.Server.Arena;
            Assert.True(arena.Catalog.TryResolve(StateLane.Document, hidden, out var row));
            Assert.True(arena.Keys.TryResolve(StateRow.SlotKey, out var key));
            var mark = fixture.Server.BeginStateTransaction();
            Assert.True(arena.TryWriteText(row.Ordinal, key, "not a vector", out var reason), reason);
            fixture.Server.CommitStateTransaction(mark);
        }
        static WorldMutation Write(Principal principal, string text) =>
            new WorldMutation.UpsertStateCell(principal, "spot", StateRow.SlotKey.Value, 0, WorldDocumentWriteKind.Set, Text: text);
        var recipient = fixture.SubmitMutation(Write(Writer, "[2,0,0]"));
        var authority = fixture.SubmitMutation(Write(Principal.Console, "[3,0,0]"));
        return ($"{recipient.Decision} {recipient.Code} {recipient.Detail}", $"{authority.Decision} {authority.Code}");
    }

    // A placement binding the written public row and a withheld one is a unit the write reaches, so the write is
    // refused; every door answers it as it answers a write into a withheld row: the neutral refusal naming the row
    // the writer wrote, through a mutation and through a typed cell write alike.
    [Fact]
    public void AWriteReachingAUnitThatReadsAWithheldRowIsRefusedAsAWithheldRowIs() {
        var document = Document() with {
            PlacementsRaw = new WorldPlacementsSection([
                new WorldPlacement("marker", "piece", Bound("state.spot"), 0, 1, Mirror: new WorldPlacementMirror(Bound("state.vault"), 0f)),
            ], Policy: Fixtures.StandardAuthoring),
        };
        Assert.True(WorldStateDocumentValues.TryResolve(document, out document, out var reason), reason);
        using var fixture = Fixtures.FreshServer(document);
        foreach (var row in new[] { "spot", "vault" }) {
            fixture.Server.Grant(new WorldGrant(Grantee: Writer, Capability: WorldCapability.Edit, Subject: GrantSubject.State(row),
                Exclusive: false, Budget: 64, KindMask: WorldMutationKindCatalog.KindsOf(WorldSection.State)), Principal.Console);
        }
        fixture.Server.Grant(new WorldGrant(Grantee: Writer, Capability: WorldCapability.Mutate, Subject: GrantSubject.Section(WorldSection.State),
            Exclusive: false, Budget: 64, KindMask: WorldMutationKindCatalog.KindsOf(WorldSection.State)), Principal.Console);
        var reached = fixture.SubmitMutation(new WorldMutation.UpsertStateCell(Writer, "spot", StateRow.SlotKey.Value, 0, WorldDocumentWriteKind.Set, Text: "[2,0,0]"));
        var withheld = fixture.SubmitMutation(new WorldMutation.UpsertStateCell(Writer, "vault", StateRow.SlotKey.Value, 0, WorldDocumentWriteKind.Set, Text: "[2,0,0]"));

        Assert.Equal(WorldRecipientMutations.NotDisclosedCode, reached.Code);
        Assert.Equal(withheld.Code, reached.Code);
        Assert.Equal(withheld.Detail.Replace("'vault'", "'spot'", StringComparison.Ordinal), reached.Detail);

        var arena = fixture.Server.Arena;
        Assert.True(arena.Catalog.TryResolve(StateLane.Document, "spot", out var spot));
        Assert.True(arena.Keys.TryResolve(StateRow.SlotKey, out var slot));
        Assert.Equal(WorldCellWriteVerdict.NotDisclosed, fixture.Server.WriteStateCell(Writer, spot, slot, 0, WorldDocumentWriteKind.Set, "[2,0,0]"));
    }

    // The bound-write check reads only the holders of the written row: a malformed reference on another unit, which
    // cannot name the row, is not its business.
    [Fact]
    public void ACheckOfOneBoundRowReadsNoOtherHolder() {
        var document = Document() with {
            PlacementsRaw = new WorldPlacementsSection([
                new WorldPlacement("marker", "piece", Bound("state.spot"), 0, 1),
                new WorldPlacement("odd", "piece", Bound("state."), 0, 1),
            ], Policy: Fixtures.StandardAuthoring),
        };

        Assert.True(WorldStateDocumentValues.TryCheckBound(document, "spot", WorldStateReader.Arena(document), ArenaTime.Origin, out var reason), reason);
    }

    [Fact]
    public void ARecipientWriteRunsEveryCheckOfTheUnitItBindsNotItsGeometryAlone() {
        // A mirror whose normal a public row binds: a zero normal reads as a vector, so only the placement's own mirror
        // check, which no geometry check makes, refuses it, exactly as the authority's whole validation does.
        var document = Document() with {
            PlacementsRaw = new WorldPlacementsSection([
                new WorldPlacement("marker", "piece", new DocumentVector3(System.Numerics.Vector3.Zero), 0, 1, Mirror: new WorldPlacementMirror(Bound("state.spot"), 0f)),
            ], Policy: Fixtures.StandardAuthoring),
        };
        Assert.True(WorldStateDocumentValues.TryResolve(document with { StateRaw = document.StateRaw! with {
            World = [new(CellName.Parse(candidate: "spot"), CellKind.Text, Cells: [new StateCell(StateRow.SlotKey, CellValue.Text(value: "[1,0,0]"))])],
        } }, out document, out var resolved), resolved);
        using var fixture = Fixtures.FreshServer(document);
        fixture.Server.Grant(new WorldGrant(Grantee: Writer, Capability: WorldCapability.Mutate, Subject: GrantSubject.Section(WorldSection.State),
            Exclusive: false, Budget: 64, KindMask: WorldMutationKindCatalog.KindsOf(WorldSection.State)), Principal.Console);
        fixture.Server.Grant(new WorldGrant(Grantee: Writer, Capability: WorldCapability.Edit, Subject: GrantSubject.State("spot"),
            Exclusive: false, KindMask: WorldMutationKindCatalog.KindsOf(WorldSection.State)), Principal.Console);

        var refused = fixture.SubmitMutation(new WorldMutation.UpsertStateCell(Writer, "spot", StateRow.SlotKey.Value, 0, WorldDocumentWriteKind.Set, Text: "[0,0,0]"));
        var applied = fixture.SubmitMutation(new WorldMutation.UpsertStateCell(Writer, "spot", StateRow.SlotKey.Value, 0, WorldDocumentWriteKind.Set, Text: "[0,1,0]"));

        Assert.NotEqual(WorldMutationDecision.Applied, refused.Decision);
        Assert.Equal(WorldMutationDecision.Applied, applied.Decision);
    }
    [Fact]
    public void AHiddenUnitLeftInvalidAnswersNothingToARecipientWriteIntoAnotherBoundRow() {
        var hidden = HiddenUnitRows(Document());
        Assert.NotEmpty(hidden);
        foreach (var row in hidden) {
            var valid = Answers(Document(), row, readable: true);
            var invalid = Answers(Document(), row, readable: false);

            Assert.Equal(valid.Recipient, invalid.Recipient);
            // The control: the recipient's write is a real one, not refused in both worlds.
            Assert.StartsWith(nameof(WorldMutationDecision.Applied), valid.Recipient, StringComparison.Ordinal);
            // The hidden unit's state is real: the authority's whole-document validation sees it, as the recipient's
            // validation did when it validated the whole document.
            Assert.NotEqual(valid.Operator, invalid.Operator);
        }
    }

    [Fact]
    public void ARecipientScreenWriteDoesNotReadAnotherUnitsBinding() {
        var document = Document();
        document = document with {
            PlacementRowsRaw = [document.Placements[1]],
            ScreensRaw = [new WorldScreen(Index: 0, Origin: Bound("state.spot"), Right: Vector3.UnitX, Up: Vector3.UnitY,
                HalfWidth: 1f, HalfHeight: 1f, HalfDepth: 0.1f, Round: 0f, Source: new WorldScreenSource.None(), Route: WorldScreenRoute.Passive)],
        };
        Assert.True(WorldStateDocumentValues.TryResolve(document, out document, out var reason), reason);
        var valid = Answers(document, "vault", readable: true);
        var invalid = Answers(document, "vault", readable: false);

        Assert.StartsWith(nameof(WorldMutationDecision.Applied), valid.Recipient, StringComparison.Ordinal);
        Assert.Equal(valid.Recipient, invalid.Recipient);
        Assert.NotEqual(valid.Operator, invalid.Operator);
    }
}
