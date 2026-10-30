using System.Text;
using Puck.Commands;
using Puck.World.Protocol;
using Xunit;
using Model = Puck.World.Protocol.Tests;

namespace Puck.World.Server.Tests;

/// <summary>The engine refines the companion custody model (<c>CompanionCustodyModel.cs</c>, linked into this suite):
/// real authorities (<see cref="CompanionRows"/>) run the player's crossings while the model follows. Every engine
/// observable, a peer call at the <see cref="IWorldPeerCall"/> and <see cref="IWorldCustodyPeer"/> seams or a crossing-log
/// append, must be a transition the model enables from its current state: a call is the delivery of the model's message
/// of that kind between those authorities under that key, and an append is the record the model's handler or progress
/// move appends. Records are compared at every append and the whole derived state (titles, embodiments and epochs,
/// bindings, slot generations, leases, in-doubt departures and owed obligations) at every drain boundary, and at the end
/// every authority restarts from its image plus its log and is compared again. Faults are schedules over the decision
/// points a fault-free run discovers: loss and duplication of any call, a crash before or after any append with recovery
/// from checkpoint plus log, a refused or uncertain append, a release, a destination refusing one companion at the
/// landing, a stranger filling the destination's cap, and a former custodian forging a reservation.</summary>
public sealed class CompanionCustodyRefinementLawTests(Xunit.ITestOutputHelper output) {
    public static TheoryData<string, int, bool, int> Scopes => new() {
        { "A", 1, true, 2 },
        { "AB", 1, true, 2 },
        { "A", 2, true, 2 },
        { "A", 2, true, 1 },
        { "A", 1, false, 2 },
    };

    [Theory]
    [MemberData(nameof(Scopes))]
    public void TheEngineRefinesTheModelUnderEveryScheduledFault(string route, int companions, bool bDeclaresRole, int bPeerSlots) {
        var scope = new Scope(Route: route, Companions: companions, BDeclaresRole: bDeclaresRole, BPeerSlots: bPeerSlots);
        var recording = Refinement.Run(scope: scope, schedule: null, recorded: null);
        var schedules = Schedules(points: recording.Points).ToArray();
        var failures = new List<string>();
        var deferred = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        var comparisons = recording.Comparisons;
        var observables = recording.Observables;

        failures.AddRange(collection: recording.Divergences.Select(selector: d => $"[fault-free] {d}"));
        foreach (var schedule in schedules) {
            var run = Refinement.Run(scope: scope, schedule: schedule, recorded: recording.Points);

            comparisons += run.Comparisons;
            observables += run.Observables;
            failures.AddRange(collection: run.Divergences.Select(selector: d => $"[{schedule.Name}] {d}"));
            foreach (var move in run.Deferred) {
                deferred[move] = (deferred.GetValueOrDefault(key: move) + 1);
            }
        }
        output.WriteLine(message: $"{scope}: {schedules.Length + 1} schedules, {observables} engine observables matched, {comparisons} state comparisons; deferred (unbuilt steps): {string.Join(", ", deferred.OrderBy(static p => p.Key, StringComparer.Ordinal).Select(static p => $"{p.Key}×{p.Value}"))}");
        Assert.True(condition: (failures.Count == 0), userMessage: string.Join(Environment.NewLine, failures.Take(count: 12)));
    }

    // Every single-fault schedule over the recording's decision points, plus a checkpoint ahead of each crash.
    private static IEnumerable<Schedule> Schedules(IReadOnlyList<Point> points) {
        for (var index = 0; (index < points.Count); index++) {
            var point = points[index];

            switch (point.Kind) {
                case PointKind.Call:
                    yield return new Schedule(Name: $"drop request #{index} {point.Text}", Fault: Fault.DropRequest, Point: index);
                    yield return new Schedule(Name: $"drop reply #{index} {point.Text}", Fault: Fault.DropReply, Point: index);
                    yield return new Schedule(Name: $"dup #{index} {point.Text}", Fault: Fault.Dup, Point: index);
                    break;
                case PointKind.Append:
                    yield return new Schedule(Name: $"crash before #{index} {point.Text}", Fault: Fault.CrashBefore, Point: index);
                    yield return new Schedule(Name: $"crash after #{index} {point.Text}", Fault: Fault.CrashAfter, Point: index);
                    yield return new Schedule(Name: $"crash after #{index} {point.Text} (checkpointed)", Fault: Fault.CrashAfter, Point: index, Checkpoint: true);
                    yield return new Schedule(Name: $"refused #{index} {point.Text}", Fault: Fault.LogRefused, Point: index);
                    yield return new Schedule(Name: $"uncertain lost #{index} {point.Text}", Fault: Fault.LogUncertainLost, Point: index);
                    yield return new Schedule(Name: $"uncertain landed #{index} {point.Text}", Fault: Fault.LogUncertainLanded, Point: index);
                    break;
                case PointKind.Boundary:
                    yield return new Schedule(Name: $"install #{index}", Fault: Fault.Install, Point: index);
                    yield return new Schedule(Name: $"stranger #{index}", Fault: Fault.Stranger, Point: index);
                    yield return new Schedule(Name: $"forge #{index}", Fault: Fault.Forge, Point: index);
                    yield return new Schedule(Name: $"refuse at commit #{index}", Fault: Fault.RefuseAtCommit, Point: index);
                    break;
            }
        }
    }

    private enum PointKind : byte { Call, Append, Boundary }
    private enum Fault : byte { None, DropRequest, DropReply, Dup, CrashBefore, CrashAfter, LogRefused, LogUncertainLost, LogUncertainLanded, Install, Stranger, Forge, RefuseAtCommit }
    // What produced an append in the recording: the handler of an active call at the appending authority, the reply of a
    // call that just returned to it, or the authority's own drain.
    private enum Producer : byte { Drain, Handler, Reply }
    private enum Expected : byte { Delivered, Missing, Unmodelled }

    private sealed record Scope(string Route, int Companions, bool BDeclaresRole, int BPeerSlots) {
        public override string ToString() => $"route {Route}, {Companions} companion(s), B {(BDeclaresRole ? "accepts" : "declares no role")}, {BPeerSlots} peer slot(s)";
    }
    private sealed record Point(PointKind Kind, Model.Node Node, string Text, Producer Producer = Producer.Drain, int ProducerCall = -1);
    private sealed record Schedule(string Name, Fault Fault, int Point, bool Checkpoint = false);
    private sealed record Outcome(IReadOnlyList<Point> Points, IReadOnlyList<string> Divergences, IReadOnlyList<string> Deferred, int Observables, int Comparisons);

    /// <summary>One run: the engine under one schedule, the model following.</summary>
    private sealed class Refinement : IDisposable {
        private static readonly string[] Unbuilt = ["sweep", "strand", "shed", "retire", "notify", "resolve", "retry", "abandon"];
        private static readonly Dictionary<Model.Node, string> RowNames = new() { [Model.Node.H] = "row-a", [Model.Node.A] = "row-b", [Model.Node.B] = "row-c" };

        private readonly Scope m_scope;
        private readonly Schedule? m_schedule;
        private readonly IReadOnlyList<Point>? m_recorded;
        private readonly List<Point> m_points = [];
        private readonly List<string> m_divergences = [];
        private readonly List<string> m_deferred = [];
        private readonly List<string> m_trace = [];
        private readonly Dictionary<WorldTransferKey, string> m_keyNames = new();
        private readonly Dictionary<string, WorldTransferKey> m_keysOf = new(comparer: StringComparer.Ordinal);
        private readonly Dictionary<WorldTransferKey, Dictionary<int, int>> m_memberIds = new();
        private readonly Dictionary<Model.Node, Dictionary<int, int>> m_slots = new();
        private readonly Dictionary<(Model.Node Node, int Slot), int> m_generationBase = new();
        private readonly HashSet<Model.Node> m_down = [];
        private readonly ulong?[] m_pendingTransfer = new ulong?[3];
        private readonly int[] m_consumed = new int[3];
        private readonly List<(int Call, Model.Node From, Model.Node To)> m_activeCalls = [];
        private readonly Dictionary<Model.Node, bool> m_crashBeforeArmed = new();

