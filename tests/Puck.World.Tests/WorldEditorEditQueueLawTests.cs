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
/// never enters the queue, and a submission that throws is settled like a refusal, so no line stays in flight. A new
/// placement's id is minted against the delivered document and every edit in flight or queued. A confirmed value only
/// moves forward: a document older than it never replaces it. A refusal returned inline settles through the same path
/// as one arriving later and keeps its rollback. Each claim has a red leg, and a seeded random interleaving of
/// submissions, verdicts, stale verdicts and deliveries checks every invariant after every step.
/// </summary>
public sealed class WorldEditorEditQueueLawTests {
    private const string Nudge = "world.nudge crate1 x 1";

    private static readonly Vector3 Step = new(x: WorldEditorPlacementLawTests.Pitch, y: 0f, z: 0f);

    // A boot row, a world a seat crossed into over a link whose verdicts the law answers, and the lines echoed.
    private sealed class Rig : IDisposable {
        private readonly WorldAuthorityEndpoint m_away;
        private readonly HostRow m_row;

        public Rig() {
            var echoes = new WorldDeferredVerbEchoes();
            var routes = new WorldSeatAuthorityRouter();

            m_row = WorldEditorPlacementLawTests.Build();
            Away = WorldEditorPlacementLawTests.AwayDocument(row: m_row);
            Link = new RecordingLink(definition: Away);
            m_away = EditorEndpoints.Of(definition: Away, identity: "away", link: Link, pose: WorldEditorPlacementLawTests.AwayCrate);
            Registry = WorldEditorPlacementLawTests.BuildRegistry(echoes: echoes, routes: routes, row: m_row, seats: Seats);
            echoes.Completed += result => Lines.Add(item: result.Output);
            _ = routes.Publish(endpoint: m_away, entity: m_away.Mirror.Address(index: 0), slot: 0);
            Submit(line: $"world.grid pitch {WorldEditorPlacementLawTests.Pitch}");
        }

        public WorldDefinition Away { get; }

        public List<string> Lines { get; } = [];

        public RecordingLink Link { get; }
        public CommandRegistry Registry { get; }

        public WorldEditorSeats Seats { get; } = new();

        public void Dispose() {
            m_away.Dispose();
            m_row.Dispose();
        }
        public void Submit(string line) {
            var result = Registry.Submit(line: line);

            Assert.False(condition: result.IsError, userMessage: result.Output);
        }
    }

    private static WorldPlacement At(string id, float x) => new(
        Id: id,
        Position: new Vector3(x: x, y: 0f, z: 0f),
        PrototypeId: "crate",
        Scale: 1f,
        YawDegrees: 0f
    );
    private static WorldEditorEditQueue.Edit EditOf(WorldPlacement row, Principal principal) => new(
        Mutation: new WorldMutation.UpsertPlacement(Placement: row, Principal: principal),
        Row: row,
        Verb: WorldEditorCommandModule.NudgeCommand,
        World: "w"
    );

