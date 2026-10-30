using Xunit;

namespace Puck.World.Protocol.Tests;

/// <summary>The companion custody protocol (docs/plans/companion-redesign.md) holds the decision's invariants on every
/// state a small scope reaches under a fault budget, and converges from every quiescent one: one embodiment at the
/// current epoch, no lost title, no silent re-issue, the held slot claimable only by the genuine return, every landing
/// fenced by home's hop record, durable state carried, and the records folding to the live facts by two independent
/// derivations. Each review finding is also a named trace.</summary>
public sealed class CompanionCustodyModelLawTests(ITestOutputHelper output) {
    // Each scope: the player's route, how many companions follow, whether B declares the role, the fault classes
    // enabled, and how many faults a trace may carry.
    public static TheoryData<string, int, bool, Fault, int> Scopes => new() {
        { "A", 1, true, Fault.Loss | Fault.Dup, 2 },
        { "A", 1, true, Fault.Crash | Fault.Deadline, 2 },
        { "A", 1, true, Fault.Log | Fault.Crash, 2 },
        { "A", 1, true, Fault.Partition | Fault.Disconnect, 2 },
        { "A", 1, true, Fault.Install | Fault.Recall | Fault.Disconnect, 2 },
        { "A", 1, true, Fault.Deadline | Fault.Forge | Fault.Disconnect, 2 },
        { "A", 1, true, Fault.Write | Fault.Census | Fault.Disconnect, 2 },
        { "A", 1, true, Fault.All, 1 },
        { "A", 2, true, Fault.Loss, 1 },
        { "A", 2, true, Fault.Admission | Fault.Disconnect, 1 },
        { "A", 2, true, Fault.Install | Fault.Recall | Fault.Disconnect, 1 },
        { "AB", 1, true, Fault.Loss, 1 },
        { "AB", 1, false, Fault.Disconnect, 1 },
        { "AB", 1, true, Fault.Disconnect | Fault.Partition, 1 },
        { "AB", 2, true, Fault.Admission, 1 },
        { "AH", 1, true, Fault.None, 0 },
        { "AB", 2, true, Fault.None, 0 },
        { "ABH", 1, true, Fault.None, 0 },
    };

    [Theory]
    [MemberData(nameof(Scopes))]
    public void EveryReachableStateHoldsTheInvariantsAndConverges(string route, int companions, bool bAccepts, Fault allowed, int faults) {
        var result = Explore(bAccepts: bAccepts, route: Route(route), companions: companions, faults: faults, allowed: allowed);

        output.WriteLine(message: $"route={route} companions={companions} B={bAccepts} faults={allowed}x{faults}: {result}");
        Assert.True(condition: (result.States > 50), userMessage: $"the scope explored only {result.States} states");
        Assert.True(condition: result.Converged > 0);
    }

    // ---- traces for the spec review's findings --------------------------------------------------------------------

    [Fact]
    public void Epochs_EveryLandingAdvancesByOneIncludingTheReturn() {
        var s = Start(route: "AB");

        s = Drive(s: s, "cross H>A");
        s = Drain(s: s);
        Assert.Equal(expected: 1, actual: Fold(s: s, Node.A).Body(id: Ids.C1)!.Epoch);
        Assert.Equal(expected: (CustodyKind.Away, Node.A, 1), actual: Title(s: s, Ids.C1));
        s = Drive(s: s, "disconnect at A");
        s = Converge(s: s);
        Assert.Equal(expected: (CustodyKind.Home, Node.H, 2), actual: Title(s: s, Ids.C1));
        Assert.Equal(expected: 2, actual: Fold(s: s, Node.H).Body(id: Ids.C1)!.Epoch);
        Assert.Equal(expected: 2, actual: s.Nodes[0].Records.OfType<Arr>().Single(static a => a.Readopted.Length > 0).Members.Single().Epoch);
    }