        private CompanionRows m_rows = null!;
        private Model.ModelState m_model = null!;
        private (int Call, Model.Node From, Model.MsgKind Kind)? m_lastReturned;
        private int m_silent;
        private readonly System.Diagnostics.Stopwatch m_clock = System.Diagnostics.Stopwatch.StartNew();
        private int m_observables;
        private int m_comparisons;
        private int m_expanded;
        private bool m_faultSpent;
        private bool m_stepHadObservable;
        private readonly Dictionary<string, int> m_refuseAtCommit = new(comparer: StringComparer.Ordinal);

        private Refinement(Scope scope, Schedule? schedule, IReadOnlyList<Point>? recorded) {
            m_scope = scope;
            m_schedule = schedule;
            m_recorded = recorded;
        }

        public static Outcome Run(Scope scope, Schedule? schedule, IReadOnlyList<Point>? recorded) {
            using var run = new Refinement(scope: scope, schedule: schedule, recorded: recorded);

            try {
                run.Drive();
            } catch (Exception exception) when (exception is not Xunit.Sdk.XunitException) {
                run.Diverge(text: $"the run threw {exception.GetType().Name}: {exception.Message}{Environment.NewLine}{string.Join(Environment.NewLine, (exception.StackTrace ?? string.Empty).Split(Environment.NewLine).Take(count: 12))}");
            }
            return new Outcome(Comparisons: run.m_comparisons, Deferred: run.m_deferred, Divergences: run.m_divergences, Observables: run.m_observables, Points: run.m_points);
        }
        public void Dispose() => m_rows?.Dispose();

        // ---- the drive -------------------------------------------------------------------------------------------

        private void Drive() {
            m_model = new Model.ModelState(aAccepts: m_scope.BDeclaresRole, bAccepts: true, route: [.. m_scope.Route.Select(selector: static c => Enum.Parse<Model.Node>(value: c.ToString()))], companions: m_scope.Companions);
            m_model.Budget.Limit = 100;
            // A custodian seats the player and offers its peer slots to companions.
            m_model.CustodianSlots = (1 + m_scope.BPeerSlots);
            m_rows = new CompanionRows(bDeclaresRole: m_scope.BDeclaresRole, bPeerSlots: m_scope.BPeerSlots, companions: m_scope.Companions, logs: (name, log) => new ObservedLog(Node: NodeOfRow(name: name), Inner: log, Owner: this), retained: true);
            m_rows.Admitted = InstallSeams;
            foreach (var row in m_rows.All) {
                InstallSeams(row: row);
            }
            foreach (var node in Enum.GetValues<Model.Node>()) {
                var row = RowOf(node: node);

                m_slots[node] = new Dictionary<int, int>();
                for (var slot = 0; (slot < row.Server.Population.Capacity); slot++) {
                    m_generationBase[(node, slot)] = row.Server.Population.Generation(index: slot);
                }
            }
            m_slots[Model.Node.H][0] = 0;
            for (var index = 0; (index < m_scope.Companions); index++) {
                m_slots[Model.Node.H][index + 1] = CompanionRows.SlotOf(row: m_rows.Home, incarnation: m_rows.Companions[index]);
            }
            // The player crosses along the route; each crossing runs to quiescence before the next. A crossing that a
            // fault ended before anything was minted started nothing, and the player tries again once.
            var retried = false;

            while (true) {
                Boundary();
                if (m_divergences.Count > 0) {
                    return;
                }
                var cross = Model.CustodyModel.ProgressActions(s: m_model).FirstOrDefault(predicate: static move => move.Name.StartsWith("cross", StringComparison.Ordinal));

                if (cross.Next is null) {
                    break;
                }
                var at = m_model.PlayerAt!.Value;
                var to = m_model.Route[m_model.Itinerary];
                var itinerary = m_model.Itinerary;

                if (CompanionRows.SlotOf(row: RowOf(node: at), incarnation: m_rows.Player) < 0) {
                    Diverge(text: $"the model's player stands at {at} but the engine's does not");
                    return;
                }
                var faulted = m_faultSpent;

                m_pendingTransfer[(int)at] = m_rows.Enqueue(from: RowOf(node: at), to: RowOf(node: to));
                Settle();
                if ((m_divergences.Count == 0) && (m_model.Itinerary == itinerary) && !faulted && m_faultSpent && !retried) {
                    retried = true;
                    Trace(text: $"the player tries {at}>{to} again");
                    continue;
                }
                if ((m_divergences.Count == 0) && (m_model.Itinerary == itinerary)) {
                    Diverge(text: $"the engine never minted the crossing {at}>{to}");
                    return;
                }
            }
            // Recovery results: every authority restarts from its image plus its log and derives the same state.
            foreach (var node in Enum.GetValues<Model.Node>()) {
                RestartEngine(node: node);
                CrashModel(node: node);
                Compare(where: $"after the recovery of {node}");
            }
        }
        // Steps the host until a step makes no observable; a crashed authority restarts between steps.
        private void Settle() {
            for (var step = 0; (step < 40); step++) {
                m_stepHadObservable = false;
                try {
                    m_rows.Step(ticks: 1);
                } catch (AuthorityCrashedException) {
                    // The authority whose append threw is down; its restart is the next thing that happens.
                }
                foreach (var node in m_down.ToArray()) {
                    RestartEngine(node: node);
                    _ = m_down.Remove(item: node);
                }
                if (m_divergences.Count > 0) {
                    return;
                }
                if (m_clock.Elapsed > TimeSpan.FromSeconds(value: 45)) {
                    Diverge(text: "the run exceeded its deadline");
                    return;
                }
                Boundary();
                if (!m_stepHadObservable && (step > 2)) {
                    // Nothing moves: a latched authority recovers (checkpoint plus log), as the model's fair completion
                    // recovers it, and the run goes on.
                    if (RecoverLatched()) {
                        continue;
                    }
                    return;
                }
            }
        }
        private bool RecoverLatched() {
            if (m_divergences.Count > 0) {
                return false;
            }
            foreach (var node in Enum.GetValues<Model.Node>()) {
                if (RowOf(node: node).Server.UncertainCrossing is not null) {
                    Trace(text: $"recover latched {node}");
                    RestartEngine(node: node);
                    CrashModel(node: node);
                    return true;
                }
            }
            return false;
        }
        // A drain boundary: the model catches up on the engine's drain-time moves, the derived states are compared, and a
        // scheduled author's or owner's act happens here.
        private void Boundary() {
            var index = m_points.Count;

            m_points.Add(item: new Point(Kind: PointKind.Boundary, Node: Model.Node.H, Text: "boundary"));
            m_lastReturned = null;
            if ((m_schedule is { Checkpoint: true } ahead) && (m_recorded is not null) && (ahead.Point < m_recorded.Count) && (index < ahead.Point) && !m_faultSpent) {
                // The image a crash later recovers from is this boundary's, so the recovery redoes only the log past it.
                m_rows.Checkpoint(name: RowNames[m_recorded[ahead.Point].Node]);
            }
            if ((m_schedule is { } schedule) && (schedule.Point == index) && !m_faultSpent) {
                m_faultSpent = true;
                ApplyBoundaryFault(fault: schedule.Fault);
            }
            // An authority latched on an uncertain record recovers before the next drain (checkpoint plus log), as the
            // model's fair completion recovers it; its peers' retries then meet the recovered activation.
            while (RecoverLatched()) {
            }
            Compare(where: $"boundary #{index}");
        }
        private void ApplyBoundaryFault(Fault fault) {
            switch (fault) {
                case Fault.Install: {
                    var move = Model.CustodyModel.FaultActions(s: m_model).FirstOrDefault(predicate: static move => move.Name.StartsWith("install", StringComparison.Ordinal));

                    if (move.Next is null) {
                        return;
                    }
                    var id = int.Parse(s: move.Name["install ".Length..]);

                    Trace(text: $"act {move.Name}");
                    m_model = move.Next;
                    _ = m_rows.Home.Server.RecordCustody(reason: out _, record: new WorldCrossingRecord.Release(Incarnation: IncarnationOf(id: id)));
                    break;
                }
                case Fault.Stranger: {
                    var move = Model.CustodyModel.FaultActions(s: m_model).FirstOrDefault(predicate: static move => move.Name.StartsWith("stranger A", StringComparison.Ordinal));

                    // The stranger matters only ahead of the player's crossing into A, where it fills A's cap; the
                    // engine seats it in a local seat, which the slot map does not follow once the player has left A.
                    if ((move.Next is null) || (m_model.Itinerary >= m_model.Route.Length) || (m_model.Route[m_model.Itinerary] != Model.Node.A)) {
                        return;
                    }
                    Trace(text: $"act {move.Name}");
                    m_model = move.Next;
                    for (var slot = 0; (slot < m_rows.B.Server.Population.LocalSeatCount); slot++) {
                        _ = m_rows.B.Server.ApplySession(request: new SessionRequest.Join(IdentityName: null, Principal: Principal.Seat(slot: slot), Slot: slot, WireProtocolKey: WorldProtocol.WireProtocolKey));
                    }
                    break;
                }
                case Fault.RefuseAtCommit:
                    // Armed here; the model's move is taken when the lease exists, at the commit.
                    m_refuseAtCommit["row-b"] = Model.Ids.C1;
                    break;
                case Fault.Forge:
                    Forge();
                    break;
            }
        }
        // A former custodian presents a ticket it once held toward home or the other custodian: the model's first forge
        // move, performed by direct calls on the target's doors.
        private void Forge() {
            var move = Model.CustodyModel.FaultActions(s: m_model).FirstOrDefault(predicate: static move => move.Name.StartsWith("forge", StringComparison.Ordinal));

            if (move.Next is null) {
                return;
            }
            // "forge A>H:2 under A1@1"
            var text = move.Name["forge ".Length..];
            var forger = Enum.Parse<Model.Node>(value: text[..1]);
            var target = Enum.Parse<Model.Node>(value: text[2..3]);
            var id = int.Parse(s: text[4..text.IndexOf(' ')]);
            var key = text[(text.IndexOf("under ", StringComparison.Ordinal) + "under ".Length)..text.LastIndexOf('@')];
            var epoch = ulong.Parse(s: text[(text.LastIndexOf('@') + 1)..]);

            Trace(text: $"act {move.Name}");
            m_model = move.Next;
            if (!m_keysOf.TryGetValue(key: key, value: out var engineKey)) {
                engineKey = new WorldTransferKey(SourceAuthority: RowOf(node: forger).Server.AuthorityIdentity, TransferId: (7000UL + (ulong)m_keysOf.Count));
                Bind(engineKey: engineKey, modelKey: key);
            }
            var incarnation = IncarnationOf(id: id);
            var destination = RowOf(node: target).Server;
            var tick = (destination.NextInputTick - 1UL);
            var request = new WorldTransferReservationRequest(
                Border: "forged",
                BorderCapacity: null,
                DeadlineSourceTick: (tick + 60UL),
                Members: [new WorldTransferReservationMember(
                    BodyColor: default,
                    CatalogRig: 0,
                    Companion: new WorldTransferCompanion(Driver: null, FollowsMember: null, Home: incarnation.Authority, Role: "companion", Ticket: new WorldCustodyTicket(Epoch: (epoch + 1UL), Home: incarnation.Authority, Hop: engineKey, Incarnation: incarnation)),
                    Identity: null,
                    MemberId: 0,
                    Mobility: new WorldMobilityIdentity(DepartedFrom: incarnation, Epoch: epoch, Incarnation: incarnation),
                    PreferredSlot: WorldBodiesLimits.LocalSeatCount,
                    Principal: Principal.Console,
                    Source: IntentSource.Idle
                )],
                PartyAllOrNothing: true,
                // As the engine sends a companion's own crossing: a companion lands through the entity door.
                PeerAdmission: false,
                SourceAuthority: engineKey.SourceAuthority,
                SourceRateHz: destination.Definition.SimulationRateHz,
                SourceTick: tick,
                TransferId: engineKey.TransferId
            );

            m_memberIds[engineKey] = new Dictionary<int, int> { [0] = id };
            Expect(from: forger, kind: Model.MsgKind.Reserve, key: key, to: target);
            var reply = destination.ReserveTransfer(request: request);

            Trace(text: $"forged reservation {(reply.Accepted ? "accepted" : $"refused: {reply.Reason}")}");
            ExpectReply(from: target, key: key, kinds: [Model.MsgKind.Reserved, Model.MsgKind.ReserveRefused], to: forger, replied: (reply.Accepted ? Model.MsgKind.Reserved : Model.MsgKind.ReserveRefused));
            if (!reply.Accepted) {
                return;
            }
            Expect(from: forger, kind: Model.MsgKind.Commit, key: key, to: target);
            var status = destination.CommitTransfer(members: [new WorldTransferCommitMember(Arrival: WorldTransferArrival.Spawn, BodyMotionProgramName: string.Empty, PlanarVelocity: default, Position: default, Profile: null, VerticalVelocity: default, YawRadians: default) { MemberId = 0, Companion = request.Members[0].Companion }], reason: out _, sourceAuthority: engineKey.SourceAuthority, transferId: engineKey.TransferId);

            ExpectReply(from: target, key: key, kinds: [Model.MsgKind.Committed, Model.MsgKind.Missing, Model.MsgKind.Uncertain], to: forger, replied: status switch { WorldTransferStatus.Committed => Model.MsgKind.Committed, WorldTransferStatus.Uncertain => Model.MsgKind.Uncertain, _ => Model.MsgKind.Missing });
        }

