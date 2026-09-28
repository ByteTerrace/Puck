using System.Numerics;
using System.Text.Json;

using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW, through the editor's verbs (<see cref="WorldEditorEditQueueStateLawTests"/> holds the queue's own laws).
/// Each queued edit goes out under the principal that issued it and through its own seat's endpoint, while seats in one
/// world share its order. An edit is validated and composed before it queues, so a refused edit never enters the queue,
/// and a submission that throws is settled like a refusal. A new placement's id is minted against the delivered
/// document and every edit in flight or queued. An undone edit is never restored by the next nudge. One path settles a
/// refusal, inline or late. A crossing onward or a closed endpoint abandons its edits by name and sends nothing more; a
/// stopped world answers its edits with the stop, and its successor takes edits at once. Each claim has a red leg./// </summary>
public sealed class WorldEditorEditQueueLawTests {
    private const string Nudge = "world.nudge crate1 x 1";

    private static readonly Vector3 Step = new(x: WorldEditorPlacementLawTests.Pitch, y: 0f, z: 0f);

    // A boot row, a world a seat crossed into over a link whose verdicts the law answers, and the lines echoed.
    private sealed class Rig : IDisposable {
        private readonly HostRow m_row;

        public Rig() {
            var echoes = new WorldDeferredVerbEchoes();

            m_row = WorldEditorPlacementLawTests.Build();
            Away = WorldEditorPlacementLawTests.AwayDocument(row: m_row);
            Link = new RecordingLink(definition: Away);
            Endpoint = EditorEndpoints.Of(definition: Away, identity: "away", link: Link, pose: WorldEditorPlacementLawTests.AwayCrate);
            Registry = WorldEditorPlacementLawTests.BuildRegistry(echoes: echoes, routes: Routes, row: m_row, seats: Seats);
            echoes.Completed += result => Lines.Add(item: result.Output);
            _ = Routes.Publish(endpoint: Endpoint, entity: Endpoint.Mirror.Address(index: 0), slot: 0);
            Submit(line: $"world.grid pitch {WorldEditorPlacementLawTests.Pitch}");
        }

        public WorldDefinition Away { get; }
        public WorldAuthorityEndpoint Endpoint { get; }

        public List<string> Lines { get; } = [];

        public RecordingLink Link { get; }
        public CommandRegistry Registry { get; }

        public WorldSeatAuthorityRouter Routes { get; } = new();
        public WorldEditorSeats Seats { get; } = new();

        public void Dispose() {
            Endpoint.Dispose();
            m_row.Dispose();
        }
        // Delivers the away world's document with its crate1 at a position, at the link's current version.
        public void Deliver(Vector3 crate) => Endpoint.Mirror.DeliverDefinition(
            definition: At(crate: crate, definition: Away),
            version: Link.Version
        );
        public CommandResult Submit(string line) {
            var result = Registry.Submit(line: line);

            Assert.False(condition: result.IsError, userMessage: result.Output);

            return result;
        }
    }

    private static WorldDefinition At(WorldDefinition definition, Vector3 crate) => (definition with {
        PlacementRowsRaw = [(WorldDefinitionRows.FindPlacement(id: "crate1", placements: definition.Placements)! with { Position = crate })],
    });