    [Fact]
    public void HomeResolvesAPendingHopByAnAuthorizedCustodyQuery() {
        var s = Start(route: "AB");

        s = Drive(s: s, "cross H>A");
        s = Drain(s: s);
        s = Drive(s: s, "cross A>B");
        s = DrainUntil(s: s, static m => m.Kind == MsgKind.HopSettled);
        s = Drive(s: s, "drop A>H:HopSettled");
        s = Drain(s: s);
        Assert.Equal(expected: CustodyKind.Pending, actual: Title(s: s, Ids.C1).Kind);
        s = Drive(s: s, "resolve A1:2");
        s = Drive(s: s, "deliver H>B:CustodyQuery");
        s = Drive(s: s, "deliver B>H:CustodyReply:A1::2,Landed");
        Assert.Equal(expected: (CustodyKind.Away, Node.B, 2), actual: Title(s: s, Ids.C1));
        _ = Converge(s: s);

        // Only the title authority may ask, and the answer comes from durable records.
        var probe = s.Clone();

        CustodyModel.Deliver(s: probe, msg: new Msg(From: Node.A, To: Node.B, Kind: MsgKind.CustodyQuery, Key: "A1", Members: [], Arg: "2", Dup: false));
        Assert.Contains(collection: probe.Network, filter: static m => (m.Kind == MsgKind.CustodyReply) && m.Arg.EndsWith("Unauthorized", StringComparison.Ordinal));
    }

    [Fact]
    public void TwoCompanionsSettlePerMemberAndPartialLandingRestoresTheOther() {
        var s = Start(route: "AB", companions: 2);

        s = Drive(s: s, "cross H>A");
        s = Drain(s: s);
        Assert.True(condition: Fold(s: s, Node.A).Follows(id: Ids.C1) && Fold(s: s, Node.A).Follows(id: Ids.C2));
        s = Drive(s: s, "cross A>B");
        s = DrainUntil(s: s, static m => (m.Kind == MsgKind.Reserved) && (m.From == Node.B));
        s = Drive(s: s, "refuse at commit B:A1:2");
        s = Drain(s: s);

        var landed = s.Nodes[2].Records.OfType<Arr>().Single();

        Assert.Equal(expected: new[] { Ids.Player, Ids.C2 }, actual: landed.Members.Select(static m => m.Id).ToArray());
        Assert.Equal(expected: new[] { Ids.Player, Ids.C2 }, actual: s.Nodes[1].Records.OfType<Settle>().Single().Landed);
        Assert.True(condition: Fold(s: s, Node.A).Has(id: Ids.C1));
        Assert.Contains(collection: s.Nodes[0].Records.OfType<HopDone>(), filter: static d => (d.Id == Ids.C1) && !d.Arrived);
        Assert.Contains(collection: s.Nodes[0].Records.OfType<HopDone>(), filter: static d => (d.Id == Ids.C2) && d.Arrived);
        Assert.Equal(expected: (CustodyKind.Away, Node.A, 1), actual: Title(s: s, Ids.C1));
        Assert.Equal(expected: (CustodyKind.Away, Node.B, 2), actual: Title(s: s, Ids.C2));
        s = Converge(s: s);
        Assert.Equal(expected: (CustodyKind.Home, Node.H, 2), actual: Title(s: s, Ids.C1));
        Assert.True(condition: Fold(s: s, Node.B).Follows(id: Ids.C2));
    }

    [Fact]
    public void ACrashBetweenTheHopRecordAndTheDepartureRecoversTheTitle() {
        var s = Start(route: "AB");

        s = Drive(s: s, "cross H>A");
        Assert.Contains(collection: s.Nodes[0].Records, filter: static r => r is Minted);
        s = Drive(s: s, "authorize H1");
        Assert.Contains(collection: s.Nodes[0].Records, filter: static r => r is Hop);
        Assert.DoesNotContain(collection: s.Nodes[0].Records, filter: static r => r is Dep);
        Assert.Equal(expected: CustodyKind.Pending, actual: Title(s: s, Ids.C1).Kind);
        s = Drive(s: s, "crash H");
        s = Drive(s: s, "abandon H:H1");
        s = Drive(s: s, "settle hop H1:2");
        Assert.Equal(expected: (CustodyKind.Home, Node.H, 0), actual: Title(s: s, Ids.C1));
        s = Converge(s: s);
        Assert.True(condition: Fold(s: s, Node.H).Has(id: Ids.C1));
        Assert.DoesNotContain(collection: s.Nodes[1].Records, filter: static r => r is Arr);
    }