        // ---- the seams -------------------------------------------------------------------------------------------

        private void InstallSeams(HostRow row) {
            var node = NodeOfRow(name: row.Instance.Name);
            var inner = row.Server.CustodyPeers;

            m_rows.Host.SetPeerCallFault(fault: new PeerCalls(Owner: this, To: node), instanceName: row.Instance.Name);
            row.Server.CustodyPeers = authority => ((inner?.Invoke(arg: authority) is { } door) ? new CustodyCalls(From: node, Inner: door, Owner: this, To: NodeOfAuthority(authority: authority)) : null);
        }
        private sealed record PeerCalls(Refinement Owner, Model.Node To) : IWorldPeerCall {
            private WorldServer Destination => Owner.RowOf(node: To).Server;

            public WorldTransferReservationReply Reserve(WorldTransferReservationRequest request) {
                var from = Owner.NodeOfAuthority(authority: request.SourceAuthority);
                var key = Owner.KeyName(key: new WorldTransferKey(SourceAuthority: request.SourceAuthority, TransferId: request.TransferId));

                Owner.LearnMembers(key: new WorldTransferKey(SourceAuthority: request.SourceAuthority, TransferId: request.TransferId), members: request.Members);
                return Owner.Call(from: from, to: To, kind: Model.MsgKind.Reserve, key: key, call: () => Destination.ReserveTransfer(request: request), unreachable: () => WorldTransferReservationReply.Refused(reason: "the lane to the destination is unavailable") with { Transient = true }, replied: static reply => (reply.Accepted ? Model.MsgKind.Reserved : Model.MsgKind.ReserveRefused), replyKinds: [Model.MsgKind.Reserved, Model.MsgKind.ReserveRefused], after: reply => Owner.LearnSlots(from: from, key: key, reply: reply, request: request));
            }
            public WorldTransferStep Commit(string sourceAuthority, ulong transferId, IReadOnlyList<WorldTransferCommitMember> members, out WorldTransferStatus status, out string reason) {
                var from = Owner.NodeOfAuthority(authority: sourceAuthority);
                var key = Owner.KeyName(key: new WorldTransferKey(SourceAuthority: sourceAuthority, TransferId: transferId));
                var detail = string.Empty;

                Owner.RefuseAtCommit(key: key, to: To);
                var verdict = Owner.Call(from: from, to: To, kind: Model.MsgKind.Commit, key: key, call: () => (WorldTransferStatus?)Destination.CommitTransfer(members: members, reason: out detail, sourceAuthority: sourceAuthority, transferId: transferId), unreachable: static () => null, replied: static verdict => verdict switch { WorldTransferStatus.Committed => Model.MsgKind.Committed, WorldTransferStatus.Uncertain => Model.MsgKind.Uncertain, _ => Model.MsgKind.Missing }, replyKinds: [Model.MsgKind.Committed, Model.MsgKind.Missing, Model.MsgKind.Uncertain]);

                reason = detail;
                status = (verdict ?? WorldTransferStatus.Missing);
                return ((verdict is null) ? WorldTransferStep.Unreachable : WorldTransferStep.Answered);
            }
            public bool TryStatus(string sourceAuthority, ulong transferId, out WorldTransferStatus status) {
                var from = Owner.NodeOfAuthority(authority: sourceAuthority);
                var key = Owner.KeyName(key: new WorldTransferKey(SourceAuthority: sourceAuthority, TransferId: transferId));
                var answer = Owner.Call(from: from, to: To, kind: Model.MsgKind.Status, key: key, call: () => (WorldTransferStatus?)Destination.TransferStatus(sourceAuthority: sourceAuthority, transferId: transferId), unreachable: static () => null, replied: static _ => Model.MsgKind.StatusReply, replyKinds: [Model.MsgKind.StatusReply]);

                status = (answer ?? WorldTransferStatus.Missing);
                return (answer is not null);
            }
            public bool TryLanded(string sourceAuthority, ulong transferId, out IReadOnlyList<int>? landed) {
                if (Owner.m_down.Contains(item: To)) {
                    landed = null;
                    return false;
                }
                landed = Destination.TransferLanded(sourceAuthority: sourceAuthority, transferId: transferId);
                return true;
            }
            public void Abort(string sourceAuthority, ulong transferId) {
                Owner.Expire(key: Owner.KeyName(key: new WorldTransferKey(SourceAuthority: sourceAuthority, TransferId: transferId)), node: To);
                if (!Owner.m_down.Contains(item: To)) {
                    Destination.AbortTransfer(sourceAuthority: sourceAuthority, transferId: transferId);
                }
            }
            public void Acknowledge(string sourceAuthority, ulong transferId) {
                if (!Owner.m_down.Contains(item: To)) {
                    Destination.AcknowledgeTransfer(sourceAuthority: sourceAuthority, transferId: transferId);
                }
            }
        }
        private sealed record CustodyCalls(Refinement Owner, Model.Node From, Model.Node To, IWorldCustodyPeer Inner) : IWorldCustodyPeer {
            public IReadOnlyList<ulong?>? ConfirmHops(WorldTransferKey key, IReadOnlyList<WorldEntityAddress> incarnations, string destination, WorldHopPhase phase) => Owner.Call(from: From, to: To, kind: Model.MsgKind.HopQuery, key: Owner.KeyName(key: key), call: () => Inner.ConfirmHops(destination: destination, incarnations: incarnations, key: key, phase: phase), unreachable: static () => null, replied: static _ => Model.MsgKind.HopQueryReply, replyKinds: [Model.MsgKind.HopQueryReply]);
            public IReadOnlyList<WorldHopAnswer> RequestHop(WorldTransferKey key, string source, string destination, IReadOnlyList<WorldHopRequestMember> members) => Owner.Call(from: From, to: To, kind: Model.MsgKind.HopRequest, key: Owner.KeyName(key: key), call: () => Inner.RequestHop(destination: destination, key: key, members: members, source: source), unreachable: () => [.. members.Select(selector: static member => new WorldHopAnswer(Incarnation: member.Incarnation, Next: 0UL, Verdict: WorldHopVerdict.Unavailable))], replied: static _ => Model.MsgKind.HopAnswer, replyKinds: [Model.MsgKind.HopAnswer]);
            public WorldCustodyAnswer QueryCustody(WorldTransferKey key, WorldEntityAddress incarnation, string asker) => Owner.Call(from: From, to: To, kind: Model.MsgKind.CustodyQuery, key: Owner.KeyName(key: key), call: () => Inner.QueryCustody(asker: asker, incarnation: incarnation, key: key), unreachable: static () => WorldCustodyAnswer.Uncertain, replied: static _ => Model.MsgKind.CustodyReply, replyKinds: [Model.MsgKind.CustodyReply]);
            public bool SettleHop(WorldTransferKey key, WorldEntityAddress incarnation, string source) => Owner.Call(from: From, to: To, kind: Model.MsgKind.HopSettled, key: Owner.KeyName(key: key), call: () => Inner.SettleHop(incarnation: incarnation, key: key, source: source), unreachable: static () => false, replied: static acked => (acked ? Model.MsgKind.HopSettledAck : null), replyKinds: [Model.MsgKind.HopSettledAck]);
        }
        private sealed record ObservedLog(Refinement Owner, Model.Node Node, IWorldCrossingLog Inner) : IWorldCrossingLog {
            public WorldCrossingDurability Append(in WorldCrossingEntry entry, out string reason) => Owner.Append(entry: in entry, inner: Inner, node: Node, reason: out reason);
        }