    [Fact]
    public void EachQueuedEditGoesOutUnderThePrincipalThatIssuedIt() {
        using var rig = new Rig();
        using var seat = new TextCommandSource(registry: rig.Registry).CreateSession(principal: Principal.Seat(slot: 0), slot: 0);

        // The console's nudge goes in flight; a second of its own queues, and the seat's queues behind it rather than
        // superseding it, since each is its own issuer's.
        rig.Submit(line: Nudge);
        rig.Submit(line: Nudge);

        var seated = rig.Registry.SubmitSession(line: Nudge, session: seat);

        Assert.False(condition: seated.IsError, userMessage: seated.Output);
        _ = Assert.Single(collection: rig.Link.Submitted);

        rig.Link.Complete(index: 0, result: RecordingLink.Applied);
        rig.Link.Complete(index: 1, result: RecordingLink.Applied);
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

        rig.Submit(line: Nudge);

        // A pitch this coarse moves the crate to infinity: refused by name at the verb, never queued.
        rig.Submit(line: "world.grid pitch 3e38");

        var infinite = rig.Registry.Submit(line: "world.nudge crate1 x 2");

        Assert.True(condition: infinite.IsError);
        Assert.Equal(actual: infinite.Output, expected: "[world.nudge: 'crate1' in 'away' refused: placement.position must contain finite coordinates.]");

        // Red leg: the row it would have queued cannot be serialized at all, which a callback composing it would throw.
        var unbounded = WorldDefinitionRows.FindPlacement(id: "crate1", placements: rig.Away.Placements)! with { Position = new Vector3(x: float.PositiveInfinity, y: 0f, z: 0f) };

        _ = Assert.ThrowsAny<ArgumentException>(testCode: () => JsonSerializer.Serialize(value: unbounded, jsonTypeInfo: WorldJsonContext.Default.WorldPlacement));

        // The verdict of the edit in flight submits nothing more: nothing was queued behind it.
        rig.Link.Complete(index: 0, result: RecordingLink.Applied);
        _ = Assert.Single(collection: rig.Link.Submitted);

        // A link that throws settles its submission refused, by name, and the line is free for the next edit.
        rig.Submit(line: $"world.grid pitch {WorldEditorPlacementLawTests.Pitch}");
        rig.Submit(line: Nudge);
        rig.Submit(line: Nudge);
        rig.Link.Fails = true;
        rig.Link.Complete(index: 1, result: RecordingLink.Applied);
        Assert.Contains(collection: rig.Lines, expected: "[world.nudge: 'crate1' in 'away' was not submitted: the link is closed]");
        Assert.Contains(collection: rig.Lines, expected: "[world.nudge: 'crate1' in 'away' rolled back to -2,0,2]");
        rig.Link.Fails = false;
        rig.Submit(line: Nudge);
        Assert.Equal(actual: rig.Link.Submitted.Count, expected: 3);
        Assert.Equal(actual: ((Vector3)rig.Link.Placement(index: 2).Position), expected: (WorldEditorPlacementLawTests.AwayCrate + (3f * Step)));
    }
    [Fact]
    public void PlacesMadeBeforeAnyVerdictMintDistinctIds() {
        using var rig = new Rig();

        rig.Seats.AimProbe = static _ => new WorldEditorRay(Direction: Vector3.UnitZ, Origin: Vector3.Zero);
        rig.Submit(line: "world.place crate");
        rig.Submit(line: "world.place crate");
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
    public void ADocumentOlderThanAConfirmedValueNeverReplacesIt() {
        var queue = new WorldEditorEditQueue();
        var origin = At(id: "a", x: 0f);
        var first = At(id: "a", x: 1f);
        var second = At(id: "a", x: 2f);
        var foreign = At(id: "a", x: 9f);

        _ = queue.Settle(applied: true, id: "a", token: queue.Offer(edit: EditOf(principal: Principal.Console, row: first), origin: origin)!.Value.Token, world: "w");
        _ = queue.Settle(applied: true, id: "a", token: queue.Offer(edit: EditOf(principal: Principal.Console, row: second), origin: first)!.Value.Token, world: "w");

        // The world confirmed both. A document from before either, or from between them, arriving after the verdicts,
        // never replaces the confirmed value.
        Assert.Equal(actual: queue.Latest(delivered: origin, id: "a", world: "w"), expected: second);
        Assert.Equal(actual: queue.Latest(delivered: first, id: "a", world: "w"), expected: second);
        Assert.True(condition: queue.IsReserved(id: "a", world: "w"));

        // Red leg: once a document shows the confirmed value the line is released, and the document leads from then on.
        Assert.Equal(actual: queue.Latest(delivered: second, id: "a", world: "w"), expected: second);
        Assert.False(condition: queue.IsReserved(id: "a", world: "w"));
        Assert.Equal(actual: queue.Latest(delivered: foreign, id: "a", world: "w"), expected: foreign);
    }
    [Fact]
    public void ARefusalReturnedInlineKeepsItsRollback() {
        using var rig = new Rig();

        rig.Link.Inline = RecordingLink.Applied;
        rig.Submit(line: Nudge);

        // Refused before the submission returns: the rollback to the confirmed value is named, and it holds.
        rig.Link.Inline = RecordingLink.Refused;

        var refused = rig.Registry.Submit(line: Nudge);

        Assert.True(condition: refused.IsError);
        Assert.Contains(collection: rig.Lines, expected: "[world.nudge: 'crate1' in 'away' rolled back to -2.5,0,2]");

        // The next edit starts from the confirmed value. Red leg: the delivered document still shows the crate where it
        // began, so a queue that forgot the rollback bases the edit there, one step short.
        rig.Link.Inline = null;
        rig.Submit(line: Nudge);
        Assert.Equal(actual: ((Vector3)rig.Link.Placement(index: 2).Position), expected: (WorldEditorPlacementLawTests.AwayCrate + (2f * Step)));
        Assert.Equal(actual: ((Vector3)WorldDefinitionRows.FindPlacement(id: "crate1", placements: rig.Away.Placements)!.Position), expected: WorldEditorPlacementLawTests.AwayCrate);
    }
    [Fact]
    public void ARandomInterleavingKeepsEveryInvariantAtEveryStep() {
        const string World = "w";

        var random = new Random(Seed: 1729);
        var queue = new WorldEditorEditQueue();
        string[] ids = ["a", "b", "c"];
        Principal[] principals = [Principal.Console, Principal.Seat(slot: 0), Principal.Seat(slot: 1)];
        // The world's applied rows per placement, oldest first; the delivered document shows one of them, and never
        // moves back.
        var applied = ids.ToDictionary(elementSelector: id => new List<WorldPlacement> { At(id: id, x: 0f) }, keySelector: id => id);
        var delivered = ids.ToDictionary(elementSelector: _ => 0, keySelector: id => id);
        var inFlight = new Dictionary<string, WorldEditorEditQueue.Submission>();
        var queued = ids.ToDictionary(elementSelector: _ => new List<WorldEditorEditQueue.Edit>(), keySelector: id => id);
        var settled = new List<WorldEditorEditQueue.Submission>();
        var value = 0f;

        var (confirms, refusalsDropping, supersedes, stale) = (0, 0, 0, 0);

        for (var step = 0; (step < 5000); step++) {
            var id = ids[random.Next(maxValue: ids.Length)];
            var shown = applied[id][delivered[id]];

            switch (random.Next(maxValue: 6)) {
                case 0:
                case 1: {
                        var principal = principals[random.Next(maxValue: principals.Length)];
                        var edit = EditOf(principal: principal, row: At(id: id, x: ++value));
                        var submission = queue.Offer(edit: edit, origin: queue.Latest(delivered: shown, id: id, world: World));

                        if (!inFlight.ContainsKey(key: id)) {
                            Assert.NotNull(@object: submission);
                            Assert.Same(actual: submission.Value.Edit, expected: edit);
                            inFlight[id] = submission.Value;
                        } else if ((queued[id].Count > 0) && (queued[id][^1].Mutation.Principal == principal)) {
                            Assert.Null(@object: submission);
                            queued[id][^1] = edit;
                            supersedes++;
                        } else {
                            Assert.Null(@object: submission);
                            queued[id].Add(item: edit);
                        }

                        break;
                    }
                case 2: {
                        if (!inFlight.Remove(key: id, value: out var submission)) {
                            break;
                        }

                        var settlement = queue.Settle(applied: true, id: id, token: submission.Token, world: World);

                        applied[id].Add(item: submission.Edit.Row);
                        settled.Add(item: submission);
                        confirms++;
                        Assert.True(condition: settlement.Answered);
                        Assert.False(condition: settlement.RolledBack);

                        if (queued[id].Count > 0) {
                            Assert.Same(actual: settlement.Next!.Value.Edit, expected: queued[id][0]);
                            queued[id].RemoveAt(index: 0);
                            inFlight[id] = settlement.Next.Value;
                        } else {
                            Assert.Null(@object: settlement.Next);
                        }

                        break;
                    }
                case 3: {
                        if (!inFlight.Remove(key: id, value: out var submission)) {
                            break;
                        }

                        var settlement = queue.Settle(applied: false, id: id, token: submission.Token, world: World);

                        settled.Add(item: submission);
                        refusalsDropping += ((queued[id].Count > 0) ? 1 : 0);
                        Assert.True(condition: settlement.RolledBack);
                        Assert.Null(@object: settlement.Next);
                        Assert.Equal(actual: settlement.Dropped, expected: queued[id].Count);
                        Assert.Equal(actual: settlement.RolledBackTo, expected: applied[id][^1]);
                        queued[id].Clear();

                        break;
                    }
                case 4:
                    delivered[id] = Math.Min(val1: (delivered[id] + 1), val2: (applied[id].Count - 1));

                    break;
                default: {
                        if (settled.Count == 0) {
                            break;
                        }

                        var old = settled[random.Next(maxValue: settled.Count)];

                        stale++;
                        Assert.Equal(
                            actual: queue.Settle(applied: (random.Next(maxValue: 2) == 0), id: old.Edit.Row.Id, token: old.Token, world: World),
                            expected: default
                        );

                        break;
                    }
            }

            // Every placement's base is its last queued edit, else the one in flight, else the world's last applied row,
            // however far the delivered document trails it.
            foreach (var each in ids) {
                var expected = ((queued[each].Count > 0)
                    ? queued[each][^1].Row
                    : (inFlight.TryGetValue(key: each, value: out var flying) ? flying.Edit.Row : applied[each][^1]));

                Assert.Equal(actual: queue.IsInFlight(id: each, world: World), expected: inFlight.ContainsKey(key: each));
                Assert.Equal(actual: queue.Latest(delivered: applied[each][delivered[each]], id: each, world: World), expected: expected);
                Assert.True(condition: (!inFlight.ContainsKey(key: each) || queue.IsReserved(id: each, world: World)));
            }
        }

        // The run reached every transition the invariants speak to.
        Assert.True(condition: (confirms > 100));
        Assert.True(condition: (refusalsDropping > 10));
        Assert.True(condition: (supersedes > 10));
        Assert.True(condition: (stale > 100));
    }
}