    [Fact]
    public void ACrashBetweenTheRecallAndItsNoticeStillNotifies() {
        var s = Start(route: "AB");

        s = Drive(s: s, "cross H>A");
        s = Drain(s: s);
        s = Drive(s: s, "disconnect at A");
        s = Drive(s: s, "reconnect");
        s = Drive(s: s, "partition H-A");
        s = Drive(s: s, "recall 2");
        Assert.DoesNotContain(collection: s.Nodes[0].Records, filter: static r => r is Noticed);
        s = Drive(s: s, "crash", optional: true);
        s = Drive(s: s, "heal H-A");
        s = Converge(s: s);
        Assert.Contains(collection: s.Nodes[0].Records, filter: static r => r is Noticed { What: "recalled" });
        Assert.Contains(collection: s.Nodes[1].Records, filter: static r => r is Retire { Cause: "Stale" });
        Assert.Equal(expected: 2, actual: Title(s: s, Ids.C1).Epoch);
    }

    [Fact]
    public void KeysAreAllocatedDurablyBeforeUse() {
        var s = Start(route: "AB");

        s = Drive(s: s, "cross H>A");
        s = DrainUntil(s: s, static m => (m.Kind == MsgKind.Reserved) && (m.From == Node.A));
        Assert.Contains(collection: s.Nodes[1].Records, filter: static r => r is Leased { Key: "H1" });
        s = Drive(s: s, "crash H");
        s = Drive(s: s, "abandon H:H1");
        s = Drive(s: s, "settle hop H1:2");
        s = Drive(s: s, "cross H>B");
        Assert.Equal(expected: new[] { "H1", "H2" }, actual: s.Nodes[0].Records.OfType<Minted>().Select(static m => m.Key).ToArray());
        s = Converge(s: s);
        Assert.DoesNotContain(collection: s.Nodes[1].Records, filter: static r => r is Arr);
        Assert.Equal(expected: Node.B, actual: s.PlayerAt);
    }

    [Fact]
    public void ADestinationVerifiesHomesHopAuthorization() {
        var s = Start(route: "AB");

        s = Drive(s: s, "cross H>A");
        s = Drain(s: s);
        Assert.True(condition: Fold(s: s, Node.A).Follows(id: Ids.C1));
        // The custodian itself tries to move the companion without home's hop record.
        s = Drive(s: s, "forge A>B:2");
        s = Drain(s: s);
        Assert.Contains(collection: s.Trace, filter: static t => t.Contains("A>H:HopQuery", StringComparison.Ordinal));
        Assert.Contains(collection: s.Trace, filter: static t => t.Contains("B>A:ReserveRefused", StringComparison.Ordinal) && t.Contains("Unverified", StringComparison.Ordinal));
        Assert.Empty(collection: s.Nodes[2].Records.OfType<Leased>());
        Assert.Equal(expected: (CustodyKind.Away, Node.A, 1), actual: Title(s: s, Ids.C1));
        Assert.True(condition: Fold(s: s, Node.A).Follows(id: Ids.C1));
        _ = Converge(s: s);
    }

    [Fact]
    public void AForgedReturnToHomeIsStaleWhileTheCurrentCustodianKeepsCustody() {
        var s = Start(route: "AB");

        s = Drive(s: s, "cross H>A");
        s = Drain(s: s);
        s = Drive(s: s, "cross A>B");
        s = Drain(s: s);
        s = Drive(s: s, "forge A>H:2");
        s = Drain(s: s);
        Assert.Contains(collection: s.Trace, filter: static t => t.Contains("H>A:ReserveRefused", StringComparison.Ordinal) && t.Contains("Stale", StringComparison.Ordinal));
        Assert.Equal(expected: (CustodyKind.Away, Node.B, 2), actual: Title(s: s, Ids.C1));
        _ = Converge(s: s);
    }

    [Fact]
    public void TheCensusNeverTakesAHeldSlotAndTheReturnLandsInIt() {
        var s = Start(route: "AB");

        s = Drive(s: s, "cross H>A");
        s = Drain(s: s);
        s = Drive(s: s, "census");

        var bot = Fold(s: s, Node.H).Bodies.Values.Single(static b => b.Id >= Ids.BotBase);

        Assert.NotEqual(expected: 1, actual: bot.Slot);
        Assert.NotEqual(expected: 0, actual: bot.Slot);
        s = Drive(s: s, "disconnect at A");
        s = Converge(s: s);
        Assert.Equal(expected: 1, actual: Fold(s: s, Node.H).Body(id: Ids.C1)!.Slot);
    }