        // One engine call between two authorities: the model delivers the request (or drops it), the engine performs it
        // (or finds the peer unreachable), and the model delivers the reply the engine's answer names.
        private TReply Call<TReply>(Model.Node from, Model.Node to, Model.MsgKind kind, string key, Func<TReply> call, Func<TReply> unreachable, Func<TReply, Model.MsgKind?> replied, Model.MsgKind[] replyKinds, Action<TReply>? after = null) {
            var index = m_points.Count;
            var fault = FaultAt(index: index);

            m_observables++;
            m_stepHadObservable = true;
            m_points.Add(item: new Point(Kind: PointKind.Call, Node: to, Text: $"{from}>{to}:{kind}:{key}"));
            // A handler the schedule crashes before its append never ran as far as the model is concerned: its nested
            // calls are forwarded without a model move.
            if (m_crashBeforeArmed.GetValueOrDefault(key: from) || (m_silent > 0)) {
                return call();
            }
            if (m_down.Contains(item: to) || (fault == Fault.DropRequest)) {
                Drop(from: from, kind: kind, key: key, to: to);
                return unreachable();
            }
            var producerFault = FaultProducedBy(call: index, producer: Producer.Handler);

            if (producerFault is Fault.LogRefused or Fault.LogUncertainLost or Fault.LogUncertainLanded) {
                ArmAppend(fault: producerFault.Value, node: to);
            }
            if (fault == Fault.Dup) {
                Duplicate(from: from, kind: kind, key: key, to: to);
            }
            if (producerFault == Fault.CrashBefore) {
                Drop(from: from, kind: kind, key: key, to: to);
                m_crashBeforeArmed[to] = true;
            } else {
                switch (Expect(from: from, kind: kind, key: key, to: to)) {
                    case Expected.Missing:
                        return unreachable();
                    case Expected.Unmodelled: {
                        // The engine re-sends a request the protocol gave up: the peer answers, the model does not watch.
                        m_silent++;
                        try {
                            return call();
                        } finally {
                            m_silent--;
                        }
                    }
                }
            }
            m_activeCalls.Add(item: (index, from, to));
            TReply reply;

            try {
                reply = call();
                // The duplicate reaches the receiver after the first copy's handler ran; the sender hears the second
                // answer, and the first is lost.
                if (fault == Fault.Dup) {
                    _ = Expect(from: from, kind: kind, key: key, to: to);
                    reply = call();
                }
            } catch (AuthorityCrashedException) {
                _ = m_activeCalls.RemoveAll(match: active => (active.Call == index));
                m_crashBeforeArmed[to] = false;
                return unreachable();
            }
            _ = m_activeCalls.RemoveAll(match: active => (active.Call == index));
            m_crashBeforeArmed[to] = false;
            if (m_down.Contains(item: to)) {
                return unreachable();
            }
            m_lastReturned = (index, from, kind);
            after?.Invoke(obj: reply);
            if (fault == Fault.Dup) {
                DropReply(from: to, key: key, kinds: replyKinds, to: from);
            }
            var replyFault = FaultProducedBy(call: index, producer: Producer.Reply);

            if (replyFault is Fault.LogRefused or Fault.LogUncertainLost or Fault.LogUncertainLanded) {
                ArmAppend(fault: replyFault.Value, node: from);
            }
            if ((fault == Fault.DropReply) || (replyFault == Fault.CrashBefore)) {
                DropReply(from: to, key: key, kinds: replyKinds, to: from);
                return ((fault == Fault.DropReply) ? unreachable() : reply);
            }
            // A reply that answers nothing yet (home has not settled the hop it was told of) is no message at all.
            if (replied(arg: reply) is { } answered) {
                ExpectReply(from: to, key: key, kinds: replyKinds, to: from, replied: answered);
            }
            return reply;
        }
        private WorldCrossingDurability Append(in WorldCrossingEntry entry, IWorldCrossingLog inner, Model.Node node, out string reason) {
            var index = m_points.Count;
            var fault = FaultAt(index: index);
            var producer = ProducerOf(node: node, record: entry.Record);
            var text = $"{node}:{Shape(record: entry.Record, node: node)}";

            m_observables++;
            m_stepHadObservable = true;
            m_points.Add(item: new Point(Kind: PointKind.Append, Node: node, Text: text, Producer: producer.Producer, ProducerCall: producer.Call));
            if (fault is Fault.LogRefused or Fault.LogUncertainLost or Fault.LogUncertainLanded) {
                if (producer.Producer == Producer.Drain) {
                    // The authority's own move runs into the armed fault; a handler's or a reply's ran armed already.
                    ArmAppend(fault: fault, node: node);
                    if (Search(crossing: (entry.Record is WorldCrossingRecord.Allocation), depth: 3, goal: state => ((state.At(node: node).NextAppend == Model.Append.None) ? state : null)) is null) {
                        Diverge(text: $"{node} appended {text} but no model move records anything there");
                    }
                }
                switch (fault) {
                    case Fault.LogRefused:
                        reason = "the store refused the record";
                        Trace(text: $"append {text} refused");
                        return WorldCrossingDurability.Refused;
                    case Fault.LogUncertainLost:
                        reason = "the store's answer was lost";
                        Trace(text: $"append {text} uncertain (lost)");
                        return WorldCrossingDurability.Uncertain;
                    default:
                        _ = inner.Append(entry: in entry, reason: out _);
                        Match(node: node, record: entry.Record);
                        reason = "the store's answer was lost";
                        Trace(text: $"append {text} uncertain (landed)");
                        return WorldCrossingDurability.Uncertain;
                }
            }
            if (fault == Fault.CrashBefore) {
                // A handler's request or a reply was dropped as the fault was armed; a drain move never happens.
                CrashModel(node: node);
                Crash(node: node, text: $"{entry.Record.GetType().Name} before it was durable");
            }
            var durability = inner.Append(entry: in entry, reason: out reason);

            if (durability == WorldCrossingDurability.Durable) {
                Match(node: node, record: entry.Record);
                if (fault == Fault.CrashAfter) {
                    CrashModel(node: node);
                    Crash(node: node, text: $"{entry.Record.GetType().Name} after it was durable");
                }
            }
            return durability;
        }
        private void Crash(Model.Node node, string text) {
            Trace(text: $"crash {node} at {text}");
            _ = m_down.Add(item: node);
            throw new AuthorityCrashedException(step: text);
        }
        private void RestartEngine(Model.Node node) {
            var name = RowNames[node];

            Trace(text: $"restart {node}");
            m_crashBeforeArmed[node] = false;
            try {
                m_rows.Restart(name: name);
            } catch (Exception exception) when (exception is not Xunit.Sdk.XunitException) {
                Diverge(text: $"the recovery of {node} threw {exception.GetType().Name}: {exception.Message}");
            }
        }
        // The model's crash: volatile state cleared, and every message to or from the authority lost, since the engine's
        // synchronous calls never outlive the process that made them.
        private void CrashModel(Model.Node node) {
            var next = m_model.Clone();

            next.At(node: node).Crash();
            next.Budget.Crashes++;
            next.Budget.Faults++;
            for (var index = next.Network.Count - 1; (index >= 0); index--) {
                if ((next.Network[index].From == node) || (next.Network[index].To == node)) {
                    next.Network.RemoveAt(index: index);
                }
            }
            m_model = next;
            Trace(text: $"model crash {node}");
        }

