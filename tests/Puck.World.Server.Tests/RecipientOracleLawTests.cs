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

    // Whether a dealt child exists is its deal row's membership; its id is admitted alike either way.
    [Fact]
    public void ADealtChildIdIsAdmittedAlikeWhetherTheChildExists() {
        var answers = new List<(bool Admits, string Withheld)>();
        foreach (var dealt in new[] { false, true }) {
            var pile = new WorldStateRow(CellName.Parse("pile"), CellKind.Int, Visibility: new StateVisibility(Readers: []),
                Cells: dealt ? [new StateCell(CellName.Parse("x"), CellValue.Int(1))] : []);
            var template = new WorldPlacement("tmpl", "piece", new DocumentVector3(0, 0, 0), 0, 1, Deal: new WorldPlacementDeal("pile"));
            var child = new WorldPlacement(WorldPlacementDeal.ChildId("tmpl", "x"), "piece", new DocumentVector3(0, 0, 0), 0, 1, Parent: "tmpl", DealSlot: 0);
            var document = new WorldDefinition(StateRaw: new WorldStateSection(World: [pile]),
                PlacementsRaw: new WorldPlacementsSection(dealt ? [template, child] : [template]));
            var view = WorldRecipientView.ForComposition(document, Principal.Seat(0));
            answers.Add((view.Admits(new WorldMutation.RemovePlacement(Principal.Seat(0), child.Id), out var withheld), withheld));
        }
        Assert.Equal(answers[0], answers[1]);
        Assert.False(answers[0].Admits);
    }

    // Removing a domain key is refused at commit while a row keyed over the domain still holds it, so the write's verdict
    // reads every dependent row's membership; a hidden dependent puts the domain outside the recipient's footprint.
    [Fact]
    public void RemovingADomainKeyAnswersAlikeWhetherAWithheldDependentHoldsIt() {
        var outcomes = new List<string>();
        foreach (var held in new[] { false, true }) {
            var cards = new WorldStateRow(CellName.Parse("cards"), CellKind.Int,
                Cells: [new StateCell(CellName.Parse("a"), CellValue.Int(1)), new StateCell(CellName.Parse("x"), CellValue.Int(2))]);
            var hand = new WorldStateRow(CellName.Parse("hand"), CellKind.Int, Domain: new StateDomain.KeysOf(CellName.Parse("cards")),
                Visibility: new StateVisibility(Readers: []), Cells: held ? [new StateCell(CellName.Parse("x"), CellValue.Int(0))] : []);
            var document = Fixtures.BuildDocument().WithWorldState([cards, hand]);
            Assert.True(WorldDefinitionValidator.TryValidateLocally(document, out var reason), reason);
            using var fixture = Fixtures.FreshServer(document);
            fixture.Server.Grant(actor: Principal.Console, grant: new WorldGrant(Capability: WorldCapability.Edit, Exclusive: false,
                Grantee: Principal.Seat(0), Subject: GrantSubject.State("cards")));
            var outcome = Submit(fixture, new WorldMutation.RemoveStateCell(Principal.Seat(0), "cards", "x"));
            outcomes.Add($"{outcome.Code}:{outcome.Detail}");
            Assert.Equal(2, fixture.Server.ExportDefinition().State[0].Cells!.Count);
        }
        Assert.Equal(outcomes[0], outcomes[1]);
        Assert.StartsWith(WorldRecipientMutations.NotDisclosedCode, outcomes[0], StringComparison.Ordinal);
    }

    // An incoming declaration keyed over a domain is validated against the domain's members before the row exists.
    [Fact]
    public void ANewRowKeyedOverAWithheldDomainIsRefusedAlikeWhetherTheDomainHoldsItsKey() {
        var outcomes = new List<string>();
        foreach (var held in new[] { false, true }) {
            var cards = new WorldStateRow(CellName.Parse("cards"), CellKind.Int, Visibility: new StateVisibility(Readers: []),
                Cells: held ? [new StateCell(CellName.Parse("a"), CellValue.Int(1)), new StateCell(CellName.Parse("x"), CellValue.Int(2))]
                    : [new StateCell(CellName.Parse("a"), CellValue.Int(1))]);
            var document = Fixtures.BuildDocument().WithWorldState([cards]);
            Assert.True(WorldDefinitionValidator.TryValidateLocally(document, out var reason), reason);
            using var fixture = Fixtures.FreshServer(document);
            var probe = new WorldStateRow(CellName.Parse("probe"), CellKind.Int, Domain: new StateDomain.KeysOf(CellName.Parse("cards")),
                Cells: [new StateCell(CellName.Parse("x"), CellValue.Int(0))]);
            var outcome = Submit(fixture, new WorldMutation.UpsertStateRow(Principal.Seat(0), probe));
            outcomes.Add($"{outcome.Code}:{outcome.Detail}");
            Assert.Single(fixture.Server.ExportDefinition().State);
        }
        Assert.Equal(outcomes[0], outcomes[1]);
        Assert.StartsWith(WorldRecipientMutations.NotDisclosedCode, outcomes[0], StringComparison.Ordinal);
    }

    // A parent id spelling a dealt child depends on the template whether or not that child exists: composition would
    // otherwise answer "not disclosed" for a present child and "not a declared placement" for an absent one.
    [Fact]
    public void APlacementUnderADealtChildIsAdmittedAlikeWhetherTheChildExists() {
        var answers = new List<(bool Admits, string Withheld)>();
        foreach (var dealt in new[] { false, true }) {
            var pile = new WorldStateRow(CellName.Parse("pile"), CellKind.Int, Visibility: new StateVisibility(Readers: []),
                Cells: dealt ? [new StateCell(CellName.Parse("x"), CellValue.Int(1))] : []);
            var template = new WorldPlacement("tmpl", "piece", new DocumentVector3(0, 0, 0), 0, 1, Deal: new WorldPlacementDeal("pile"));
            var child = new WorldPlacement(WorldPlacementDeal.ChildId("tmpl", "x"), "piece", new DocumentVector3(0, 0, 0), 0, 1, Parent: "tmpl", DealSlot: 0);
            var document = new WorldDefinition(StateRaw: new WorldStateSection(World: [pile]),
                PlacementsRaw: new WorldPlacementsSection(dealt ? [template, child] : [template]));
            var view = WorldRecipientView.ForComposition(document, Principal.Seat(0));
            var probe = new WorldPlacement("probe", "piece", new DocumentVector3(0, 0, 0), 0, 1, Parent: child.Id);
            answers.Add((view.Admits(new WorldMutation.UpsertPlacement(Principal.Seat(0), probe), out var withheld), withheld));
        }
        Assert.Equal(answers[0], answers[1]);
        Assert.Equal((false, "pile"), answers[0]);
    }

    // An unrelated document edit revalidates the whole authority and recompiles every rule. The compile-time audience
    // check reads declared row policies only; a cell policy a state command set is the arena's to judge at the firing,
    // so the recipient's edit lands whatever hidden cells the sorted row holds.
    [Fact]
    public void AnUnrelatedPlacementEditLandsAlikeWhateverCellPolicyASortedRowHoldsNow() {
        var outcomes = new List<string>();
        foreach (var hidden in new[] { false, true }) {
            var cards = new WorldStateRow(CellName.Parse("cards"), CellKind.Int,
                Cells: [new StateCell(CellName.Parse("a"), CellValue.Int(1)), new StateCell(CellName.Parse("b"), CellValue.Int(2))]);
            var table = new WorldStateRow(CellName.Parse("table"), CellKind.Bool, Domain: new StateDomain.KeysOf(CellName.Parse("cards"), Ordered: true),
                Cells: [new StateCell(CellName.Parse("a"), CellValue.Bool(true)), new StateCell(CellName.Parse("b"), CellValue.Bool(true))]);
            var rank = new WorldStateRow(CellName.Parse("rank"), CellKind.Int, Domain: new StateDomain.KeysOf(CellName.Parse("cards")), CellVisibility: true,
                Cells: hidden ? [new StateCell(CellName.Parse("b"), CellValue.Int(1), Visibility: new StateVisibility(Readers: []))] : []);
            var rule = new WorldRule(CellName.Parse("sort-table"), [new ActionEffect.TransformState(new StateTransform.Sort("table", [new SortKey("rank")]))]);
            var document = Fixtures.BuildDocument().WithWorldState([cards, table, rank]) with {
                Rules = [rule], CreationsRaw = [CreationFixtures.Sphere(id: "piece", scale: 0.2f)],
            };
            document = document with { PlacementsRaw = new WorldPlacementsSection([], Policy: Fixtures.StandardAuthoring) };
            Assert.True(WorldDefinitionValidator.TryValidateLocally(document, out var reason), reason);
            using var fixture = Fixtures.FreshServer(document);
            fixture.Server.Grant(actor: Principal.Console, grant: new WorldGrant(Budget: 4, Capability: WorldCapability.Mutate, Exclusive: false,
                KindMask: WorldMutationKindCatalog.KindsOf(WorldSection.Placements), Grantee: Principal.Seat(0),
                Subject: GrantSubject.Section(WorldSection.Placements)));
            var probe = new WorldPlacement("probe", document.Creations[0].Id, new DocumentVector3(0, 0, 0), 0, 1);
            var outcome = Submit(fixture, new WorldMutation.UpsertPlacement(Principal.Seat(0), probe));
            outcomes.Add($"{outcome.Applied}:{outcome.Code}:{outcome.Detail}");
        }
        Assert.Equal(outcomes[0], outcomes[1]);
        Assert.StartsWith("True:", outcomes[0], StringComparison.Ordinal);
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