    [Fact]
    public void AStrangerFillsTheCapAndThePlayersOwnAdmissionIsRefused() {
        var s = Start(route: "AB");

        s = Drive(s: s, "stranger B");
        s = Drive(s: s, "cross H>A");
        s = Drain(s: s);
        s = Drive(s: s, "cross A>B");
        s = Drain(s: s);
        Assert.Contains(collection: s.Trace, filter: static t => t.Contains("B>A:ReserveRefused", StringComparison.Ordinal) && t.Contains("Player", StringComparison.Ordinal));
        Assert.Equal(expected: Node.A, actual: s.PlayerAt);
        Assert.True(condition: Fold(s: s, Node.A).Follows(id: Ids.C1));
        Assert.Equal(expected: 1, actual: Fold(s: s, Node.B).Humans);
        _ = Converge(s: s);
    }

    [Fact]
    public void WritesAbroadComeHomeWithTheCompanion() {
        var s = Start(route: "AB");

        s = Drive(s: s, "cross H>A");
        s = Drain(s: s);
        s = Drive(s: s, "write A:2");
        Assert.Equal(expected: 1, actual: Fold(s: s, Node.A).Body(id: Ids.C1)!.Image);
        s = Drive(s: s, "disconnect at A");
        s = Converge(s: s);
        Assert.Equal(expected: 1, actual: Fold(s: s, Node.H).Body(id: Ids.C1)!.Image);
        Assert.Equal(expected: 1, actual: Fold(s: s, Node.H).Custody[Ids.C1].KnownImage);
    }

    [Fact]
    public void AnUnansweredReturnStillShedsAfterTheGraceWindow() {
        var s = Start(route: "AB");

        s = Drive(s: s, "cross H>A");
        s = Drain(s: s);
        s = Drive(s: s, "partition H-A");
        s = Drive(s: s, "disconnect at A");
        s = Drive(s: s, "sweep A:2");
        Assert.Single(collection: s.Nodes[1].Outbound);
        s = Drive(s: s, "strand A:2");
        s = Drive(s: s, "elapse A:2");
        s = Drive(s: s, "shed A:2");
        Assert.False(condition: Fold(s: s, Node.A).Has(id: Ids.C1));
        Assert.Contains(collection: s.Nodes[1].Records, filter: static r => r is Dep { Shed: true });
        s = Drive(s: s, "crash A");
        s = Drive(s: s, "heal H-A");
        s = Converge(s: s);
        Assert.True(condition: Fold(s: s, Node.H).Has(id: Ids.C1));
        Assert.Contains(collection: s.Nodes[1].Records, filter: static r => r is Settle { Landed: [Ids.C1] });
    }

    [Fact]
    public void ARefusedAppendIsATransientRefusalTheRequesterRetries() {
        var s = Start(route: "AB");

        s = Drive(s: s, "cross H>A");
        s = Drain(s: s);
        s = Drive(s: s, "cross A>B");
        s = Drive(s: s, "append Refused H");
        s = Drive(s: s, "deliver A>H:HopRequest");
        Assert.Contains(collection: s.Network, filter: static m => (m.Kind == MsgKind.HopAnswer) && m.Arg.EndsWith("Unavailable", StringComparison.Ordinal));
        Assert.DoesNotContain(collection: s.Nodes[0].Records, filter: static r => r is Hop { Key: "A1" });
        s = Converge(s: s);
        Assert.True(condition: Fold(s: s, Node.B).Follows(id: Ids.C1));
        Assert.Equal(expected: (CustodyKind.Away, Node.B, 2), actual: Title(s: s, Ids.C1));
    }

