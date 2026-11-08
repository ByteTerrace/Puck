using Puck.Assets.Documents;
using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

// A recipient's write or guard answers alike whatever withheld state holds. Each case varies only a withheld value
// and compares what the recipient is told; running a World cannot construct the two variants side by side.
public sealed class RecipientOracleLawTests {
    private static WorldMutationOutcome Submit(WorldFixture fixture, WorldMutation mutation) {
        var results = new List<WorldSubmissionResult>();
        fixture.Server.Submit(new SubmissionEnvelope(ConnectionId: SubmissionEnvelope.LocalConnectionId,
            SessionGeneration: 0, Sequence: 1, CorrelationId: 1, Principal: mutation.Principal,
            Payload: new WorldSubmissionPayload.Mutation(Value: mutation), OperationId: Guid.NewGuid()), results.Add);
        fixture.Step();
        return Assert.IsType<WorldSubmissionResult.Mutation>(Assert.Single(results)).Outcome;
    }

    private static WorldStateRow Audience(string token, bool cellVisibility = true) =>
        new(CellName.Parse("audience"), CellKind.Text, Domain: StateDomain.Keys.Instance, CellVisibility: cellVisibility,
            Cells: [new StateCell(CellName.Parse("secret"), CellValue.Text(token), Visibility: new StateVisibility(Readers: ["seat2"]))]);
    private static WorldStateRow Inbox() => new(CellName.Parse("inbox"), CellKind.Int,
        Cells: [new StateCell(StateRow.SlotKey, CellValue.Int(0))], Visibility: new StateVisibility(ReadersFrom: "audience"));

    [Fact]
    public void AnAudienceSomeRecipientCannotReadWholeIsRefusedAndAdmitsNoOne() {
        var answers = new List<(bool Admits, string Withheld, bool Reads)>();
        foreach (var token in new[] { "seat1", "seat2" }) {
            var document = new WorldDefinition(StateRaw: new WorldStateSection(World: [Audience(token), Inbox()]));
            Assert.False(WorldDefinitionValidator.TryValidateLocally(document, out var reason));
            Assert.Contains("readersFrom", reason, StringComparison.Ordinal);
            // A later install can still restrict an audience the validator saw public; the decision stays public.
            var view = WorldRecipientView.ForComposition(document, Principal.Seat(0));
            var write = new WorldMutation.UpsertStateCell(Principal.Seat(0), "inbox", StateRow.SlotKey.Value, 9, WorldDocumentWriteKind.Set);
            answers.Add((view.Admits(write, out var withheld), withheld, view.Discloses("inbox", null)));
        }
        Assert.Equal(answers[0], answers[1]);
        Assert.False(answers[0].Admits);
    }

    [Fact]
    public void APublicAudienceStillAdmitsTheTokensItLists() {
        var audience = Audience("seat1", cellVisibility: false) with {
            Cells = [new StateCell(CellName.Parse("1"), CellValue.Text("seat1"))]
        };
        var document = new WorldDefinition(StateRaw: new WorldStateSection(World: [audience, Inbox()]));
        Assert.True(WorldDefinitionValidator.TryValidateLocally(document, out var reason), reason);
        var write = new WorldMutation.UpsertStateCell(Principal.Seat(0), "inbox", StateRow.SlotKey.Value, 9, WorldDocumentWriteKind.Set);
        Assert.True(WorldRecipientView.ForComposition(document, Principal.Seat(0)).Admits(write, out _));
        Assert.False(WorldRecipientView.ForComposition(document, Principal.Seat(1)).Admits(write, out _));
    }

    [Fact]
    public void ASpatialGuardRefusesAlikeWhereverWithheldGeometryLies() {
        var outcomes = new List<string>();
        foreach (var center in new[] { "[0,0,0]", "[100,0,0]" }) {
            var hidden = new WorldStateRow(CellName.Parse("geometry"), CellKind.Text,
                Cells: [new StateCell(StateRow.SlotKey, CellValue.Text(center))], Visibility: new StateVisibility(Readers: []));
            var marker = new WorldStateRow(CellName.Parse("marker"), CellKind.Int, Cells: [new StateCell(StateRow.SlotKey, CellValue.Int(0))]);
            var document = Fixtures.BuildDocument().WithWorldState([hidden, marker]) with { CreationsRaw = [CreationFixtures.Sphere(id: "piece", scale: 0.2f)] };
            var bound = System.Text.Json.JsonSerializer.Deserialize<DocumentVector3>("\"state.geometry\"")!;
            var placement = new WorldPlacement("obstacle", document.Creations[0].Id, new DocumentVector3(0, 0, 0), 0, 1,
                Spatial: [new WorldPlacementSpatialVolume("space", WorldPlacementSpatialRole.Occupation,
                    new WorldSpatialShape(WorldSpatialShapeKind.Sphere, bound, new DocumentVector3(0, 0, 0), 1))]);
            document = document with { PlacementsRaw = new WorldPlacementsSection([placement], Policy: Fixtures.StandardAuthoring) };
            Assert.True(WorldStateDocumentValues.TryResolve(document, out document, out var reason), reason);
            Assert.True(WorldDefinitionValidator.TryValidateLocally(document, out reason), reason);
            using var fixture = Fixtures.FreshServer(document);
            var batch = new WorldMutation.Batch(Principal.Seat(0),
                [new WorldMutation.UpsertStateCell(Principal.Seat(0), "marker", StateRow.SlotKey.Value, 1, WorldDocumentWriteKind.Set)],
                ExpectedSpatialReads: [new WorldSpatialReadDependency(-131072, -131072, 131072, 131072, new string('0', 64))]);
            var outcome = Submit(fixture, batch);
            outcomes.Add($"{outcome.Code}:{outcome.Detail}");
        }
        Assert.Equal(outcomes[0], outcomes[1]);
        Assert.StartsWith(WorldRecipientMutations.NotDisclosedCode, outcomes[0], StringComparison.Ordinal);
    }

