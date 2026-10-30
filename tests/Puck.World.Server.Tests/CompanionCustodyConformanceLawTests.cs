using Puck.World.Protocol;
using Xunit;
using Model = Puck.World.Protocol.Tests;

namespace Puck.World.Server.Tests;

/// <summary>The engine's custody reducer (<see cref="WorldCompanionCustody"/>) folds the custody model's own records to
/// the model's titles. The law explores small scopes of the model (<c>CompanionCustodyModel.cs</c>, linked into this
/// suite), maps every home record sequence the model reaches onto engine records, and folds each through the engine at
/// every cut: after each prefix the engine's title (custody, custodian, epoch, pending hop, highest issued epoch,
/// release and recalls) and its owed notices equal the model's fold of the same prefix, and restoring the history of
/// any prefix and folding the rest yields the same state as folding all of it, which is what a checkpoint plus a log
/// redo does. Fidelity between the two is checked rather than reviewed.</summary>
public sealed class CompanionCustodyConformanceLawTests {
    private const string Home = "H";

    public static TheoryData<string, int, Model.Fault, int> Scopes => new() {
        { "AB", 1, Model.Fault.Install | Model.Fault.Recall | Model.Fault.Disconnect, 2 },
        { "AB", 1, Model.Fault.Loss | Model.Fault.Crash, 1 },
        { "A", 2, Model.Fault.Install | Model.Fault.Recall | Model.Fault.Disconnect | Model.Fault.Admission, 2 },
        { "ABH", 1, Model.Fault.Loss, 1 },
    };

    [Theory]
    [MemberData(nameof(Scopes))]
    public void TheEngineFoldsTheModelsHomeRecordsToTheModelsTitles(string route, int companions, Model.Fault allowed, int faults) {
        var sequences = HomeSequences(
            allowed: allowed,
            companions: companions,
            faults: faults,
            route: route
        );

        Assert.True(condition: sequences.Count > 10, userMessage: $"the scope reached only {sequences.Count} home record sequences");
        foreach (var records in sequences) {
            var engine = new WorldCompanionCustody(authority: static () => Home);
            var full = new WorldCompanionCustody(authority: static () => Home);

            foreach (var record in records.Select(selector: Map).OfType<WorldCrossingRecord>()) {
                full.Apply(record: record);
            }
            for (var cut = 0; (cut <= records.Count); cut++) {
                if (cut > 0 && (Map(record: records[cut - 1]) is { } mapped)) {
                    engine.Apply(record: mapped);
                }
                AssertConforms(companions: companions, engine: engine, model: records.Take(count: cut).ToList(), records: records);

                // A checkpoint at this cut, then the rest of the log redone.
                var restored = new WorldCompanionCustody(authority: static () => Home);

                restored.Restore(history: engine.History);
                foreach (var record in records.Skip(count: cut).Select(selector: Map).OfType<WorldCrossingRecord>()) {
                    restored.Apply(record: record);
                }
                for (var id = Model.Ids.C1; (id < (Model.Ids.C1 + companions)); id++) {
                    Assert.Equal(actual: Describe(title: restored.TitleOf(incarnation: Incarnation(id: id))!), expected: Describe(title: full.TitleOf(incarnation: Incarnation(id: id))!));
                }
            }
        }
    }

    // Every distinct record list home holds across the states a scope reaches.
    private static List<List<Model.Rec>> HomeSequences(string route, int companions, Model.Fault allowed, int faults) {
        var start = new Model.ModelState(aAccepts: true, bAccepts: true, route: [.. route.Select(selector: static c => Enum.Parse<Model.Node>(value: c.ToString()))], companions: companions);

        start.Budget.Allowed = allowed;
        start.Budget.Limit = 1;

        var visited = new HashSet<string>(comparer: StringComparer.Ordinal) { start.Canon() };
        var homes = new Dictionary<string, List<Model.Rec>>(comparer: StringComparer.Ordinal);
        var stack = new Stack<Model.ModelState>();

        stack.Push(item: start);
        while ((stack.Count > 0) && (visited.Count < 40_000)) {
            var state = stack.Pop();
            var records = state.Nodes[0].Records.ToList();

            homes.TryAdd(key: string.Join(';', records.Select(selector: static r => r.Canon)), value: records);
            foreach (var (_, next) in Model.CustodyModel.Successors(s: state, faultBudget: faults)) {
                if (visited.Add(item: next.Canon())) {
                    stack.Push(item: next);
                }
            }
        }
        return [.. homes.Values];
    }
    private static void AssertConforms(int companions, WorldCompanionCustody engine, List<Model.Rec> model, List<Model.Rec> records) {
        var node = new Model.NodeState(id: Model.Node.H, acceptsRole: true) { Records = model };
        var view = Model.CustodyModel.Fold(companions: companions, node: node);

        for (var id = Model.Ids.C1; (id < (Model.Ids.C1 + companions)); id++) {
            var expected = view.Custody[id];
            var title = engine.TitleOf(incarnation: Incarnation(id: id))!;
            var context = $"after {model.Count} of [{string.Join("; ", records.Select(selector: static r => r.Canon))}]";

            Assert.True(condition: (title.Kind.ToString() == expected.Kind.ToString()), userMessage: $"{id}: engine {title.Kind}, model {expected.Kind} {context}");
            Assert.True(condition: (title.Epoch == ((ulong)expected.Epoch)), userMessage: $"{id}: engine epoch {title.Epoch}, model {expected.Epoch} {context}");
            Assert.True(condition: (title.MaxIssued == ((ulong)expected.MaxIssued)), userMessage: $"{id}: engine max issued {title.MaxIssued}, model {expected.MaxIssued} {context}");
            Assert.True(condition: (title.Installed == expected.Installed), userMessage: $"{id}: engine installed {title.Installed}, model {expected.Installed} {context}");
            Assert.True(condition: title.Recalls.SequenceEqual(second: expected.Recalls.Select(selector: static e => (ulong)e)), userMessage: $"{id}: recalls differ {context}");
            if (expected.Kind == Model.CustodyKind.Pending) {
                Assert.True(condition: (title.PendingKey == Key(key: expected.PendingKey)), userMessage: $"{id}: engine pending {title.PendingKey}, model {expected.PendingKey} {context}");
            }
            if (expected.Kind is Model.CustodyKind.Away or Model.CustodyKind.Home) {
                Assert.True(condition: (title.Custodian == expected.Custodian.ToString()), userMessage: $"{id}: engine custodian {title.Custodian}, model {expected.Custodian} {context}");
            }

            var owedModel = expected.Recalls.Count(predicate: e => !view.Noticed.Contains(item: $"{id}:recalled@{e}")) + ((expected.Installed && !view.Noticed.Contains(item: $"{id}:released")) ? 1 : 0);

            Assert.True(condition: (engine.OwedNotices().Count(predicate: owed => owed.Incarnation == Incarnation(id: id)) == owedModel), userMessage: $"{id}: owed notices differ {context}");
        }
    }