    [Fact]
    public void AnUncertainArrivalIsResolvedOnlyByTheDestinationsRecovery() {
        var s = Start(route: "AB");

        s = Drive(s: s, "cross H>A");
        s = DrainUntil(s: s, static m => m.Kind == MsgKind.Commit);
        s = Drive(s: s, "append UncertainLanded A");
        s = Drive(s: s, "deliver H>A:Commit");
        Assert.True(condition: s.Nodes[1].Latched);
        Assert.Contains(collection: s.Network, filter: static m => m.Kind == MsgKind.Uncertain);
        s = Drive(s: s, "deliver A>H:Uncertain");
        Assert.Single(collection: Fold(s: s, Node.H).InDoubt);
        s = Drive(s: s, "retry status H:H1");
        s = Drive(s: s, "deliver H>A:Status");
        s = Drive(s: s, "deliver A>H:StatusReply:H1::Uncertain");
        Assert.Single(collection: Fold(s: s, Node.H).InDoubt);
        s = Converge(s: s);
        Assert.Equal(expected: (CustodyKind.Away, Node.A, 1), actual: Title(s: s, Ids.C1));
        Assert.True(condition: Fold(s: s, Node.A).Follows(id: Ids.C1));
    }

    [Fact]
    public void AnInstallWinsOverADepartureInDoubt() {
        var s = Start(route: "AB");

        s = Drive(s: s, "cross H>A");
        s = DrainUntil(s: s, static m => m.Kind == MsgKind.Commit);
        Assert.Single(collection: Fold(s: s, Node.H).InDoubt);
        s = Drive(s: s, "install 2");
        s = Drive(s: s, "expire A:");
        s = Converge(s: s);
        Assert.Contains(collection: s.Nodes[0].Records, filter: static r => r is Settle { Retired: [Ids.C1] });
        Assert.False(condition: Fold(s: s, Node.H).Has(id: Ids.C1));
        Assert.False(condition: Fold(s: s, Node.A).Has(id: Ids.C1));
        Assert.Contains(collection: s.Nodes[0].Records, filter: static r => r is Noticed { What: "released" });
    }

    [Fact]
    public void ReleaseIsHomesRecordAndTheCustodianRetiresOnContact() {
        var s = Start(route: "AB");

        s = Drive(s: s, "cross H>A");
        s = Drain(s: s);
        s = Drive(s: s, "partition H-A");
        s = Drive(s: s, "install 2");
        s = Drive(s: s, "disconnect at A");
        s = Drive(s: s, "sweep A:2");
        s = Drive(s: s, "heal H-A");
        s = Converge(s: s);
        Assert.Equal(expected: CustodyKind.Released, actual: Title(s: s, Ids.C1).Kind);
        Assert.Contains(collection: s.Nodes[1].Records, filter: static r => (r is Retire { Cause: "Released" }) || (r is Settle { Retired: [Ids.C1] }));
    }

    [Fact]
    public void ASecondReservationForTheSameIncarnationIsRefusedAsADuplicate() {
        var s = Start(route: "AB");

        s = Drive(s: s, "cross H>A");
        s = Drain(s: s);
        s = Drive(s: s, "disconnect at A");
        s = Drive(s: s, "sweep A:2");
        s = Drive(s: s, "deliver A>H:Reserve");
        s = Drive(s: s, "forge A>H:2");
        s = Drive(s: s, "deliver A>H:Reserve");
        Assert.Contains(collection: s.Network, filter: static m => (m.Kind == MsgKind.ReserveRefused) && (m.Arg == "Duplicate"));
        s = Converge(s: s);
        Assert.Single(collection: s.Nodes[1].Records.OfType<Dep>());
    }

    // ---- the explorer ---------------------------------------------------------------------------------------------

    private static Node[] Route(string route) => route.Select(static c => Enum.Parse<Node>(value: c.ToString())).ToArray();
    private static ModelState Start(string route, int companions = 1, bool bAccepts = true) => new(aAccepts: true, bAccepts: bAccepts, route: Route(route), companions: companions);
    private static View Fold(ModelState s, Node node) => CustodyModel.Fold(s: s, at: node);
    private static (CustodyKind Kind, Node Custodian, int Epoch) Title(ModelState s, int id) {
        var custody = Fold(s: s, Node.H).Custody[id];

        return (custody.Kind, custody.Custodian, custody.Epoch);
    }