    [Fact]
    public void EachQueuedEditGoesOutUnderThePrincipalThatIssuedIt() {
        using var rig = new Rig();
        using var seat = new TextCommandSource(registry: rig.Registry).CreateSession(principal: Principal.Seat(slot: 0), slot: 0);

        // The console's nudge goes in flight; a second of its own queues, and the seat's queues behind it rather than
        // superseding it, since each is its own issuer's.
        _ = rig.Submit(line: Nudge);
        _ = rig.Submit(line: Nudge);

        var seated = rig.Registry.SubmitSession(line: Nudge, session: seat);

        Assert.False(condition: seated.IsError, userMessage: seated.Output);
        _ = Assert.Single(collection: rig.Link.Submitted);

        rig.Link.Complete(index: 0, result: rig.Link.Applied());
        rig.Link.Complete(index: 1, result: rig.Link.Applied());
        Assert.Equal(actual: rig.Link.Submitted.Count, expected: 3);

        // Red leg: the edit in flight when the others queued is the console's, so a queue that composed a queued edit
        // under the first issuer's context sends the seat's as the console.
        Assert.Equal(actual: rig.Link.PrincipalOf(index: 0), expected: Principal.Console);
        Assert.Equal(actual: rig.Link.PrincipalOf(index: 1), expected: Principal.Console);
        Assert.Equal(actual: rig.Link.PrincipalOf(index: 2), expected: Principal.Seat(slot: 0));
        Assert.Equal(actual: ((Vector3)rig.Link.Placement(index: 1).Position), expected: (WorldEditorPlacementLawTests.AwayCrate + (2f * Step)));
        Assert.Equal(actual: ((Vector3)rig.Link.Placement(index: 2).Position), expected: (WorldEditorPlacementLawTests.AwayCrate + (3f * Step)));
    }
    [Fact]
    public void AnEditThatCannotGoOutIsRefusedBeforeItQueuesAndNeverWedgesItsLine() {
        using var rig = new Rig();

        _ = rig.Submit(line: Nudge);

        // A pitch this coarse moves the crate to infinity: refused by name at the verb, never queued.
        _ = rig.Submit(line: "world.grid pitch 3e38");

        var infinite = rig.Registry.Submit(line: "world.nudge crate1 x 2");

        Assert.True(condition: infinite.IsError);
        Assert.Equal(actual: infinite.Output, expected: "[world.nudge: 'crate1' in 'away' refused: placement.position must contain finite coordinates.]");

        // Red leg: the row it would have queued cannot be serialized at all, which a callback composing it would throw.
        var unbounded = WorldDefinitionRows.FindPlacement(id: "crate1", placements: rig.Away.Placements)! with { Position = new Vector3(x: float.PositiveInfinity, y: 0f, z: 0f) };

        _ = Assert.ThrowsAny<ArgumentException>(testCode: () => JsonSerializer.Serialize(value: unbounded, jsonTypeInfo: WorldJsonContext.Default.WorldPlacement));

        // The verdict of the edit in flight submits nothing more: nothing was queued behind it.
        rig.Link.Complete(index: 0, result: rig.Link.Applied());
        _ = Assert.Single(collection: rig.Link.Submitted);

        // A link that throws settles its submission refused, by name, and the line is free for the next edit.
        _ = rig.Submit(line: $"world.grid pitch {WorldEditorPlacementLawTests.Pitch}");
        _ = rig.Submit(line: Nudge);
        _ = rig.Submit(line: Nudge);
        rig.Link.Fails = true;
        rig.Link.Complete(index: 1, result: rig.Link.Applied());
        Assert.Contains(collection: rig.Lines, expected: "[world.nudge: 'crate1' in 'away' was not submitted: the link is closed]");
        Assert.Contains(collection: rig.Lines, expected: "[world.nudge: 'crate1' in 'away' rolled back to -2,0,2]");
        rig.Link.Fails = false;
        _ = rig.Submit(line: Nudge);
        Assert.Equal(actual: rig.Link.Submitted.Count, expected: 3);
        Assert.Equal(actual: ((Vector3)rig.Link.Placement(index: 2).Position), expected: (WorldEditorPlacementLawTests.AwayCrate + (3f * Step)));
    }
    [Fact]
    public void PlacesMadeBeforeAnyVerdictMintDistinctIds() {
        using var rig = new Rig();

        rig.Seats.AimProbe = static _ => new WorldEditorRay(Direction: Vector3.UnitZ, Origin: Vector3.Zero);
        _ = rig.Submit(line: "world.place crate");
        _ = rig.Submit(line: "world.place crate");
        Assert.Equal(actual: rig.Link.Submitted.Count, expected: 2);
        Assert.Equal(actual: rig.Link.Placement(index: 0).Id, expected: "crate2");
        Assert.Equal(actual: rig.Link.Placement(index: 1).Id, expected: "crate3");

        // An explicit id an edit holds is taken too. Red leg: the delivered document alone does not hold it, so a check
        // against the document lets a second placement overwrite the first.
        var taken = rig.Registry.Submit(line: "world.place crate crate2");

        Assert.True(condition: taken.IsError);
        Assert.Equal(actual: taken.Output, expected: "[world.place: a placement 'crate2' already exists]");
        Assert.Null(@object: WorldDefinitionRows.FindPlacement(id: "crate2", placements: rig.Away.Placements));
    }
    [Fact]
    public void AnUndoneEditIsNeverRestoredByTheNextNudge() {
        // In the console's own world: nudge x, let it apply and deliver, undo it, let that apply, then nudge z.
        using var row = WorldEditorPlacementLawTests.Build();
        var registry = WorldEditorPlacementLawTests.BuildRegistry(row: row, seats: new WorldEditorSeats());
        var before = ((Vector3)WorldDefinitionRows.FindPlacement(id: "crate1", placements: row.Server.Definition.Placements)!.Position);
        var z = new Vector3(x: 0f, y: 0f, z: WorldEditorPlacementLawTests.Pitch);

        Assert.False(condition: registry.Submit(line: $"world.grid pitch {WorldEditorPlacementLawTests.Pitch}").IsError);
        Assert.False(condition: registry.Submit(line: Nudge).IsError);
        row.Server.Advance(stepTicks: Fixtures.StepTicks);
        row.Instance.Link.SubmitUndo(count: 1, principal: Principal.Console);
        row.Server.Advance(stepTicks: Fixtures.StepTicks);
        Assert.False(condition: registry.Submit(line: "world.nudge crate1 z 1").IsError);
        row.Server.Advance(stepTicks: Fixtures.StepTicks);
        Assert.Equal(actual: ((Vector3)WorldDefinitionRows.FindPlacement(id: "crate1", placements: row.Server.Definition.Placements)!.Position), expected: (before + z));

        // Across a seam: the away world confirms a nudge, delivers it, then delivers its undo, all before the next edit.
        using var rig = new Rig();

        _ = rig.Submit(line: Nudge);
        rig.Link.Complete(index: 0, result: rig.Link.Applied());
        rig.Deliver(crate: (WorldEditorPlacementLawTests.AwayCrate + Step));
        rig.Link.Sequence++;
        rig.Deliver(crate: WorldEditorPlacementLawTests.AwayCrate);
        _ = rig.Submit(line: "world.nudge crate1 z 1");
        Assert.Equal(actual: ((Vector3)rig.Link.Placement(index: 1).Position), expected: (WorldEditorPlacementLawTests.AwayCrate + z));

        // Red leg: the undone document shows exactly the row from before the edit, so a queue ordering by value takes it
        // as a document older than the confirmation and bases the next edit on the undone move.
        Assert.Equal(actual: ((Vector3)WorldDefinitionRows.FindPlacement(id: "crate1", placements: rig.Endpoint.Definition.Placements)!.Position), expected: WorldEditorPlacementLawTests.AwayCrate);
    }
    [Fact]
    public void ACrossingOnwardAbandonsTheWorldItLeftAndNeverCarriesItsRows() {
        using var rig = new Rig();
        var beyond = new Vector3(x: 7f, y: 1f, z: -4f);

        // In world A: one nudge confirmed, a second in flight when the seat crosses on.
        _ = rig.Submit(line: Nudge);
        rig.Link.Complete(index: 0, result: rig.Link.Applied());
        _ = rig.Submit(line: Nudge);

        // The same traveler endpoint now carries world B: another activation, whose crate1 stands elsewhere.
        rig.Link.Activation = Guid.NewGuid();
        rig.Link.Sequence = 0L;
        rig.Deliver(crate: beyond);
        _ = rig.Routes.Publish(endpoint: rig.Endpoint, entity: new WorldEntityAddress(Authority: "beyond", Generation: 1, Index: 0), slot: 0);
        Assert.Contains(collection: rig.Lines, expected: "[world.nudge: 'crate1' in 'away' abandoned: its link now delivers another world]");

        // A nudge in B bases on B's own crate1. Red leg: A's confirmed row is not B's, so a queue keyed by the endpoint's
        // name hands B the geometry of A plus the nudge.
        _ = rig.Submit(line: Nudge);
        Assert.Equal(actual: ((Vector3)rig.Link.Placement(index: 2).Position), expected: (beyond + Step));
        Assert.NotEqual(actual: ((Vector3)rig.Link.Placement(index: 1).Position), expected: beyond);

        // A's late verdict answers nothing.
        rig.Link.Complete(index: 1, result: rig.Link.Applied());
        Assert.Equal(actual: rig.Link.Submitted.Count, expected: 3);
    }
    [Fact]
    public void AStoppedWorldAnswersItsEditsAndItsSuccessorTakesEditsAtOnce() {
        var echoes = new WorldDeferredVerbEchoes();
        var lines = new List<string>();
        var current = WorldEditorPlacementLawTests.Build();
        var registry = WorldEditorPlacementLawTests.BuildRegistry(console: () => current.Instance, echoes: echoes, row: current, seats: new WorldEditorSeats());

        echoes.Completed += result => lines.Add(item: result.Output);

        try {
            // An edit in flight in 'boot', which stops before it steps again: the stop answers it, by name, and the line
            // rolls back.
            Assert.False(condition: registry.Submit(line: Nudge).IsError);

            var stopped = current;

            stopped.Dispose();
            Assert.Contains(collection: lines, expected: $"[world.nudge: {Server.WorldServer.StoppedCode} instance 'boot' stopped]");
            Assert.Contains(collection: lines, expected: "[world.nudge: 'crate1' in 'boot' rolled back to 1.1,3,-1]");

            // Red leg: the stopped world never stepped, so the edit was answered by the stop, never applied.
            Assert.Equal(actual: ((Vector3)WorldDefinitionRows.FindPlacement(id: "crate1", placements: stopped.Server.Definition.Placements)!.Position).X, expected: 1.1f);

            // A new 'boot', at the very tick the stopped one had reached, takes the next edit at once, from its own document:
            // neither the queue nor the console's row guard carries anything over from the world it replaced.
            current = WorldEditorPlacementLawTests.Build();

            var before = ((Vector3)WorldDefinitionRows.FindPlacement(id: "crate1", placements: current.Server.Definition.Placements)!.Position);
            var nudged = registry.Submit(line: Nudge);

            Assert.False(condition: nudged.IsError, userMessage: nudged.Output);
            Assert.Contains(expectedSubstring: "edit=submitted", actualString: nudged.Output);
            current.Server.Advance(stepTicks: Fixtures.StepTicks);
            Assert.Equal(actual: ((Vector3)WorldDefinitionRows.FindPlacement(id: "crate1", placements: current.Server.Definition.Placements)!.Position).X, expected: (before.X + WorldEditorPlacementLawTests.Pitch), tolerance: 1e-5);
        } finally {
            current.Dispose();
        }
    }
    [Fact]
    public void TwoSeatsInOneWorldShareItsOrderAndEachSendsThroughItsOwnEndpoint() {
        using var rig = new Rig();
        var second = new RecordingLink(definition: rig.Away) { Activation = rig.Link.Activation };
        using var other = EditorEndpoints.Of(definition: rig.Away, identity: "away-2", link: second, pose: WorldEditorPlacementLawTests.AwayCrate);
        using var seat = new TextCommandSource(registry: rig.Registry).CreateSession(principal: Principal.Seat(slot: 1), slot: 1);

        // Both seats travel in the same world, each through its own endpoint.
        _ = rig.Routes.Publish(endpoint: rig.Endpoint, entity: new WorldEntityAddress(Authority: "away", Generation: 1, Index: 0), slot: 0);
        _ = rig.Routes.Publish(endpoint: other, entity: new WorldEntityAddress(Authority: "away", Generation: 1, Index: 1), slot: 1);
        Assert.False(condition: rig.Registry.SubmitSession(line: $"world.grid pitch {WorldEditorPlacementLawTests.Pitch}", session: seat).IsError);

        // Seat 1's nudge queues behind seat 0's in the world's one order, and goes out, composed on it, through seat 1's
        // endpoint under seat 1's principal.
        _ = rig.Submit(line: Nudge);

        var seated = rig.Registry.SubmitSession(line: Nudge, session: seat);

        Assert.Contains(expectedSubstring: "edit=queued", actualString: seated.Output);
        Assert.Empty(collection: second.Submitted);
        rig.Link.Complete(index: 0, result: rig.Link.Applied());
        _ = Assert.Single(collection: second.Submitted);
        Assert.Equal(actual: second.PrincipalOf(index: 0), expected: Principal.Seat(slot: 1));
        Assert.Equal(actual: ((Vector3)second.Placement(index: 0).Position), expected: (WorldEditorPlacementLawTests.AwayCrate + (2f * Step)));

        // Red leg: seat 0's endpoint carried seat 0's edit alone, so a queue keeping the first endpoint's link would have
        // sent seat 1's through seat 0's credential.
        _ = Assert.Single(collection: rig.Link.Submitted);
    }
    [Fact]
    public void AClosedEndpointAbandonsItsEditsAndSendsNothingMore() {
        using var rig = new Rig();
        var mutation = new WorldMutation.UpsertPlacement(Placement: WorldDefinitionRows.FindPlacement(id: "crate1", placements: rig.Away.Placements)!, Principal: Principal.Console);
        WorldSubmissionResult? answer = null;

        // Red leg: an open endpoint's link carries the edit.
        _ = rig.Submit(line: Nudge);
        _ = Assert.Single(collection: rig.Link.Submitted);

        rig.Endpoint.Dispose();
        Assert.Contains(collection: rig.Lines, expected: "[world.nudge: 'crate1' in 'away' abandoned: its link closed]");
        _ = rig.Endpoint.Submissions.SubmitWorldMutation(completion: result => answer = result, mutation: mutation, operationId: Guid.NewGuid());
        Assert.Equal(actual: Assert.IsType<WorldSubmissionResult.Refusal>(@object: answer).Code, expected: "world.endpoint.retired");
        _ = Assert.Single(collection: rig.Link.Submitted);
    }
    [Fact]
    public void AnEditMadeOnOneWorldIsRefusedByAWorldItsLinkReachedInstead() {
        using var console = WorldEditorPlacementLawTests.Build();
        var away = WorldEditorPlacementLawTests.AwayDocument(row: console);
        using var first = HostRow.Build(definition: away, name: "a");
        using var second = HostRow.Build(definition: away, name: "b");
        var link = new SwitchingLink(target: first.Instance.Link);
        using var endpoint = EditorEndpoints.Of(definition: first.Server.Definition, identity: "away", link: link, pose: WorldEditorPlacementLawTests.AwayCrate, version: first.Server.DocumentVersion);
        var echoes = new WorldDeferredVerbEchoes();
        var lines = new List<string>();
        var routes = new WorldSeatAuthorityRouter();
        var registry = WorldEditorPlacementLawTests.BuildRegistry(echoes: echoes, routes: routes, row: console, seats: new WorldEditorSeats());

        Vector3 CrateIn(HostRow world) => ((Vector3)WorldDefinitionRows.FindPlacement(id: "crate1", placements: world.Server.Definition.Placements)!.Position);

        echoes.Completed += result => lines.Add(item: result.Output);
        _ = routes.Publish(endpoint: endpoint, entity: endpoint.Mirror.Address(index: 0), slot: 0);
        Assert.False(condition: registry.Submit(line: $"world.grid pitch {WorldEditorPlacementLawTests.Pitch}").IsError);

        // The traveler crosses on: its link reaches world b before its mirror has seen b. An edit based on a's document is
        // refused by b by name, and rolls back.
        link.Target = second.Instance.Link;

        var refused = registry.Submit(line: Nudge);

        Assert.True(condition: refused.IsError);
        Assert.StartsWith(actualString: refused.Output, expectedStartString: $"[world.nudge: {Server.WorldDocument.ActivationMismatchCode} ");
        Assert.Contains(collection: lines, expected: "[world.nudge: 'crate1' in 'away' rolled back to -3,0,2]");
        second.Server.Advance(stepTicks: Fixtures.StepTicks);
        Assert.Equal(actual: CrateIn(world: second), expected: WorldEditorPlacementLawTests.AwayCrate);

        // Red leg: the same upsert with no expectation is one b applies.
        _ = second.Instance.Link.SubmitWorldMutation(mutation: new WorldMutation.UpsertPlacement(
            Placement: (WorldDefinitionRows.FindPlacement(id: "crate1", placements: away.Placements)! with { Position = (WorldEditorPlacementLawTests.AwayCrate + Step) }),
            Principal: Principal.Console
        ));
        second.Server.Advance(stepTicks: Fixtures.StepTicks);
        Assert.Equal(actual: CrateIn(world: second), expected: (WorldEditorPlacementLawTests.AwayCrate + Step));
    }
}
