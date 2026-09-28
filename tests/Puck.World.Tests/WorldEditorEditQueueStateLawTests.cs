using System.Numerics;

using Puck.Commands;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: <see cref="WorldEditorEditQueue"/> is a state machine every transition of which happens under its lock,
/// against the document its source delivers at that moment and against its retirement state. A confirmed value is
/// ordered by the world's document versions: a late document never replaces it, a newer one always does, and a
/// malformed version changes nothing. A base is read, its edit composed and admitted, and the edit sent, in one step,
/// so no verdict, delivery or retirement lands in between; a retired queue never sends, and an edit whose source no
/// longer reaches the world is abandoned rather than sent. Each claim has a red leg, and a seeded random interleaving
/// of offers from two endpoints, verdicts, stale verdicts, deliveries, malformed deliveries, other doors' installs,
/// endpoint closings and teardowns checks every invariant after every step.
/// </summary>
public sealed class WorldEditorEditQueueStateLawTests {
    private static readonly WorldDefinition Basis = Fixtures.BuildDocument();

    // A world as an endpoint reads it: its activation, the install its delivered document reflects, and its rows.
    private sealed class World {
        private Guid m_activation = Guid.NewGuid();

        private WorldDeliveredDocument? m_document;
        private long m_sequence;

        private IReadOnlyList<WorldPlacement> m_rows = [];

        public Guid Activation {
            get => m_activation;
            set {
                m_activation = value;
                m_document = null;
            }
        }
        public WorldDeliveredDocument Document => (m_document ??= new WorldDeliveredDocument(
            Definition: (Basis with { PlacementRowsRaw = [.. m_rows] }),
            Version: Version
        ));
        public WorldDocumentVersion Version => new(Activation: Activation, Sequence: m_sequence);

        // Delivers the document of one install: its rows, at its ordinal.
        public void Deliver(long sequence, IEnumerable<WorldPlacement> rows) {
            m_sequence = sequence;
            m_rows = [.. rows];
            m_document = null;
        }
        public WorldEditorEditQueue.Source SourceFor(IServerLink link) => new(Delivered: () => Document, Link: link, World: "w");
    }
    // Records what a queue sends, in order.
    private sealed class Sent {
        public List<WorldEditorEditQueue.Submission> Submissions { get; } = [];

        public CommandResult Send(WorldEditorEditQueue.Submission submission) {
            Submissions.Add(item: submission);

            return CommandResult.None;
        }
    }

    private static WorldPlacement Row(string id, float x) => new(
        Id: id,
        Position: new Vector3(x: x, y: 0f, z: 0f),
        PrototypeId: "crate",
        Scale: 1f,
        YawDegrees: 0f
    );
    private static WorldEditorEditQueue.Edit EditOf(WorldPlacement row, Principal principal, WorldEditorEditQueue.Source source) => new(
        Mutation: new WorldMutation.UpsertPlacement(Placement: row, Principal: principal),
        Row: row,
        Source: source,
        Verb: WorldEditorCommandModule.NudgeCommand
    );
    // An offer whose composer writes a fixed row whatever the base, as a law drives the queue directly.
    private static WorldEditorEditQueue.Admission Offer(WorldEditorEditQueue queue, WorldEditorEditQueue.Source source, WorldPlacement row, Principal principal) => queue.Offer(
        compose: (WorldDeliveredDocument document, WorldPlacement? basis, bool queues, out CommandResult refusal) => {
            refusal = CommandResult.None;

            return EditOf(principal: principal, row: row, source: source);
        },
        id: row.Id,
        source: source,
        verb: WorldEditorCommandModule.NudgeCommand
    );
    private static IServerLink LinkOf(WorldDefinition definition) => new RecordingLink(definition: definition);
    // A document that carries nothing but its version.
    private static WorldDeliveredDocument At(WorldDocumentVersion version) => new(Definition: Basis, Version: version);

