using System.Numerics;
using System.Text.Json;

using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: the editor's pending-edit queue is a state machine whose invariants hold at every step. Each queued edit
/// goes out under the principal that issued it. An edit is validated and composed before it queues, so a refused edit
/// never enters the queue, and a submission that throws is settled like a refusal. A new placement's id is minted
/// against the delivered document and every edit in flight or queued. A confirmed value is ordered against delivered
/// documents by the world's own document versions, so a late document never replaces it and a newer one (an undo)
/// always does. One path settles a refusal, inline or late. A queue is keyed by its world's authority and activation
/// and lives exactly as long as its world does here: a crossing onward or a stopped world abandons its edits by name.
/// A base is read and its edit admitted in one step, so no refusal settles in between. Each claim has a red leg, and a
/// seeded random interleaving of submissions, verdicts, stale verdicts, deliveries, undos, crossings and teardowns
/// checks every invariant after every step.
/// </summary>
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
    private static WorldPlacement Row(string id, float x) => new(
        Id: id,
        Position: new Vector3(x: x, y: 0f, z: 0f),
        PrototypeId: "crate",
        Scale: 1f,
        YawDegrees: 0f
    );
    private static WorldEditorEditQueue.Edit EditOf(WorldPlacement row, Principal principal) => new(
        Mutation: new WorldMutation.UpsertPlacement(Placement: row, Principal: principal),
        Row: row,
        Verb: WorldEditorCommandModule.NudgeCommand
    );
    // An offer whose composer ignores the base and writes a fixed row, as a law drives the queue directly.
    private static WorldEditorEditQueue.Admission Offer(WorldEditorEditQueue queue, WorldPlacement row, Principal principal, WorldPlacement? delivered, WorldDocumentVersion version) => queue.Offer(
        compose: (WorldPlacement? basis, bool queues, out CommandResult refusal) => {
            refusal = CommandResult.None;

            return EditOf(principal: principal, row: row);
        },
        delivered: delivered,
        id: row.Id,
        verb: WorldEditorCommandModule.NudgeCommand,
        version: version
    );

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
    public void ADocumentOlderThanAConfirmedValueNeverReplacesItAndANewerOneAlwaysDoes() {
        var activation = Guid.NewGuid();
        var queue = new WorldEditorEditQueue(activation: activation);
        var origin = Row(id: "a", x: 0f);
        var first = Row(id: "a", x: 1f);
        var second = Row(id: "a", x: 2f);
        var undone = first;

        WorldDocumentVersion Installed(long sequence) => new(Activation: activation, Sequence: sequence);

        _ = queue.Settle(applied: true, id: "a", token: Offer(delivered: origin, principal: Principal.Console, queue: queue, row: first, version: Installed(sequence: 0)).Submitted!.Value.Token, version: Installed(sequence: 1));
        _ = queue.Settle(applied: true, id: "a", token: Offer(delivered: origin, principal: Principal.Console, queue: queue, row: second, version: Installed(sequence: 0)).Submitted!.Value.Token, version: Installed(sequence: 2));

        // The world confirmed both, at 1 and 2. A document from before either, or from between them, arriving after the
        // verdicts, never replaces the confirmed value.
        Assert.Equal(actual: queue.Latest(delivered: origin, id: "a", version: Installed(sequence: 0)), expected: second);
        Assert.Equal(actual: queue.Latest(delivered: first, id: "a", version: Installed(sequence: 1)), expected: second);
        Assert.Equal(actual: queue.Lines, expected: 1);

        // Red leg: a document at or past the confirmation releases the line whatever it shows. An undo that puts the
        // first value back is a newer document, and the next edit bases on it; a value comparison would keep the second.
        Assert.Equal(actual: queue.Latest(delivered: undone, id: "a", version: Installed(sequence: 3)), expected: undone);
        Assert.Equal(actual: queue.Lines, expected: 0);
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
    public void AStoppedWorldAbandonsItsEditsAndItsSuccessorTakesEditsAtOnce() {
        var echoes = new WorldDeferredVerbEchoes();
        var lines = new List<string>();
        var current = WorldEditorPlacementLawTests.Build();
        var registry = WorldEditorPlacementLawTests.BuildRegistry(console: () => current.Instance, echoes: echoes, row: current, seats: new WorldEditorSeats());

        echoes.Completed += result => lines.Add(item: result.Output);

        try {
            // An edit in flight in 'boot', which stops before it steps again.
            Assert.False(condition: registry.Submit(line: Nudge).IsError);
            current.Dispose();
            Assert.Contains(collection: lines, expected: "[world.nudge: 'crate1' in 'boot' abandoned: its world stopped]");

            // Red leg: the stopped world never answers the edit it held, so a queue that outlived it would queue every
            // later edit to crate1 behind a verdict that never comes.
            Assert.DoesNotContain(collection: lines, filter: line => line.Contains(comparisonType: StringComparison.Ordinal, value: "world.mutation"));

            // A new 'boot' takes the next edit at once, from its own document. It steps once first: the console's row
            // guard keys a claim by tick alone, and the stopped world's claim holds for the tick both worlds share.
            current = WorldEditorPlacementLawTests.Build();
            current.Server.Advance(stepTicks: Fixtures.StepTicks);

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
    public void ARefusalCannotSettleBetweenABaseReadAndItsAdmission() {
        var activation = Guid.NewGuid();
        var version = new WorldDocumentVersion(Activation: activation, Sequence: 0L);
        var origin = Row(id: "a", x: 0f);
        var queue = new WorldEditorEditQueue(activation: activation);
        var first = Offer(delivered: origin, principal: Principal.Seat(slot: 0), queue: queue, row: Row(id: "a", x: 1f), version: version).Submitted!.Value;
        Thread? refusal = null;
        WorldEditorEditQueue.Settlement settled = default;

        // The console composes on the seat's edit in flight; the world refuses that edit from another thread while the
        // console is composing. The refusal waits for the admission, then drops the console's edit composed on it.
        var admission = queue.Offer(
            compose: (WorldPlacement? basis, bool queues, out CommandResult composeRefusal) => {
                refusal = new Thread(start: () => settled = queue.Settle(applied: false, id: "a", token: first.Token, version: version));
                refusal.Start();
                Assert.False(condition: refusal.Join(millisecondsTimeout: 100));
                composeRefusal = CommandResult.None;

                return EditOf(principal: Principal.Console, row: (basis! with { Position = new Vector3(x: 2f, y: 0f, z: 0f) }));
            },
            delivered: origin,
            id: "a",
            verb: WorldEditorCommandModule.NudgeCommand,
            version: version
        );

        refusal!.Join();
        Assert.NotNull(@object: admission.Queued);
        Assert.True(condition: settled.RolledBack);
        Assert.Equal(actual: settled.Dropped, expected: 1);
        Assert.Equal(actual: queue.Latest(delivered: origin, id: "a", version: version), expected: origin);

        // Red leg: with the read and the admission apart, the refusal lands between them and the console's edit, composed
        // on the refused one, goes out on its own under the console.
        var apart = new WorldEditorEditQueue(activation: activation);
        var seat = Offer(delivered: origin, principal: Principal.Seat(slot: 0), queue: apart, row: Row(id: "a", x: 1f), version: version).Submitted!.Value;
        var read = apart.Latest(delivered: origin, id: "a", version: version)!;

        _ = apart.Settle(applied: false, id: "a", token: seat.Token, version: version);
        Assert.NotNull(@object: Offer(delivered: origin, principal: Principal.Console, queue: apart, row: (read with { Position = new Vector3(x: 2f, y: 0f, z: 0f) }), version: version).Submitted);
    }
    [Fact]
    public void ARandomInterleavingKeepsEveryInvariantAtEveryStep() {
        var random = new Random(Seed: 1729);
        string[] ids = ["a", "b", "c"];
        Principal[] principals = [Principal.Console, Principal.Seat(slot: 0), Principal.Seat(slot: 1)];
        var value = 0f;

        var (confirms, refusalsDropping, supersedes, stale, undos, released, crossings, abandoned) = (0, 0, 0, 0, 0, 0, 0, 0);

        // The world behind the queue: its activation, and its documents in install order, each a row per placement. The
        // delivered document is one of them and never moves back. The model of each line: the edit in flight, the edits
        // queued, and the confirmed value no delivered document reflects yet with the install it applied at.
        var activation = Guid.NewGuid();
        var queue = new WorldEditorEditQueue(activation: activation);
        var documents = new List<Dictionary<string, WorldPlacement>> { ids.ToDictionary(elementSelector: id => Row(id: id, x: 0f), keySelector: id => id) };
        var delivered = 0;
        var inFlight = new Dictionary<string, WorldEditorEditQueue.Submission>();
        var queued = ids.ToDictionary(elementSelector: _ => new List<WorldEditorEditQueue.Edit>(), keySelector: id => id);
        var confirmed = new Dictionary<string, (WorldPlacement Row, long At)>();
        var settled = new List<WorldEditorEditQueue.Submission>();

        WorldDocumentVersion Version(int index) => new(Activation: activation, Sequence: index);
        void Install(string id, WorldPlacement row) => documents.Add(item: new Dictionary<string, WorldPlacement>(dictionary: documents[^1]) { [id] = row });
        void Deliver(int index) {
            delivered = index;

            foreach (var id in ids) {
                if (confirmed.TryGetValue(key: id, value: out var held) && (held.At <= index)) {
                    _ = confirmed.Remove(key: id);
                    released++;
                }
            }

            Assert.True(condition: queue.Deliver(version: Version(index: index)));
        }

        for (var step = 0; (step < 6000); step++) {
            var id = ids[random.Next(maxValue: ids.Length)];

            switch (random.Next(maxValue: 10)) {
                case 0:
                case 1:
                case 2: {
                        var principal = principals[random.Next(maxValue: principals.Length)];
                        var row = Row(id: id, x: ++value);
                        var admission = Offer(delivered: documents[delivered][id], principal: principal, queue: queue, row: row, version: Version(index: delivered));

                        if (!inFlight.ContainsKey(key: id)) {
                            Assert.Equal(actual: admission.Submitted!.Value.Edit.Row, expected: row);
                            Assert.Equal(actual: admission.Submitted.Value.Edit.Mutation.Principal, expected: principal);
                            inFlight[id] = admission.Submitted.Value;
                        } else if ((queued[id].Count > 0) && (queued[id][^1].Mutation.Principal == principal)) {
                            Assert.Null(@object: admission.Submitted);
                            queued[id][^1] = admission.Queued!;
                            supersedes++;
                        } else {
                            Assert.Null(@object: admission.Submitted);
                            queued[id].Add(item: admission.Queued!);
                        }

                        break;
                    }
                case 3:
                case 4: {
                        if (!inFlight.Remove(key: id, value: out var submission)) {
                            break;
                        }

                        Install(id: id, row: submission.Edit.Row);

                        var at = (documents.Count - 1);
                        var settlement = queue.Settle(applied: true, id: id, token: submission.Token, version: Version(index: at));

                        settled.Add(item: submission);
                        confirms++;
                        Assert.True(condition: settlement.Answered);
                        Assert.False(condition: settlement.RolledBack);

                        if (at > delivered) {
                            confirmed[id] = (submission.Edit.Row, at);
                        }

                        if (queued[id].Count > 0) {
                            Assert.Same(actual: settlement.Next!.Value.Edit, expected: queued[id][0]);
                            queued[id].RemoveAt(index: 0);
                            inFlight[id] = settlement.Next.Value;
                        } else {
                            Assert.Null(@object: settlement.Next);
                        }

                        break;
                    }
                case 5: {
                        if (!inFlight.Remove(key: id, value: out var submission)) {
                            break;
                        }

                        var settlement = queue.Settle(applied: false, id: id, token: submission.Token, version: Version(index: delivered));

                        settled.Add(item: submission);
                        refusalsDropping += ((queued[id].Count > 0) ? 1 : 0);
                        Assert.True(condition: settlement.RolledBack);
                        Assert.Null(@object: settlement.Next);
                        Assert.Equal(actual: settlement.Dropped, expected: queued[id].Count);
                        Assert.Equal(actual: settlement.RolledBackTo, expected: (confirmed.TryGetValue(key: id, value: out var held) ? held.Row : null));
                        queued[id].Clear();

                        break;
                    }
                case 6:
                    if (delivered < (documents.Count - 1)) {
                        Deliver(index: ((delivered + 1) + random.Next(maxValue: ((documents.Count - 1) - delivered))));
                    }

                    break;
                case 7:
                    // Another door (an undo, a reload, another editor) installs a row this queue never submitted.
                    Install(id: id, row: Row(id: id, x: -(++value)));
                    undos++;

                    break;
                case 8: {
                        if (settled.Count == 0) {
                            break;
                        }

                        var old = settled[random.Next(maxValue: settled.Count)];

                        stale++;
                        Assert.Equal(
                            actual: queue.Settle(applied: (random.Next(maxValue: 2) == 0), id: old.Edit.Row.Id, token: old.Token, version: Version(index: delivered)),
                            expected: default
                        );

                        break;
                    }
                default: {
                        if (random.Next(maxValue: 4) != 0) {
                            break;
                        }

                        // A crossing onward or a teardown: the world goes, every edit it held is abandoned, nothing is kept, and
                        // a new activation starts from its own documents.
                        var expected = ids.SelectMany(selector: each => (inFlight.TryGetValue(key: each, value: out var flying) ? [flying.Edit] : Array.Empty<WorldEditorEditQueue.Edit>()).Concat(second: queued[each])).ToHashSet();
                        var retired = queue.Retire();

                        Assert.Equal(actual: retired.ToHashSet(), expected: expected);
                        Assert.Equal(actual: queue.Lines, expected: 0);
                        Assert.False(condition: Offer(delivered: null, principal: Principal.Console, queue: queue, row: Row(id: id, x: 0f), version: Version(index: delivered)).Admitted);
                        Assert.False(condition: queue.Deliver(version: new WorldDocumentVersion(Activation: Guid.NewGuid(), Sequence: 0L)));
                        abandoned += retired.Count;
                        crossings++;
                        activation = Guid.NewGuid();
                        queue = new WorldEditorEditQueue(activation: activation);
                        documents = [ids.ToDictionary(elementSelector: each => Row(id: each, x: -(++value)), keySelector: each => each)];
                        delivered = 0;
                        inFlight.Clear();
                        confirmed.Clear();
                        settled.Clear();

                        foreach (var each in ids) {
                            queued[each].Clear();
                        }

                        break;
                    }
            }

            // Every placement's base is its last queued edit, else the one in flight, else the confirmed value no delivered
            // document reflects yet, else the delivered row: never a document older than a confirmation.
            foreach (var each in ids) {
                var expected = ((queued[each].Count > 0)
                    ? queued[each][^1].Row
                    : (inFlight.TryGetValue(key: each, value: out var flying)
                        ? flying.Edit.Row
                        : (confirmed.TryGetValue(key: each, value: out var held) ? held.Row : documents[delivered][each])));

                Assert.Equal(actual: queue.Latest(delivered: documents[delivered][each], id: each, version: Version(index: delivered)), expected: expected);
            }

            Assert.Equal(actual: queue.Lines, expected: ids.Count(predicate: each => (inFlight.ContainsKey(key: each) || confirmed.ContainsKey(key: each))));
        }

        // The run reached every transition the invariants speak to.
        Assert.True(condition: (confirms > 100));
        Assert.True(condition: (refusalsDropping > 10));
        Assert.True(condition: (supersedes > 10));
        Assert.True(condition: (stale > 100));
        Assert.True(condition: (undos > 100));
        Assert.True(condition: (released > 50));
        Assert.True(condition: (crossings > 5));
        Assert.True(condition: (abandoned > 5));
    }
}