        // ---- the model following -------------------------------------------------------------------------------

        private Fault FaultAt(int index) {
            if ((m_schedule is not { } schedule) || (schedule.Point != index) || m_faultSpent) {
                return Fault.None;
            }
            m_faultSpent = true;
            return schedule.Fault;
        }
        // The fault the schedule aims at an append that `call`'s handler or reply produces, read from the recording.
        private Fault? FaultProducedBy(int call, Producer producer) {
            if ((m_schedule is not { } schedule) || (m_recorded is null) || (schedule.Point >= m_recorded.Count) || m_faultSpent) {
                return null;
            }
            var point = m_recorded[schedule.Point];

            if ((point.Kind != PointKind.Append) || (point.Producer != producer) || (point.ProducerCall != call)) {
                return null;
            }
            return schedule.Fault;
        }
        // The records a reply's handler appends: the departure on a reservation, the settlement on a commit's or status
        // answer, the acknowledgement on home's, and home's settlement of a hop on the destination's custody answer.
        private (Producer Producer, int Call) ProducerOf(Model.Node node, WorldCrossingRecord record) {
            for (var index = m_activeCalls.Count - 1; (index >= 0); index--) {
                if (m_activeCalls[index].To == node) {
                    return (Producer.Handler, m_activeCalls[index].Call);
                }
            }
            if ((m_lastReturned is { } returned) && (returned.From == node)) {
                var reply = record switch {
                    WorldCrossingRecord.Departure => (returned.Kind == Model.MsgKind.Reserve),
                    WorldCrossingRecord.Settlement => (returned.Kind is Model.MsgKind.Commit or Model.MsgKind.Status),
                    WorldCrossingRecord.HopAcked => (returned.Kind == Model.MsgKind.HopSettled),
                    WorldCrossingRecord.HopDone => (returned.Kind == Model.MsgKind.CustodyQuery),
                    _ => false,
                };

                if (reply) {
                    return (Producer.Reply, returned.Call);
                }
            }
            return (Producer.Drain, -1);
        }
        private void ArmAppend(Fault fault, Model.Node node) {
            var next = m_model.Clone();

            next.At(node: node).NextAppend = fault switch {
                Fault.LogRefused => Model.Append.Refused,
                Fault.LogUncertainLost => Model.Append.UncertainLost,
                _ => Model.Append.UncertainLanded,
            };
            next.Budget.Logs++;
            next.Budget.Faults++;
            m_model = next;
            Trace(text: $"model arm {fault} at {node}");
        }
        private static Model.Msg? Find(Model.ModelState state, Model.Node from, Model.Node to, string key, Model.MsgKind[] kinds) => Model.CustodyModel.LaneHeads(s: state).FirstOrDefault(predicate: msg => (msg.From == from) && (msg.To == to) && kinds.Contains(value: msg.Kind) && (msg.Key == key));
        // The model delivers the message the engine call is; free moves (progress, timeouts) may precede it.
        private Expected Expect(Model.Node from, Model.Node to, Model.MsgKind kind, string key) {
            var path = Search(depth: 3, goal: state => ((Find(state: state, from: from, to: to, key: key, kinds: [kind]) is { } msg) ? Delivered(state: state, msg: msg) : null));

            if (path is not null) {
                return Expected.Delivered;
            }
            // The model bounds each obligation's retries to keep its state space finite; the engine retries an
            // unanswered obligation every drain. A resend past that bound is the model's own retry move with the count
            // forgotten, never a new transition.
            var unbounded = m_model.Clone();
            var counted = unbounded.At(node: from).Retries.Keys.Where(predicate: retried => retried.Contains(value: key, comparisonType: StringComparison.Ordinal)).ToArray();

            if (counted.Length > 0) {
                var bounded = m_model;

                foreach (var retried in counted) {
                    _ = unbounded.At(node: from).Retries.Remove(key: retried);
                }
                m_model = unbounded;
                if (Search(depth: 3, goal: state => ((Find(state: state, from: from, to: to, key: key, kinds: [kind]) is { } msg) ? Delivered(state: state, msg: msg) : null)) is not null) {
                    Defer(text: "retry (past the model's retry bound)");
                    return Expected.Delivered;
                }
                m_model = bounded;
            }
            if ((kind == Model.MsgKind.Reserve) && !m_model.At(node: from).Outbound.ContainsKey(key: key)) {
                Defer(text: "retry (engine re-sends a refused reservation)");
                return Expected.Unmodelled;
            }
            Diverge(text: $"the engine sent {from}>{to}:{kind}:{key} but the model enables no such delivery (lane heads: {string.Join(", ", Model.CustodyModel.LaneHeads(s: m_model).Select(static m => $"{m.From}>{m.To}:{m.Kind}:{m.Key}"))})");
            return Expected.Missing;
        }
        private void ExpectReply(Model.Node from, Model.Node to, string key, Model.MsgKind[] kinds, Model.MsgKind replied) {
            Model.Msg? found = null;
            var path = Search(depth: 2, goal: state => ((Find(state: state, from: from, to: to, key: key, kinds: kinds) is { } msg) ? Delivered(state: state, msg: (found = msg)) : null));

            if ((path is null) || (found is null)) {
                Diverge(text: $"the engine answered {from}>{to}:{replied}:{key} but the model has no reply in flight");
                return;
            }
            if (found.Kind != replied) {
                Diverge(text: $"the engine answered {from}>{to}:{replied}:{key} where the model answered {found.Kind} ({found.Arg})");
            }
            Trace(text: $"deliver {found.Canon}");
        }
        private Model.ModelState Delivered(Model.ModelState state, Model.Msg msg) {
            var next = state.Clone();

            _ = next.Network.Remove(item: msg);
            Model.CustodyModel.Deliver(s: next, msg: msg);
            return next;
        }
        private void Drop(Model.Node from, Model.Node to, Model.MsgKind kind, string key) {
            var path = Search(depth: 3, goal: state => ((Find(state: state, from: from, to: to, key: key, kinds: [kind]) is { } msg) ? Dropped(state: state, msg: msg) : null));

            if (path is null) {
                Diverge(text: $"the engine lost {from}>{to}:{kind}:{key} but the model has no such message in flight");
            }
        }
        private void DropReply(Model.Node from, Model.Node to, string key, Model.MsgKind[] kinds) {
            if (Find(state: m_model, from: from, to: to, key: key, kinds: kinds) is { } msg) {
                m_model = Dropped(state: m_model, msg: msg);
            }
        }
        private Model.ModelState Dropped(Model.ModelState state, Model.Msg msg) {
            var next = state.Clone();

            _ = next.Network.Remove(item: msg);
            next.Budget.Drops++;
            next.Budget.Faults++;
            Trace(text: $"drop {msg.Canon}");
            return next;
        }
        private void Duplicate(Model.Node from, Model.Node to, Model.MsgKind kind, string key) {
            if (Find(state: m_model, from: from, to: to, key: key, kinds: [kind]) is { } msg) {
                var next = m_model.Clone();
                var index = next.Network.IndexOf(item: msg);

                // The copy is delayed on a lane of its own (spec section 4: a duplicate may arrive late, as a request a
                // reconnect left behind does), so it reaches the receiver after the first copy's handler and its nested
                // exchange, which is when the engine's second call delivers it.
                next.Network[index] = msg with { Dup = true };
                next.Network.Add(item: msg with { Dup = true, Lane = (msg.Lane + 1000) });
                next.Budget.Dups++;
                next.Budget.Faults++;
                m_model = next;
                Trace(text: $"dup {msg.Canon}");
            }
        }
        private void Expire(string key, Model.Node node) {
            if (m_model.At(node: node).Leases.ContainsKey(key: key)) {
                var next = m_model.Clone();

                _ = next.At(node: node).Leases.Remove(key: key);
                next.Budget.Deadlines++;
                next.Budget.Faults++;
                m_model = next;
                Trace(text: $"expire {node}:{key}");
            }
        }
        private void RefuseAtCommit(string key, Model.Node to) {
            if (!m_refuseAtCommit.TryGetValue(key: RowNames[to], value: out var id)) {
                return;
            }
            var move = Model.CustodyModel.FaultActions(s: m_model).FirstOrDefault(predicate: move => move.Name == $"refuse at commit {to}:{key}:{id}");

            if (move.Next is null) {
                return;
            }
            _ = m_refuseAtCommit.Remove(key: RowNames[to]);
            Trace(text: $"act {move.Name}");
            m_model = move.Next;
            if (m_keysOf.TryGetValue(key: key, value: out var engineKey) && m_memberIds.TryGetValue(key: engineKey, value: out var members) && members.FirstOrDefault(predicate: pair => (pair.Value == id)) is { } pair) {
                var ordinal = pair.Key;

                m_rows.LandingRefusals[RowNames[to]] = index => ((index == ordinal) ? "refused at the landing" : null);
            }
        }
        // An engine append is the model's next unconsumed record at that authority, or the record a free move appends.
        private void Match(Model.Node node, WorldCrossingRecord record) {
            var records = m_model.At(node: node).Records;

            while ((records.Count > m_consumed[(int)node]) && (Shape(rec: records[m_consumed[(int)node]]) is null)) {
                m_consumed[(int)node]++;
            }
            if (records.Count > m_consumed[(int)node]) {
                var expected = records[m_consumed[(int)node]++];

                if (!Matches(rec: expected, record: record, node: node)) {
                    Diverge(text: $"{node} appended {Shape(record: record, node: node)} where the model's next record is {Shape(rec: expected)}");
                }
                Trace(text: $"append {node}:{Shape(record: record, node: node)}");
                return;
            }
            var before = m_consumed[(int)node];
            var path = Search(crossing: (record is WorldCrossingRecord.Allocation), depth: 3, goal: state => {
                var mine = state.At(node: node).Records;

                return ((mine.Count > before) && Matches(rec: mine[before], record: record, node: node)) ? state : null;
            });

            if (path is null) {
                Diverge(text: $"{node} appended {Shape(record: record, node: node)} but no model move produces it (enabled: {string.Join(", ", Model.CustodyModel.ProgressActions(s: m_model).Select(static m => m.Name))})");
                return;
            }
            m_consumed[(int)node] = (before + 1);
            Trace(text: $"append {node}:{Shape(record: record, node: node)}");
        }
        // Whether a model record is the engine record: an allocation is the mint of the authority's pending transfer, which
        // binds the engine key to the model's name for it.
        private bool Matches(Model.Rec rec, WorldCrossingRecord record, Model.Node node) {
            if (record is WorldCrossingRecord.Allocation) {
                if ((rec is not Model.Minted minted) || (m_pendingTransfer[(int)node] is not { } pending)) {
                    return false;
                }
                Bind(engineKey: new WorldTransferKey(SourceAuthority: RowOf(node: node).Server.AuthorityIdentity, TransferId: pending), modelKey: minted.Key);
                return true;
            }
            return (Shape(rec: rec) == Shape(record: record, node: node));
        }
        // Iterative deepening over the model's free moves (progress moves and the timeouts an unreachable answer is) to a
        // state the goal accepts, which becomes the model's state.
        private Model.ModelState? Search(int depth, Func<Model.ModelState, Model.ModelState?> goal, bool crossing = false) {
            m_expanded = 0;
            for (var limit = 0; (limit <= depth); limit++) {
                var found = Search(crossing: crossing, state: m_model, goal: goal, limit: limit, taken: []);

                if (found is { } result) {
                    foreach (var name in result.Taken) {
                        Trace(text: $"move {name}");
                    }
                    m_model = result.State;
                    return result.State;
                }
            }
            return null;
        }
        private (Model.ModelState State, List<string> Taken)? Search(Model.ModelState state, Func<Model.ModelState, Model.ModelState?> goal, int limit, List<string> taken, bool crossing) {
            if (limit == 0) {
                return ((goal(arg: state) is { } reached) ? (reached, taken) : null);
            }
            if (++m_expanded > 4000) {
                return null;
            }
            foreach (var (name, next) in FreeMoves(crossing: crossing, state: state)) {
                var found = Search(crossing: crossing, state: next, goal: goal, limit: (limit - 1), taken: [.. taken, name]);

                if (found is not null) {
                    return found;
                }
            }
            return null;
        }
        private static IEnumerable<(string Name, Model.ModelState Next)> FreeMoves(Model.ModelState state, bool crossing = false) {
            foreach (var move in Model.CustodyModel.ProgressActions(s: state)) {
                if (crossing || !move.Name.StartsWith("cross", StringComparison.Ordinal)) {
                    yield return move;
                }
            }
            foreach (var move in Model.CustodyModel.FaultActions(s: state)) {
                if (move.Name.StartsWith("hop timeout", StringComparison.Ordinal) || move.Name.StartsWith("verify timeout", StringComparison.Ordinal)) {
                    yield return move;
                }
            }
        }