    [Fact]
    public void ADocumentOlderThanAConfirmedValueNeverReplacesItAndANewerOneAlwaysDoes() {
        var world = new World();
        var sent = new Sent();
        var queue = new WorldEditorEditQueue(activation: world.Activation, send: sent.Send);
        var source = world.SourceFor(link: LinkOf(definition: Basis));
        var origin = Row(id: "a", x: 0f);
        var first = Row(id: "a", x: 1f);
        var second = Row(id: "a", x: 2f);

        WorldDocumentVersion Installed(long sequence) => new(Activation: world.Activation, Sequence: sequence);

        world.Deliver(rows: [origin], sequence: 0L);
        _ = Offer(principal: Principal.Console, queue: queue, row: first, source: source);
        _ = queue.Settle(applied: true, id: "a", token: sent.Submissions[^1].Token, version: Installed(sequence: 1L));
        _ = Offer(principal: Principal.Console, queue: queue, row: second, source: source);
        _ = queue.Settle(applied: true, id: "a", token: sent.Submissions[^1].Token, version: Installed(sequence: 2L));

        // The world confirmed both, at 1 and 2. A document from before either, or between them, never replaces the
        // confirmed value; nor does a malformed version.
        Assert.Equal(actual: queue.Latest(id: "a", source: source), expected: second);
        world.Deliver(rows: [first], sequence: 1L);
        Assert.Equal(actual: queue.Latest(id: "a", source: source), expected: second);
        Assert.True(condition: queue.Deliver(document: At(version: new WorldDocumentVersion(Activation: Guid.Empty, Sequence: 9L))));
        Assert.True(condition: queue.Deliver(document: At(version: new WorldDocumentVersion(Activation: world.Activation, Sequence: -1L))));
        Assert.Equal(actual: queue.Lines, expected: 1);

        // Red leg: a document at or past the confirmation releases the line whatever it shows. An undo that puts the
        // first value back is a newer document, and the next edit bases on it.
        world.Deliver(rows: [first], sequence: 3L);
        Assert.Equal(actual: queue.Latest(id: "a", source: source), expected: first);
        Assert.Equal(actual: queue.Lines, expected: 0);
    }
    [Fact]
    public void ABaseIsReadFromTheDocumentDeliveredAtAdmissionNeverOneCapturedBefore() {
        var world = new World();
        var sent = new Sent();
        var queue = new WorldEditorEditQueue(activation: world.Activation, send: sent.Send);
        var source = world.SourceFor(link: LinkOf(definition: Basis));
        var moved = Row(id: "a", x: 1f);
        WorldPlacement? basis = null;
        string? minted = null;

        world.Deliver(rows: [Row(id: "a", x: 0f)], sequence: 0L);

        var before = world.Document;

        _ = Offer(principal: Principal.Console, queue: queue, row: moved, source: source);
        _ = queue.Settle(applied: true, id: "a", token: sent.Submissions[^1].Token, version: world.Version with { Sequence = 1L });

        // The confirming document, which also holds a placement another door put down, arrives before the next offer.
        world.Deliver(rows: [moved, Row(id: "a1", x: 5f)], sequence: 1L);
        _ = queue.Offer(
            compose: (WorldDeliveredDocument document, WorldPlacement? read, bool queues, out CommandResult refusal) => {
                basis = read;
                refusal = CommandResult.None;

                return null;
            },
            id: "a",
            source: source,
            verb: WorldEditorCommandModule.NudgeCommand
        );
        _ = queue.OfferNew(
            compose: (WorldDeliveredDocument document, Func<string, WorldPlacement?> latest, Func<string, bool> taken, Func<string, string> mint, out CommandResult refusal) => {
                minted = mint(arg: "a");
                refusal = CommandResult.None;

                return null;
            },
            source: source,
            verb: WorldEditorCommandModule.PlaceCommand
        );
        Assert.Equal(actual: basis, expected: moved);
        Assert.Equal(actual: minted, expected: "a2");

        // Red leg: the document captured before the confirmation shows the unmoved row and no a1, so a base or an id taken
        // from it would overwrite the confirmed move or reuse the placement just installed.
        Assert.NotEqual(actual: WorldDefinitionRows.FindPlacement(id: "a", placements: before.Definition.Placements), expected: moved);
        Assert.Null(@object: WorldDefinitionRows.FindPlacement(id: "a1", placements: before.Definition.Placements));
    }
    [Fact]
    public void ARetiredQueueNeverSendsAndAMovedSourceAbandonsRatherThanSends() {
        var world = new World();
        var other = new World();
        var sent = new Sent();
        Thread? retiring = null;
        IReadOnlyList<WorldEditorEditQueue.Edit>? abandoned = null;
        WorldEditorEditQueue queue = null!;

        queue = new WorldEditorEditQueue(activation: world.Activation, send: submission => {
            _ = sent.Send(submission: submission);

            // A retirement from another thread while the next edit is being sent waits until the send is done.
            if (sent.Submissions.Count == 2) {
                retiring = new Thread(start: () => abandoned = queue.Retire());
                retiring.Start();
                Assert.False(condition: retiring.Join(millisecondsTimeout: 100));
            }

            return CommandResult.None;
        });

        var source = world.SourceFor(link: LinkOf(definition: Basis));

        world.Deliver(rows: [Row(id: "a", x: 0f)], sequence: 0L);
        _ = Offer(principal: Principal.Console, queue: queue, row: Row(id: "a", x: 1f), source: source);
        _ = Offer(principal: Principal.Seat(slot: 0), queue: queue, row: Row(id: "a", x: 2f), source: source);

        var settled = queue.Settle(applied: true, id: "a", token: sent.Submissions[0].Token, version: world.Version with { Sequence = 1L });

        retiring!.Join();
        Assert.NotNull(@object: settled.Next);
        Assert.Contains(collection: abandoned!, expected: settled.Next);

        // Retired: a verdict for the edit it sent answers nothing and sends nothing; nothing is admitted.
        Assert.False(condition: queue.Settle(applied: true, id: "a", token: sent.Submissions[1].Token, version: world.Version with { Sequence = 2L }).Answered);
        Assert.Null(@object: Offer(principal: Principal.Console, queue: queue, row: Row(id: "a", x: 3f), source: source).Admitted);
        Assert.Equal(actual: sent.Submissions.Count, expected: 2);

        // A queued edit whose endpoint now reaches another world is abandoned when its turn comes, never sent.
        var moving = new World { Activation = world.Activation };
        var fresh = new Sent();
        var live = new WorldEditorEditQueue(activation: world.Activation, send: fresh.Send);
        var near = world.SourceFor(link: LinkOf(definition: Basis));
        var far = moving.SourceFor(link: LinkOf(definition: Basis));

        moving.Deliver(rows: [Row(id: "a", x: 0f)], sequence: 0L);
        _ = Offer(principal: Principal.Console, queue: live, row: Row(id: "a", x: 1f), source: near);
        _ = Offer(principal: Principal.Seat(slot: 0), queue: live, row: Row(id: "a", x: 2f), source: far);
        moving.Activation = other.Activation;

        var turned = live.Settle(applied: true, id: "a", token: fresh.Submissions[0].Token, version: world.Version with { Sequence = 1L });

        Assert.Null(@object: turned.Next);
        _ = Assert.Single(collection: turned.Abandoned);
        _ = Assert.Single(collection: fresh.Submissions);

        // Red leg: when it queued, that endpoint still reached this world, so it was admitted.
        Assert.NotEqual(actual: moving.Activation, expected: world.Activation);
    }
    [Fact]
    public void ARefusalCannotSettleBetweenABaseReadAndItsAdmission() {
        var world = new World();
        var sent = new Sent();
        var queue = new WorldEditorEditQueue(activation: world.Activation, send: sent.Send);
        var source = world.SourceFor(link: LinkOf(definition: Basis));
        var origin = Row(id: "a", x: 0f);
        Thread? refusal = null;
        WorldEditorEditQueue.Settlement settled = default;

        world.Deliver(rows: [origin], sequence: 0L);
        _ = Offer(principal: Principal.Seat(slot: 0), queue: queue, row: Row(id: "a", x: 1f), source: source);

        var first = sent.Submissions[0];

        // The console composes on the seat's edit in flight; the world refuses that edit from another thread while the
        // console is composing. The refusal waits for the admission, then drops the console's edit composed on it.
        var admission = queue.Offer(
            compose: (WorldDeliveredDocument document, WorldPlacement? basis, bool queues, out CommandResult composeRefusal) => {
                refusal = new Thread(start: () => settled = queue.Settle(applied: false, id: "a", token: first.Token, version: world.Version));
                refusal.Start();
                Assert.False(condition: refusal.Join(millisecondsTimeout: 100));
                composeRefusal = CommandResult.None;

                return EditOf(principal: Principal.Console, row: (basis! with { Position = new Vector3(x: 2f, y: 0f, z: 0f) }), source: source);
            },
            id: "a",
            source: source,
            verb: WorldEditorCommandModule.NudgeCommand
        );

        refusal!.Join();
        Assert.True(condition: admission.Queued);
        Assert.True(condition: settled.RolledBack);
        Assert.Equal(actual: settled.Dropped, expected: 1);
        Assert.Equal(actual: settled.RolledBackTo, expected: origin);
        Assert.Equal(actual: queue.Latest(id: "a", source: source), expected: origin);

        // Red leg: with the read and the admission apart, the refusal lands between them and the console's edit, composed
        // on the refused one, goes out on its own under the console.
        var apart = new WorldEditorEditQueue(activation: world.Activation, send: sent.Send);

        _ = Offer(principal: Principal.Seat(slot: 0), queue: apart, row: Row(id: "a", x: 1f), source: source);

        var seat = sent.Submissions[^1];
        var read = apart.Latest(id: "a", source: source)!;

        _ = apart.Settle(applied: false, id: "a", token: seat.Token, version: world.Version);
        Assert.False(condition: Offer(principal: Principal.Console, queue: apart, row: (read with { Position = new Vector3(x: 2f, y: 0f, z: 0f) }), source: source).Queued);
        Assert.Equal(actual: sent.Submissions[^1].Edit.Row.Position.X, expected: 2f);
    }
    [Fact]
    public void AnEndpointThatLagsIsBasedOnTheNewestStateAnyEndpointDelivered() {
        var fast = new World();
        var slow = new World { Activation = fast.Activation };
        var sent = new Sent();
        var queue = new WorldEditorEditQueue(activation: fast.Activation, send: sent.Send);
        var near = fast.SourceFor(link: LinkOf(definition: Basis));
        var far = slow.SourceFor(link: LinkOf(definition: Basis));
        var origin = Row(id: "a", x: 0f);
        var moved = Row(id: "a", x: 1f);
        WorldPlacement? basis = null;

        fast.Deliver(rows: [origin], sequence: 0L);
        slow.Deliver(rows: [origin], sequence: 0L);
        _ = Offer(principal: Principal.Console, queue: queue, row: moved, source: near);
        _ = queue.Settle(applied: true, id: "a", token: sent.Submissions[^1].Token, version: fast.Version with { Sequence = 1L });

        // The fast endpoint delivers the confirming document; the slow one has not yet.
        fast.Deliver(rows: [moved], sequence: 1L);
        Assert.True(condition: queue.Deliver(document: fast.Document));
        _ = queue.Offer(
            compose: (WorldDeliveredDocument document, WorldPlacement? read, bool queues, out CommandResult refusal) => {
                basis = read;
                refusal = CommandResult.None;

                return null;
            },
            id: "a",
            source: far,
            verb: WorldEditorCommandModule.NudgeCommand
        );
        Assert.Equal(actual: basis, expected: moved);

        // Red leg: the slow endpoint's own document still shows the row before the confirmed move, so an edit based on
        // it would overwrite that move.
        Assert.Equal(actual: WorldDefinitionRows.FindPlacement(id: "a", placements: slow.Document.Definition.Placements), expected: origin);
    }
    [Fact]
    public void ARandomInterleavingKeepsEveryInvariantAtEveryStep() {
        var random = new Random(Seed: 1729);
        string[] ids = ["a", "b", "c"];
        Principal[] principals = [Principal.Console, Principal.Seat(slot: 0), Principal.Seat(slot: 1)];
        IServerLink[] links = [LinkOf(definition: Basis), LinkOf(definition: Basis)];
        var value = 0f;

        var (confirms, refusalsDropping, supersedes, stale, undos, released, crossings, abandoned, closings, malformed, crossSource, lagging) = (0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        // The world behind the queue: its documents in install order, each a row per placement. Each endpoint has its
        // own view of it, delivered at its own pace and never moving back; the newest either has delivered is the world's
        // known state. The model of each line: the edit in flight, the edits queued, and the confirmed value no known
        // state reflects yet with the install it applied at.
        var activation = Guid.NewGuid();
        World[] views = [new World { Activation = activation }, new World { Activation = activation }];
        var sent = new Sent();
        var queue = new WorldEditorEditQueue(activation: activation, send: sent.Send);
        var documents = new List<Dictionary<string, WorldPlacement>> { ids.ToDictionary(elementSelector: id => Row(id: id, x: 0f), keySelector: id => id) };
        int[] delivered = [0, 0];
        var newest = 0;
        var inFlight = new Dictionary<string, WorldEditorEditQueue.Submission>();
        var queued = ids.ToDictionary(elementSelector: _ => new List<WorldEditorEditQueue.Edit>(), keySelector: id => id);
        var confirmed = new Dictionary<string, (WorldPlacement Row, long At)>();
        var settled = new List<WorldEditorEditQueue.Submission>();
        WorldEditorEditQueue.Source[] sources = [views[0].SourceFor(link: links[0]), views[1].SourceFor(link: links[1])];

        // One endpoint delivers a document: its mirror hands it to the queue, as it would on arrival.
        void Show(int view, int index) {
            delivered[view] = index;
            views[view].Deliver(rows: documents[index].Values, sequence: index);
            Assert.True(condition: queue.Deliver(document: views[view].Document));

            if (index <= newest) {
                return;
            }

            newest = index;

            foreach (var each in ids) {
                if (confirmed.TryGetValue(key: each, value: out var held) && (held.At <= newest)) {
                    _ = confirmed.Remove(key: each);
                    released++;
                }
            }
        }
        void Install(string id, WorldPlacement row) => documents.Add(item: new Dictionary<string, WorldPlacement>(dictionary: documents[^1]) { [id] = row });
        IEnumerable<WorldEditorEditQueue.Edit> EditsOf(string id) => ((inFlight.TryGetValue(key: id, value: out var flying) ? [flying.Edit] : Array.Empty<WorldEditorEditQueue.Edit>()).Concat(second: queued[id]));

        Show(index: 0, view: 0);
        Show(index: 0, view: 1);

        for (var step = 0; (step < 6000); step++) {
            var id = ids[random.Next(maxValue: ids.Length)];

            switch (random.Next(maxValue: 12)) {
                case 0:
                case 1:
                case 2: {
                        var principal = principals[random.Next(maxValue: principals.Length)];
                        var source = sources[random.Next(maxValue: sources.Length)];
                        var row = Row(id: id, x: ++value);
                        var count = sent.Submissions.Count;
                        var admission = Offer(principal: principal, queue: queue, row: row, source: source);

                        if (!inFlight.ContainsKey(key: id)) {
                            Assert.False(condition: admission.Queued);
                            Assert.Equal(actual: sent.Submissions.Count, expected: (count + 1));
                            Assert.Same(actual: sent.Submissions[^1].Edit, expected: admission.Admitted);
                            Assert.Same(actual: sent.Submissions[^1].Edit.Source, expected: source);
                            inFlight[id] = sent.Submissions[^1];
                        } else {
                            Assert.True(condition: admission.Queued);
                            Assert.Equal(actual: sent.Submissions.Count, expected: count);
                            crossSource += ((((queued[id].Count > 0) ? queued[id][^1].Source : inFlight[id].Edit.Source) != source) ? 1 : 0);

                            if ((queued[id].Count > 0) && (queued[id][^1].Mutation.Principal == principal)) {
                                queued[id][^1] = admission.Admitted!;
                                supersedes++;
                            } else {
                                queued[id].Add(item: admission.Admitted!);
                            }
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
                        var count = sent.Submissions.Count;
                        var settlement = queue.Settle(applied: true, id: id, token: submission.Token, version: new WorldDocumentVersion(Activation: activation, Sequence: at));

                        settled.Add(item: submission);
                        confirms++;
                        Assert.True(condition: settlement.Answered);
                        Assert.False(condition: settlement.RolledBack);
                        confirmed[id] = (submission.Edit.Row, at);

                        if (queued[id].Count > 0) {
                            Assert.Same(actual: settlement.Next, expected: queued[id][0]);
                            Assert.Same(actual: sent.Submissions[^1].Edit, expected: queued[id][0]);
                            queued[id].RemoveAt(index: 0);
                            inFlight[id] = sent.Submissions[^1];
                        } else {
                            Assert.Null(@object: settlement.Next);
                            Assert.Equal(actual: sent.Submissions.Count, expected: count);
                        }

                        break;
                    }
                case 5: {
                        if (!inFlight.Remove(key: id, value: out var submission)) {
                            break;
                        }

                        var settlement = queue.Settle(applied: false, id: id, token: submission.Token, version: views[0].Version);

                        settled.Add(item: submission);
                        refusalsDropping += ((queued[id].Count > 0) ? 1 : 0);
                        Assert.True(condition: settlement.RolledBack);
                        Assert.Null(@object: settlement.Next);
                        Assert.Equal(actual: settlement.Dropped, expected: queued[id].Count);
                        Assert.Equal(actual: settlement.RolledBackTo, expected: (confirmed.TryGetValue(key: id, value: out var held) ? held.Row : documents[newest][id]));
                        queued[id].Clear();

                        break;
                    }
                case 6: {
                        // One endpoint, chosen at random, delivers a later document; the other lags.
                        var view = random.Next(maxValue: views.Length);

                        if (delivered[view] < (documents.Count - 1)) {
                            Show(index: ((delivered[view] + 1) + random.Next(maxValue: ((documents.Count - 1) - delivered[view]))), view: view);
                            lagging += ((delivered[0] != delivered[1]) ? 1 : 0);
                        }

                        break;
                    }
                case 7:
                    // Another door (an undo, a reload, another editor) installs a row this queue never submitted.
                    Install(id: id, row: Row(id: id, x: -(++value)));
                    undos++;

                    break;
                case 8: {
                        malformed++;
                        Assert.True(condition: queue.Deliver(document: At(version: ((random.Next(maxValue: 2) == 0)
                            ? new WorldDocumentVersion(Activation: Guid.Empty, Sequence: (documents.Count + 5))
                            : new WorldDocumentVersion(Activation: activation, Sequence: -1L)))));

                        if (settled.Count > 0) {
                            var old = settled[random.Next(maxValue: settled.Count)];

                            stale++;
                            Assert.False(condition: queue.Settle(applied: (random.Next(maxValue: 2) == 0), id: old.Edit.Row.Id, token: old.Token, version: views[0].Version).Answered);
                        }

                        break;
                    }
                case 9: {
                        // One endpoint closes: every edit that goes out through it, with everything queued behind it, is
                        // abandoned; the other endpoint's edits ahead of it stay.
                        var link = links[random.Next(maxValue: links.Length)];
                        var expected = new List<WorldEditorEditQueue.Edit>();

                        foreach (var each in ids) {
                            var edits = EditsOf(id: each).ToList();
                            var from = edits.FindIndex(match: edit => ReferenceEquals(objA: edit.Source.Link, objB: link));

                            if (from < 0) {
                                continue;
                            }

                            expected.AddRange(collection: edits.Skip(count: from));

                            if ((from == 0) && inFlight.Remove(key: each, value: out var flying)) {
                                settled.Add(item: flying);
                                queued[each].Clear();
                            } else {
                                queued[each].RemoveRange(count: (queued[each].Count - (from - 1)), index: (from - 1));
                            }
                        }

                        closings++;
                        Assert.Equal(actual: queue.Abandon(link: link).ToHashSet(), expected: expected.ToHashSet());

                        break;
                    }
                default: {
                        if (random.Next(maxValue: 4) != 0) {
                            break;
                        }

                        // A crossing onward or a teardown: the world goes, every edit it held is abandoned, nothing is kept or
                        // sent, and a new activation starts from its own documents.
                        var expected = ids.SelectMany(selector: EditsOf).ToHashSet();
                        var retired = queue.Retire();
                        var count = sent.Submissions.Count;

                        Assert.Equal(actual: retired.ToHashSet(), expected: expected);
                        Assert.Equal(actual: queue.Lines, expected: 0);
                        Assert.Null(@object: Offer(principal: Principal.Console, queue: queue, row: Row(id: id, x: 0f), source: sources[0]).Admitted);

                        foreach (var flying in inFlight.Values) {
                            Assert.False(condition: queue.Settle(applied: true, id: flying.Edit.Row.Id, token: flying.Token, version: views[0].Version).Answered);
                        }

                        Assert.Equal(actual: sent.Submissions.Count, expected: count);
                        Assert.False(condition: queue.Deliver(document: At(version: new WorldDocumentVersion(Activation: Guid.NewGuid(), Sequence: 0L))));
                        abandoned += retired.Count;
                        crossings++;
                        activation = Guid.NewGuid();
                        views[0].Activation = activation;
                        views[1].Activation = activation;
                        queue = new WorldEditorEditQueue(activation: activation, send: sent.Send);
                        documents = [ids.ToDictionary(elementSelector: each => Row(id: each, x: -(++value)), keySelector: each => each)];
                        newest = 0;
                        inFlight.Clear();
                        confirmed.Clear();
                        settled.Clear();

                        foreach (var each in ids) {
                            queued[each].Clear();
                        }

                        Show(index: 0, view: 0);
                        Show(index: 0, view: 1);

                        break;
                    }
            }

            // Every placement's base is its last queued edit, else the one in flight, else the confirmed value no known
            // state reflects yet, else its row in the newest document either endpoint delivered: never a document older
            // than a confirmation, whichever endpoint reads it.
            foreach (var each in ids) {
                var expected = ((queued[each].Count > 0)
                    ? queued[each][^1].Row
                    : (inFlight.TryGetValue(key: each, value: out var flying)
                        ? flying.Edit.Row
                        : (confirmed.TryGetValue(key: each, value: out var held) ? held.Row : documents[newest][each])));

                Assert.Equal(actual: queue.Latest(id: each, source: sources[0]), expected: expected);
                Assert.Equal(actual: queue.Latest(id: each, source: sources[1]), expected: expected);
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
        Assert.True(condition: (closings > 100));
        Assert.True(condition: (malformed > 100));
        Assert.True(condition: (crossSource > 50));
        Assert.True(condition: (lagging > 100));
    }
}