    // An arrangement reads its zone's token domain, a row its subjects do not name; the arena's audience check walks
    // that row's current cell policies before the kernel runs.
    [Fact]
    public void ATransformAnswersAlikeWhateverPolicyAnUnnamedRowItReadsHoldsNow() {
        var outcomes = new List<string>();
        foreach (var restricted in new[] { false, true }) {
            var cards = new WorldStateRow(CellName.Parse("cards"), CellKind.Int, CellVisibility: true,
                Cells: [new StateCell(CellName.Parse("a"), CellValue.Int(1), Visibility: restricted ? new StateVisibility(Readers: ["seat2"]) : null),
                    new StateCell(CellName.Parse("b"), CellValue.Int(2))]);
            var pile = new WorldStateRow(CellName.Parse("pile"), CellKind.Bool, Domain: new StateDomain.KeysOf(CellName.Parse("cards"), Ordered: true),
                Cells: [new StateCell(CellName.Parse("b"), CellValue.Bool(true)), new StateCell(CellName.Parse("a"), CellValue.Bool(true))]);
            var rank = new WorldStateRow(CellName.Parse("rank"), CellKind.Int, Cells: [new StateCell(StateRow.SlotKey, CellValue.Int(0))]);
            var document = Fixtures.BuildDocument().WithWorldState([cards, pile, rank]);
            Assert.True(WorldDefinitionValidator.TryValidateLocally(document, out var reason), reason);
            using var fixture = Fixtures.FreshServer(document);
            var outcome = Submit(fixture, new WorldMutation.TransformState(Principal.Seat(0), new StateTransform.Arrange("pile", "rank")));
            outcomes.Add($"{outcome.Code}:{outcome.Detail}");
        }
        Assert.Equal(outcomes[0], outcomes[1]);
    }

    // A keyed row's transaction refuses a key outside its token domain, so the write reads the domain's membership.
    [Fact]
    public void AWriteIntoAZoneAnswersAlikeWhetherAWithheldDomainHoldsTheKey() {
        var outcomes = new List<string>();
        foreach (var held in new[] { false, true }) {
            var cards = new WorldStateRow(CellName.Parse("cards"), CellKind.Int, Visibility: new StateVisibility(Readers: []),
                Cells: held ? [new StateCell(CellName.Parse("a"), CellValue.Int(1)), new StateCell(CellName.Parse("x"), CellValue.Int(2))]
                    : [new StateCell(CellName.Parse("a"), CellValue.Int(1))]);
            var hand = new WorldStateRow(CellName.Parse("hand"), CellKind.Int, Domain: new StateDomain.KeysOf(CellName.Parse("cards")),
                Cells: [new StateCell(CellName.Parse("a"), CellValue.Int(0))]);
            var document = Fixtures.BuildDocument().WithWorldState([cards, hand]);
            Assert.True(WorldDefinitionValidator.TryValidateLocally(document, out var reason), reason);
            using var fixture = Fixtures.FreshServer(document);
            var outcome = Submit(fixture, new WorldMutation.UpsertStateCell(Principal.Seat(0), "hand", "x", 5, WorldDocumentWriteKind.Set));
            outcomes.Add($"{outcome.Code}:{outcome.Detail}");
        }
        Assert.Equal(outcomes[0], outcomes[1]);
    }

    [Fact]
    public void ABatchWhoseMembersCloseAParentCycleIsAnsweredNotOverflowed() {
        var document = Fixtures.BuildDocument() with { CreationsRaw = [CreationFixtures.Sphere(id: "piece", scale: 0.2f)] };
        var a = new WorldPlacement("a", document.Creations[0].Id, new DocumentVector3(0, 0, 0), 0, 1);
        var b = a with { Id = "b", Parent = "a" };
        document = document with { PlacementsRaw = new WorldPlacementsSection([a, b], Policy: Fixtures.StandardAuthoring) };
        Assert.True(WorldDefinitionValidator.TryValidateLocally(document, out var reason), reason);
        using var fixture = Fixtures.FreshServer(document);
        var batch = new WorldMutation.Batch(Principal.Seat(0), [new WorldMutation.UpsertPlacement(Principal.Seat(0), a with { Parent = "b" }),
            new WorldMutation.RemovePlacement(Principal.Seat(0), "b")]);
        Assert.False(string.IsNullOrEmpty(Submit(fixture, batch).Code));
    }
}