        // ---- comparison ------------------------------------------------------------------------------------------

        private void Compare(string where) {
            if (m_divergences.Count > 0) {
                return;
            }
            // The model catches up on drain-time moves the engine made silently (nothing durable, nothing sent) before
            // the states are compared; whatever stays enabled is an unbuilt step or a divergence.
            var engine = ProjectEngine();

            if (engine != ProjectModel(state: m_model)) {
                _ = Search(depth: 2, goal: state => ((ProjectModel(state: state) == engine) ? state : null));
            }
            var model = ProjectModel(state: m_model);

            m_comparisons++;
            if (engine != model) {
                Diverge(text: $"state differs {where}:{Environment.NewLine}  engine {engine}{Environment.NewLine}  model  {model}");
                return;
            }
            try {
                Model.CustodyModel.CheckInvariants(s: m_model);
            } catch (Model.ModelViolation violation) {
                Diverge(text: $"the model's own invariant fails {where}: {violation.Message}");
            }
            foreach (var (name, _) in FreeMoves(state: m_model)) {
                var prefix = Unbuilt.FirstOrDefault(predicate: unbuilt => name.StartsWith(unbuilt, StringComparison.Ordinal));

                if (prefix is not null) {
                    Defer(text: prefix);
                }
            }
        }
        private string ProjectEngine() {
            var sb = new StringBuilder();

            for (var node = Model.Node.H; (node <= Model.Node.B); node++) {
                var row = RowOf(node: node);

                sb.Append(node).Append('{');
                sb.Append("p").Append((CompanionRows.SlotOf(row: row, incarnation: m_rows.Player) >= 0) ? 1 : 0).Append(';');
                for (var index = 0; (index < m_scope.Companions); index++) {
                    var companion = m_rows.Companions[index];
                    var slot = CompanionRows.SlotOf(row: row, incarnation: companion);

                    sb.Append(index + 2).Append(':');
                    if (slot >= 0) {
                        sb.Append('@').Append(CompanionRows.EpochAt(row: row, incarnation: companion)).Append(FollowsLive(node: node, row: row, companion: companion) ? "f" : "-");
                    } else {
                        sb.Append('-');
                    }
                    sb.Append(';');
                }
                foreach (var (modelSlot, engineSlot) in m_slots[node].OrderBy(static pair => pair.Key)) {
                    sb.Append('g').Append(modelSlot).Append('=').Append(row.Server.Population.Generation(index: engineSlot) - m_generationBase[(node, engineSlot)]).Append(';');
                }
                var leases = row.Server.ExecuteAuthorityOperation(operation: () => row.Server.TransferEscrow.Capture().Leases.Select(selector: lease => KeyName(key: lease.Key)).Order(comparer: StringComparer.Ordinal).ToArray());
                var inDoubt = m_rows.Host.CaptureRow(row: row.Instance).InDoubtTransfers.Select(selector: transfer => KeyName(key: new WorldTransferKey(SourceAuthority: row.Server.AuthorityIdentity, TransferId: transfer.TransferId))).Order(comparer: StringComparer.Ordinal).ToArray();

                sb.Append("L[").Append(string.Join(',', leases)).Append("]D[").Append(string.Join(',', inDoubt)).Append(']');
                if (node == Model.Node.H) {
                    var owed = row.Server.ExecuteAuthorityOperation(operation: () => row.Server.TransferEscrow.Custody.OwedNotices());

                    for (var index = 0; (index < m_scope.Companions); index++) {
                        var title = m_rows.Title(companion: m_rows.Companions[index]);

                        sb.Append('T').Append(index + 2).Append('=').Append(title.Kind).Append(':').Append((title.Kind == WorldCustodyKind.Pending) ? KeyName(key: title.PendingKey!.Value) : NodeOfAuthority(authority: title.Custodian).ToString()).Append('@').Append(title.Epoch).Append("/m").Append(title.MaxIssued).Append("/i").Append(title.Installed ? 1 : 0).Append("/r").Append(string.Join(',', title.Recalls)).Append("/n").Append(owed.Count(predicate: notice => (notice.Incarnation == m_rows.Companions[index]))).Append(';');
                    }
                } else {
                    var owed = row.Server.ExecuteAuthorityOperation(operation: () => row.Server.TransferEscrow.Custody.OwedHopNotices());

                    sb.Append("N[").Append(string.Join(',', owed.Select(selector: notice => $"{KeyName(key: notice.Key)}:{IdOf(incarnation: notice.Incarnation)}").Order(comparer: StringComparer.Ordinal))).Append(']');
                }
                sb.Append('}');
            }
            return sb.ToString();
        }
        private string ProjectModel(Model.ModelState state) {
            var sb = new StringBuilder();

            for (var node = Model.Node.H; (node <= Model.Node.B); node++) {
                var view = Model.CustodyModel.Fold(s: state, at: node);

                sb.Append(node).Append('{');
                sb.Append("p").Append(view.Has(id: Model.Ids.Player) ? 1 : 0).Append(';');
                for (var id = Model.Ids.C1; (id < (Model.Ids.C1 + m_scope.Companions)); id++) {
                    sb.Append(id).Append(':');
                    if (view.Body(id: id) is { } body) {
                        sb.Append('@').Append(body.Epoch).Append(view.Follows(id: id) ? "f" : "-");
                    } else {
                        sb.Append('-');
                    }
                    sb.Append(';');
                }
                foreach (var (modelSlot, _) in m_slots[node].OrderBy(static pair => pair.Key)) {
                    sb.Append('g').Append(modelSlot).Append('=').Append(view.SlotGen[modelSlot]).Append(';');
                }
                sb.Append("L[").Append(string.Join(',', state.At(node: node).Leases.Keys.Order(comparer: StringComparer.Ordinal))).Append("]D[").Append(string.Join(',', view.InDoubt.Select(static dep => dep.Key).Order(comparer: StringComparer.Ordinal))).Append(']');
                if (node == Model.Node.H) {
                    for (var id = Model.Ids.C1; (id < (Model.Ids.C1 + m_scope.Companions)); id++) {
                        var custody = view.Custody[id];
                        var owed = custody.Recalls.Count(predicate: epoch => !view.Noticed.Contains(item: $"{id}:recalled@{epoch}")) + ((custody.Installed && !view.Noticed.Contains(item: $"{id}:released")) ? 1 : 0);

                        sb.Append('T').Append(id).Append('=').Append(custody.Kind).Append(':').Append((custody.Kind == Model.CustodyKind.Pending) ? custody.PendingKey : custody.Custodian.ToString()).Append('@').Append(custody.Epoch).Append("/m").Append(custody.MaxIssued).Append("/i").Append(custody.Installed ? 1 : 0).Append("/r").Append(string.Join(',', custody.Recalls)).Append("/n").Append(owed).Append(';');
                    }
                } else {
                    sb.Append("N[").Append(string.Join(',', view.UnackedSettles(node: node).Select(static settle => $"{settle.Key}:{settle.Id}").Order(comparer: StringComparer.Ordinal))).Append(']');
                }
                sb.Append('}');
            }
            return sb.ToString();
        }
        private bool FollowsLive(Model.Node node, HostRow row, WorldEntityAddress companion) {
            if (node == Model.Node.H) {
                return (CompanionRows.SlotOf(row: row, incarnation: m_rows.Player) == 0);
            }
            return row.Server.ExecuteAuthorityOperation(operation: () => row.Server.TransferEscrow.Custody.TryBinding(arrival: out _, binding: out var binding, carried: out _, follows: out var follows, incarnation: companion) &&
                row.Server.Population.IsActive(index: binding.Slot) &&
                (row.Server.Population.Generation(index: binding.Slot) == binding.Generation) &&
                (row.Server.Population.ResolveIncarnation(authority: row.Server.AuthorityIdentity, index: binding.Slot) == follows));
        }