    private static ModelState Drive(ModelState s, string prefix, bool optional = false) {
        foreach (var (name, next) in CustodyModel.Successors(s: s, faultBudget: int.MaxValue)) {
            if (name.StartsWith(prefix, StringComparison.Ordinal)) {
                next.Trace.Add(item: name);
                CustodyModel.CheckInvariants(s: next);
                return next;
            }
        }
        if (optional) {
            return s;
        }
        throw new Xunit.Sdk.XunitException($"no enabled action starts with '{prefix}'; enabled: {string.Join(" | ", CustodyModel.Successors(s: s, faultBudget: int.MaxValue).Select(static p => p.Name))}");
    }
    private static ModelState Drain(ModelState s) => DrainUntil(s: s, static _ => false);
    private static ModelState DrainUntil(ModelState s, Func<Msg, bool> stop) {
        for (var guard = 0; guard < 300; guard++) {
            var msg = s.Network.Where(m => s.LinkUp[(int)m.From, (int)m.To]).OrderBy(static m => m.Canon, StringComparer.Ordinal).FirstOrDefault();

            if (msg is null) {
                // Nothing in flight: the authorities' own bookkeeping steps run (home authorizes its hops, settles them
                // from its own settlement, and records its notices).
                var local = CustodyModel.Successors(s: s, faultBudget: 0).FirstOrDefault(static p => p.Name.StartsWith("authorize", StringComparison.Ordinal) || p.Name.StartsWith("settle hop", StringComparison.Ordinal) || p.Name.StartsWith("notify", StringComparison.Ordinal));

                if (local.Next is null) {
                    return s;
                }
                local.Next.Trace.Add(item: local.Name);
                CustodyModel.CheckInvariants(s: local.Next);
                s = local.Next;
                continue;
            }
            if (stop(msg)) {
                return s;
            }
            s = Drive(s: s, $"deliver {msg.Canon}");
        }
        throw new Xunit.Sdk.XunitException("the network never drained");
    }
    private static ModelState Converge(ModelState s) {
        try {
            return CustodyModel.Converge(s: s);
        } catch (ModelViolation violation) {
            throw new Xunit.Sdk.XunitException($"{violation.Message}\ntrace: {string.Join(" -> ", s.Trace)}");
        }
    }

    private const int StateCap = 300_000;

    private readonly record struct Exploration(int States, int Converged, int MaxDepth, long Milliseconds);

    private static Exploration Explore(bool bAccepts, Node[] route, int companions, int faults, Fault allowed) {
        var start = new ModelState(aAccepts: true, bAccepts: bAccepts, route: route, companions: companions);

        start.Budget.Allowed = allowed;

        var visited = new HashSet<string>(comparer: StringComparer.Ordinal) { start.Canon() };
        var queue = new Queue<(ModelState State, int Depth)>();
        var converged = 0;
        var maxDepth = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        queue.Enqueue(item: (start, 0));
        while (queue.Count > 0) {
            var (state, depth) = queue.Dequeue();

            maxDepth = Math.Max(maxDepth, depth);
            try {
                CustodyModel.CheckInvariants(s: state);
                // Every state reaches a quiescent one by deliveries alone, and the search explores those, so
                // convergence is checked from each quiescent state.
                if (state.Network.Count == 0) {
                    _ = CustodyModel.Converge(s: state);
                    converged++;
                }
            } catch (ModelViolation violation) {
                throw new Xunit.Sdk.XunitException($"{violation.Message}\nscope: route={string.Join("", route)} companions={companions} B={bAccepts} faults={allowed}x{faults}\ntrace: {string.Join(" -> ", state.Trace)}");
            }
            foreach (var (name, next) in CustodyModel.Successors(s: state, faultBudget: faults)) {
                var canon = next.Canon();

                if (!visited.Add(item: canon)) {
                    continue;
                }
                next.Trace.Add(item: name);
                queue.Enqueue(item: (next, depth + 1));
            }
            if (visited.Count > StateCap) {
                throw new Xunit.Sdk.XunitException($"the scope route={string.Join("", route)} companions={companions} faults={allowed}x{faults} exceeds {StateCap} states; tighten its bounds");
            }
        }

        return new Exploration(States: visited.Count, Converged: converged, MaxDepth: maxDepth, Milliseconds: watch.ElapsedMilliseconds);
    }
}