    private static string Describe(WorldCustodyTitle title) => $"{title.Kind}:{title.Custodian}@{title.Epoch}/p{title.PendingKey}/m{title.MaxIssued}/i{title.Installed}/r{string.Join(',', title.Recalls)}";
    private static WorldEntityAddress Incarnation(int id) => new(Authority: Home, Index: (id - 1), Generation: 1);
    // A model key ("A1", "Hforged2") is its source node and a stable id.
    private static WorldTransferKey Key(string key) {
        var id = 14695981039346656037UL;

        foreach (var c in key) {
            id = ((id ^ c) * 1099511628211UL);
        }
        return new WorldTransferKey(SourceAuthority: key[..1], TransferId: id);
    }
    // The engine record a model record at home is, or null for one the custody reducer does not read there.
    private static WorldCrossingRecord? Map(Model.Rec record) => record switch {
        Model.Hop hop => new WorldCrossingRecord.Hop(
            From: hop.From.ToString(),
            Key: Key(key: hop.Key),
            Members: [.. hop.Members.Select(selector: static m => new WorldCustodyHopMember(Epoch: (ulong)m.Epoch, Incarnation: Incarnation(id: m.Id), Next: (ulong)m.Next))],
            To: hop.To.ToString()
        ),
        Model.HopDone done => new WorldCrossingRecord.HopDone(Arrived: done.Arrived, Incarnation: Incarnation(id: done.Id), Key: Key(key: done.Key)),
        Model.Recall recall => new WorldCrossingRecord.Recall(Epoch: (ulong)recall.NewEpoch, Incarnation: Incarnation(id: recall.Id)),
        Model.Install install => new WorldCrossingRecord.Release(Incarnation: Incarnation(id: install.Id)),
        Model.Noticed { What: "released" } noticed => new WorldCrossingRecord.Noticed(Epoch: 0UL, Incarnation: Incarnation(id: noticed.Id), What: WorldCustodyNotice.Released),
        Model.Noticed noticed => new WorldCrossingRecord.Noticed(Epoch: ulong.Parse(s: noticed.What["recalled@".Length..]), Incarnation: Incarnation(id: noticed.Id), What: WorldCustodyNotice.Recalled),
        Model.Arr { Readopted.Length: > 0 } arr => Readoption(arr: arr),
        _ => null,
    };
    private static WorldCrossingRecord.Arrival Readoption(Model.Arr arr) {
        var readopted = arr.Members.Where(predicate: m => arr.Readopted.Contains(value: m.Id)).ToArray();

        return new WorldCrossingRecord.Arrival(Value: new WorldCrossingArrival(
            Members: [.. readopted.Select(selector: static _ => new WorldTransferCommitMember(
                Arrival: WorldTransferArrival.Spawn,
                BodyMotionProgramName: string.Empty,
                PlanarVelocity: default,
                Position: default,
                Profile: null,
                VerticalVelocity: default,
                YawRadians: default
            ))],
            Readopted: [.. readopted.Select(selector: static m => Incarnation(id: m.Id))],
            Request: new WorldTransferReservationRequest(
                Border: "home",
                BorderCapacity: null,
                DeadlineSourceTick: 1UL,
                Members: [.. readopted.Select(selector: static (m, ordinal) => new WorldTransferReservationMember(
                    BodyColor: default,
                    CatalogRig: 0,
                    Identity: null,
                    MemberId: ordinal,
                    Mobility: new WorldMobilityIdentity(DepartedFrom: Incarnation(id: m.Id), Epoch: (ulong)(m.Epoch - 1), Incarnation: Incarnation(id: m.Id)),
                    PreferredSlot: m.Slot,
                    Principal: Puck.Commands.Principal.Console,
                    Source: IntentSource.Idle
                ))],
                PartyAllOrNothing: true,
                PeerAdmission: true,
                SourceAuthority: arr.From.ToString(),
                SourceRateHz: 30,
                SourceTick: 0UL,
                TransferId: Key(key: arr.Key).TransferId
            ),
            Slots: [.. readopted.Select(selector: static m => m.Slot)]
        ));
    }
}
