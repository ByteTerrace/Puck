using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>Laws for what a transform may write: never a row a reader may see who may not see a row the transform reads
/// (<see cref="StateVisibility.Encloses"/>, refused as <see cref="RuleRefusal.TransformWidensAudience"/>). A two-seat
/// table: <c>rank</c> is a token attribute only <c>seat1</c> reads, <c>table</c> is a zone every seat sees, and
/// <c>hand</c> is a zone only <c>seat1</c> sees.</summary>
public sealed class TransformDisclosureLawTests {
    private static readonly Principal Dealer = Principal.Peer(
        generation: 1,
        index: 4
    );

    private static StateCell Cell(string key, long value) => new(
        Key: CellName.Parse(candidate: key),
        Value: CellValue.Int(value: value)
    );
    private static StateCell Member(string key) => new(
        Key: CellName.Parse(candidate: key),
        Value: CellValue.Bool(value: true)
    );
    private static WorldDefinition Table(IReadOnlyList<WorldRule>? rules = null) {
        var tokens = CellName.Parse(candidate: "tokens");
        var seatOne = new StateVisibility(Readers: ["seat1"]);

        return (Fixtures.BuildDocument() with {
            Rules = rules,
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(Capacity: 8, Cells: [Cell(key: "a", value: 1L), Cell(key: "b", value: 2L), Cell(key: "c", value: 3L), Cell(key: "d", value: 4L), Cell(key: "e", value: 5L), Cell(key: "f", value: 6L)], Kind: CellKind.Int, Name: tokens),
                new WorldStateRow(Capacity: 8, Cells: [Cell(key: "a", value: 3L), Cell(key: "b", value: 1L), Cell(key: "c", value: 2L), Cell(key: "d", value: 3L), Cell(key: "e", value: 1L), Cell(key: "f", value: 2L)], Domain: new StateDomain.KeysOf(Row: tokens), Kind: CellKind.Int, Name: CellName.Parse(candidate: "rank"), Visibility: seatOne),
                new WorldStateRow(Capacity: 8, Cells: [Member(key: "a"), Member(key: "b"), Member(key: "c")], Domain: new StateDomain.KeysOf(Ordered: true, Row: tokens), Kind: CellKind.Bool, Name: CellName.Parse(candidate: "table")),
                new WorldStateRow(Capacity: 8, Cells: [Member(key: "d"), Member(key: "e"), Member(key: "f")], Domain: new StateDomain.KeysOf(Ordered: true, Row: tokens), Kind: CellKind.Bool, Name: CellName.Parse(candidate: "hand"), Visibility: seatOne),
            ]),
        });
    }
    private static StateTransform.Sort ByRank(string zone) => new(
        By: [new SortKey(Row: "rank")],
        Row: zone
    );
    private static string Order(WorldDefinition definition, string zone) => string.Join(
        separator: ",",
        values: WorldDefinitionRows.FindStateRow(name: zone, rows: definition.State)!.Cells!.Select(selector: static cell => cell.Key.Value)
    );

    // The dealer holds every grant the sort needs — edit over both zones, observe over rank — so authority admits both
    // sorts. Sorting the public table by rank would spell seat1's ranks to seat2 in the table's order, so it is refused
    // by name and the table stands; sorting seat1's own hand, which only seat1 sees, lands.
    [Fact]
    public void ASubmittedSortOfAWiderZoneByAHiddenRowIsRefusedAndOneWithinItsAudienceLands() {
        using var fixture = Fixtures.FreshServer(definition: Table());
        var rejections = new List<string>();

        fixture.Server.EchoTap = echo => {
            if (echo.Rejected) {
                rejections.Add(item: echo.Message);
            }
        };
        fixture.Server.Grant(
            actor: Principal.Console,
            grant: new WorldGrant(
                Budget: 16,
                Capability: WorldCapability.Mutate,
                Exclusive: false,
                KindMask: WorldMutationKindCatalog.KindsOf(section: WorldSection.State),
                Grantee: Dealer,
                Subject: GrantSubject.Section(section: WorldSection.State)
            )
        );
        foreach (var zone in ((string[])["table", "hand"])) {
            fixture.Server.Grant(
                actor: Principal.Console,
                grant: new WorldGrant(
                    Capability: WorldCapability.Edit,
                    Exclusive: false,
                    Grantee: Dealer,
                    Subject: GrantSubject.State(name: zone)
                )
            );
        }
        fixture.Server.Grant(
            actor: Principal.Console,
            grant: new WorldGrant(
                Budget: 16,
                Capability: WorldCapability.Observe,
                Exclusive: false,
                Grantee: Dealer,
                Subject: GrantSubject.State(name: "rank")
            )
        );

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.TransformState(
            Principal: Dealer,
            Transform: ByRank(zone: "table")
        ));
        fixture.Step();

        var refused = Assert.Single(collection: rejections);

        Assert.Contains(expectedSubstring: nameof(RuleRefusal.TransformWidensAudience), actualString: refused);
        Assert.Contains(actualString: refused, expectedSubstring: "'table' (public) from 'rank' (readers [seat1])");
        Assert.Equal(expected: "a,b,c", actual: Order(definition: fixture.Server.Definition, zone: "table"));

        rejections.Clear();
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.TransformState(
            Principal: Dealer,
            Transform: ByRank(zone: "hand")
        ));
        fixture.Step();

        Assert.Empty(collection: rejections);
        Assert.Equal(expected: "e,f,d", actual: Order(definition: fixture.Server.Definition, zone: "hand"));
    }
    // The same policy holds for the world's own rules, at validation: a rule sorting the public table by rank is
    // refused by name before the world boots, and the same rule over seat1's hand validates.
    [Fact]
    public void AnAuthoredRuleSortingAWiderZoneByAHiddenRowIsRefusedAtValidation() {
        static WorldRule Sorting(string zone) => new(
            Effects: [new ActionEffect.TransformState(Transform: ByRank(zone: zone))],
            Name: CellName.Parse(candidate: $"sort-{zone}")
        );

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: Table(rules: [Sorting(zone: "table")]),
            reason: out var reason
        ));
        Assert.Contains(expectedSubstring: $"rule 'sort-table' refused {nameof(RuleRefusal.TransformWidensAudience)}", actualString: reason);
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: Table(rules: [Sorting(zone: "hand")]),
                reason: out var handReason
            ),
            userMessage: handReason
        );
    }

    [Fact]
    public void AGrantedPeerTransferCannotPublishPrivateMembershipButCanReorderItsHand() {
        using var fixture = Fixtures.FreshServer(definition: Table());
        fixture.Server.Grant(actor: Principal.Console, grant: new WorldGrant(Budget: 4,
            Capability: WorldCapability.Mutate, Exclusive: false,
            KindMask: WorldMutationKindCatalog.KindsOf(section: WorldSection.State), Grantee: Dealer,
            Subject: GrantSubject.Section(section: WorldSection.State)));
        foreach (var row in new[] { "hand", "table" }) {
            fixture.Server.Grant(actor: Principal.Console, grant: new WorldGrant(Capability: WorldCapability.Edit,
                Exclusive: false, Grantee: Dealer, Subject: GrantSubject.State(name: row)));
        }
        var results = new List<WorldSubmissionResult>();
        fixture.Server.Submit(new SubmissionEnvelope(ConnectionId: SubmissionEnvelope.LocalConnectionId,
            SessionGeneration: 0, Sequence: 1, CorrelationId: 1, Principal: Dealer, OperationId: Guid.NewGuid(),
            Payload: new WorldSubmissionPayload.Mutation(Value: new WorldMutation.TransformState(Principal: Dealer,
                Transform: new StateTransform.Transfer(From: "hand", To: "table", Selector: ZoneSelector.First)))), results.Add);
        fixture.Step();
        var refused = Assert.IsType<WorldSubmissionResult.Mutation>(Assert.Single(results)).Outcome;
        Assert.False(refused.Applied);
        Assert.Contains(nameof(RuleRefusal.TransformWidensAudience), refused.Detail);
        Assert.Equal("a,b,c", Order(definition: fixture.Server.Definition, zone: "table"));
        Assert.Equal("d,e,f", Order(definition: fixture.Server.Definition, zone: "hand"));
        results.Clear();
        fixture.Server.Submit(new SubmissionEnvelope(ConnectionId: SubmissionEnvelope.LocalConnectionId,
            SessionGeneration: 0, Sequence: 2, CorrelationId: 2, Principal: Dealer, OperationId: Guid.NewGuid(),
            Payload: new WorldSubmissionPayload.Mutation(Value: new WorldMutation.TransformState(Principal: Dealer,
                Transform: new StateTransform.Transfer(From: "hand", To: "hand", Selector: ZoneSelector.First)))), results.Add);
        fixture.Step();
        var applied = Assert.IsType<WorldSubmissionResult.Mutation>(Assert.Single(results)).Outcome;
        Assert.True(applied.Applied, applied.Detail);
        Assert.Equal("e,f,d", Order(definition: fixture.Server.Definition, zone: "hand"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AGrantedPeerCannotPublishLivePrivateCellsThroughAnOwnValueSort(bool confined) {
        var definition = Table();
        definition = definition with { StateRaw = definition.StateRaw! with {
            World = definition.State.Select(row => row.Name.Value == "rank"
                ? row with { Visibility = confined ? new StateVisibility(Readers: ["seat1"]) : null }
                : row).ToArray(),
        } };
        using var fixture = Fixtures.FreshServer(definition: definition);
        // An admitted peer needs a metered state mutation grant and edit over the written row. Edit covers
        // an own-value sort's read/write row; no Observe grant over another row is involved in this attack.
        fixture.Server.Grant(actor: Principal.Console, grant: new WorldGrant(Budget: 4,
            Capability: WorldCapability.Mutate, Exclusive: false,
            KindMask: WorldMutationKindCatalog.KindsOf(section: WorldSection.State), Grantee: Dealer,
            Subject: GrantSubject.Section(section: WorldSection.State)));
        fixture.Server.Grant(actor: Principal.Console, grant: new WorldGrant(Capability: WorldCapability.Edit,
            Exclusive: false, Grantee: Dealer, Subject: GrantSubject.State(name: "rank")));
        var arena = fixture.Server.Arena;
        Assert.True(definition.StateCatalog.TryResolve(lane: StateLane.Document, name: "rank", handle: out var rank));
        Assert.True(arena.TryWriteVisibility(rowOrdinal: rank.Ordinal,
            key: arena.Keys.Intern(name: CellName.Parse(candidate: "a")), visibility: new StateVisibility(Readers: ["seat1"])));
        Assert.True(fixture.Server.PublishArena());
        var before = Order(definition: fixture.Server.Definition, zone: "rank");
        var results = new List<WorldSubmissionResult>();
        fixture.Server.Submit(new SubmissionEnvelope(ConnectionId: SubmissionEnvelope.LocalConnectionId,
            SessionGeneration: 0, Sequence: 1, CorrelationId: 1, Principal: Dealer, OperationId: Guid.NewGuid(),
            Payload: new WorldSubmissionPayload.Mutation(Value: new WorldMutation.TransformState(Principal: Dealer,
                Transform: new StateTransform.Sort(Row: "rank", By: [new SortKey(Row: "rank")])))), results.Add);
        fixture.Step();

        var result = Assert.IsType<WorldSubmissionResult.Mutation>(Assert.Single(results)).Outcome;
        Assert.Equal(Dealer, result.Actor);
        Assert.Equal(confined, result.Applied);
        if (confined) {
            Assert.Equal("b,e,c,f,a,d", Order(definition: fixture.Server.Definition, zone: "rank"));
        } else {
            Assert.Contains(nameof(RuleRefusal.TransformWidensAudience), result.Detail);
            Assert.Equal(before, Order(definition: fixture.Server.Definition, zone: "rank"));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ADecisionScoreCannotSelectAWiderAudienceTransform(bool confined, bool common) {
        ActionEffect[] effects = [new ActionEffect.TransformState(Transform: new StateTransform.Transfer(
            From: confined ? "hand" : "table", To: confined ? "hand" : "table", Selector: ZoneSelector.First))];
        var rule = new WorldRule(Name: CellName.Parse(candidate: "choose"), Effects: common ? effects : [],
            Decision: new WorldDecision(PeriodSeconds: 0.01m, ScoreKind: CellKind.Int,
                Options: [new WorldDecisionOption(Name: CellName.Parse(candidate: "move"),
                    Score: ExpressionProgram.Parse(text: "rank[d]"),
                    Effects: common ? [] : effects) ]));
        var valid = WorldDefinitionValidator.TryValidateLocally(definition: Table(rules: [rule]), reason: out var reason);
        Assert.True(confined == valid, reason);
        if (!confined) { Assert.True(reason.Contains(nameof(RuleRefusal.TransformWidensAudience), StringComparison.Ordinal), reason); }
    }
    // The declarations alone decide: a public policy encloses every audience, no restricted policy encloses a public
    // one, and a restricted policy encloses a restricted audience only through readers it lists itself and its own
    // live reader row.
    [Fact]
    public void APolicyEnclosesOnlyAnAudienceItAdmitsWhole() {
        var seatOne = new StateVisibility(Readers: ["seat1"]);
        var both = new StateVisibility(Readers: ["seat1", "seat2"]);
        var authority = new StateVisibility(Readers: []);
        var live = new StateVisibility(Readers: ["seat1"], ReadersFrom: "audience");

        Assert.True(condition: StateVisibility.Encloses(audience: null, policy: null));
        Assert.True(condition: StateVisibility.Encloses(policy: new StateVisibility(), audience: seatOne));
        Assert.False(condition: StateVisibility.Encloses(audience: null, policy: seatOne));
        Assert.False(condition: StateVisibility.Encloses(policy: seatOne, audience: new StateVisibility(Hidden: HiddenCells.Placeholder)));
        Assert.True(condition: StateVisibility.Encloses(audience: seatOne, policy: both));
        Assert.False(condition: StateVisibility.Encloses(audience: both, policy: seatOne));
        Assert.True(condition: StateVisibility.Encloses(audience: authority, policy: seatOne));
        Assert.False(condition: StateVisibility.Encloses(audience: live, policy: seatOne));
        Assert.True(condition: StateVisibility.Encloses(audience: live, policy: live));
    }
}