        // ---- naming ----------------------------------------------------------------------------------------------

        private HostRow RowOf(Model.Node node) => m_rows.Row(name: RowNames[node]);
        private static Model.Node NodeOfRow(string name) => RowNames.First(predicate: pair => (pair.Value == name)).Key;
        private Model.Node NodeOfAuthority(string authority) {
            foreach (var (node, name) in RowNames) {
                if (string.Equals(a: m_rows.Row(name: name).Server.AuthorityIdentity, b: authority, comparisonType: StringComparison.Ordinal)) {
                    return node;
                }
            }
            throw new InvalidOperationException(message: $"no row has the authority '{authority}'");
        }
        private WorldEntityAddress IncarnationOf(int id) => ((id == Model.Ids.Player) ? m_rows.Player : m_rows.Companions[id - Model.Ids.C1]);
        private int IdOf(WorldEntityAddress incarnation) => ((incarnation == m_rows.Player) ? Model.Ids.Player : (Array.IndexOf(array: m_rows.Companions, value: incarnation) is var index && (index >= 0)) ? (Model.Ids.C1 + index) : Model.Ids.Stranger);
        private void Bind(WorldTransferKey engineKey, string modelKey) {
            m_keyNames[engineKey] = modelKey;
            m_keysOf[modelKey] = engineKey;
        }
        // The model's name of an engine key, bound when the key was minted.
        private string KeyName(WorldTransferKey key) {
            if (m_keyNames.TryGetValue(key: key, value: out var name)) {
                return name;
            }
            return $"{NodeOfAuthority(authority: key.SourceAuthority)}?{key.TransferId}";
        }
        private void LearnMembers(WorldTransferKey key, IReadOnlyList<WorldTransferReservationMember> members) {
            var ids = (m_memberIds.TryGetValue(key: key, value: out var known) ? known : (m_memberIds[key] = new Dictionary<int, int>()));

            for (var ordinal = 0; (ordinal < members.Count); ordinal++) {
                if (members[ordinal].Mobility is { } mobility) {
                    ids[members[ordinal].IdAt(ordinal: ordinal)] = IdOf(incarnation: mobility.Incarnation);
                }
            }
        }
        // The destination's reply names the engine body each leased member takes; the model's Reserved names its slot.
        private void LearnSlots(Model.Node from, string key, WorldTransferReservationRequest request, WorldTransferReservationReply reply) {
            if (!reply.Accepted || (Model.CustodyModel.LaneHeads(s: m_model).FirstOrDefault(predicate: msg => (msg.Kind == Model.MsgKind.Reserved) && (msg.Key == key)) is not { } reserved)) {
                return;
            }
            var leased = reply.LeasedIds(request: request);
            var engineKey = new WorldTransferKey(SourceAuthority: request.SourceAuthority, TransferId: request.TransferId);

            for (var index = 0; (index < leased.Count) && (index < reply.BodyIndices.Count); index++) {
                var id = ModelMemberId(key: engineKey, memberId: leased[index]);

                if (reserved.Members.FirstOrDefault(predicate: member => (member.Id == id)) is { } member) {
                    m_slots[reserved.From][member.Slot] = reply.BodyIndices[index];
                }
            }
        }
        private int ModelMemberId(WorldTransferKey key, int memberId) => ((m_memberIds.TryGetValue(key: key, value: out var ids) && ids.TryGetValue(key: memberId, value: out var id)) ? id : -1);
        private static string Ids(IEnumerable<int> ids) => string.Join(',', ids.Order());
        // The comparable shape of an engine record, in the model's own vocabulary.
        private string Shape(WorldCrossingRecord record, Model.Node node) {
            var self = RowOf(node: node).Server.AuthorityIdentity;

            switch (record) {
                case WorldCrossingRecord.Allocation:
                    return $"Minted({((m_pendingTransfer[(int)node] is { } pending) ? KeyName(key: new WorldTransferKey(SourceAuthority: self, TransferId: pending)) : "?")})";
                case WorldCrossingRecord.Hop hop:
                    return $"Hop({KeyName(key: hop.Key)}:{NodeOfAuthority(authority: hop.From)}>{NodeOfAuthority(authority: hop.To)}:{string.Join(',', hop.Members.Select(selector: member => $"{IdOf(incarnation: member.Incarnation)}:{member.Epoch}>{member.Next}"))})";
                case WorldCrossingRecord.Lease lease:
                    return $"Leased({KeyName(key: lease.Key)})";
                case WorldCrossingRecord.Tombstone tombstone:
                    return $"Dead({KeyName(key: tombstone.Key)})";
                case WorldCrossingRecord.Departure departure: {
                    var key = new WorldTransferKey(SourceAuthority: self, TransferId: departure.Transfer.TransferId);

                    for (var ordinal = 0; (ordinal < departure.Transfer.Landed.Count); ordinal++) {
                        (m_memberIds.TryGetValue(key: key, value: out var ids) ? ids : (m_memberIds[key] = new Dictionary<int, int>()))[((departure.Transfer.CommitMembers[ordinal].MemberId < 0) ? ordinal : departure.Transfer.CommitMembers[ordinal].MemberId)] = IdOf(incarnation: departure.Transfer.Landed[ordinal].Mobility.Incarnation);
                    }
                    return $"Dep({KeyName(key: key)}>{NodeOfAuthority(authority: departure.Transfer.TargetAuthority)}:{Ids(ids: departure.Transfer.Landed.Select(selector: member => IdOf(incarnation: member.Mobility.Incarnation)))})";
                }
                case WorldCrossingRecord.Arrival arrival: {
                    LearnMembers(key: arrival.Value.Key, members: arrival.Value.Request.Members);
                    return $"Arr({KeyName(key: arrival.Value.Key)}<{NodeOfAuthority(authority: arrival.Value.Request.SourceAuthority)}:{Ids(ids: arrival.Value.Request.Members.Select(selector: member => IdOf(incarnation: member.Mobility!.Value.Incarnation)))}:r{Ids(ids: arrival.Value.Readopted.Select(selector: IdOf))})";
                }
                case WorldCrossingRecord.Settlement settlement: {
                    var key = new WorldTransferKey(SourceAuthority: self, TransferId: settlement.TransferId);

                    return $"Settle({KeyName(key: key)}:l{Ids(ids: settlement.Landed.Select(selector: id => ModelMemberId(key: key, memberId: id)))}:r{Ids(ids: settlement.Retired.Select(selector: id => ModelMemberId(key: key, memberId: id)))})";
                }
                case WorldCrossingRecord.HopDone done:
                    return $"HopDone({KeyName(key: done.Key)}:{IdOf(incarnation: done.Incarnation)}:{(done.Arrived ? 1 : 0)})";
                case WorldCrossingRecord.HopAcked acked:
                    return $"HopAcked({KeyName(key: acked.Key)}:{IdOf(incarnation: acked.Incarnation)})";
                case WorldCrossingRecord.Recall recall:
                    return $"Recall({IdOf(incarnation: recall.Incarnation)}:{recall.Epoch})";
                case WorldCrossingRecord.Noticed noticed:
                    return $"Noticed({IdOf(incarnation: noticed.Incarnation)}:{((noticed.What == WorldCustodyNotice.Released) ? "released" : $"recalled@{noticed.Epoch}")})";
                case WorldCrossingRecord.Release release:
                    return $"Install({IdOf(incarnation: release.Incarnation)})";
                case WorldCrossingRecord.Retire retire:
                    return $"Retire({IdOf(incarnation: retire.Incarnation)})";
                default:
                    return record.GetType().Name;
            }
        }
        // The comparable shape of a model record; null for one the engine's crossing log never carries.
        private static string? Shape(Model.Rec rec) => rec switch {
            Model.Minted minted => $"Minted({minted.Key})",
            Model.Hop hop => $"Hop({hop.Key}:{hop.From}>{hop.To}:{string.Join(',', hop.Members.Select(static m => $"{m.Id}:{m.Epoch}>{m.Next}"))})",
            Model.Leased leased => $"Leased({leased.Key})",
            Model.Dead dead => $"Dead({dead.Key})",
            Model.Dep dep => $"Dep({dep.Key}>{dep.To}:{Ids(ids: dep.Members.Select(static m => m.Id))})",
            Model.Arr arr => $"Arr({arr.Key}<{arr.From}:{Ids(ids: arr.Members.Select(static m => m.Id))}:r{Ids(ids: arr.Readopted)})",
            Model.Settle settle => $"Settle({settle.Key}:l{Ids(ids: settle.Landed)}:r{Ids(ids: settle.Retired)})",
            Model.HopDone done => $"HopDone({done.Key}:{done.Id}:{(done.Arrived ? 1 : 0)})",
            Model.HopAcked acked => $"HopAcked({acked.Key}:{acked.Id})",
            Model.Recall recall => $"Recall({recall.Id}:{recall.NewEpoch})",
            Model.Noticed noticed => $"Noticed({noticed.Id}:{noticed.What})",
            Model.Install install => $"Install({install.Id})",
            Model.Retire retire => $"Retire({retire.Id})",
            _ => null,
        };
        private void Trace(string text) => m_trace.Add(item: text);
        private void Defer(string text) {
            if (!m_deferred.Contains(item: text)) {
                m_deferred.Add(item: text);
            }
        }
        private void Diverge(string text) => m_divergences.Add(item: $"{text}{Environment.NewLine}  trace: {string.Join(" | ", m_trace.TakeLast(count: 14))}");
    }
}
