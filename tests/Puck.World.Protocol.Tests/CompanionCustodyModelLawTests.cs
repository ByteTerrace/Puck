using Xunit;

namespace Puck.World.Protocol.Tests;

/// <summary>The companion custody protocol (docs/plans/companion-redesign.md) holds the decision's invariants on every
/// state a small scope reaches under a fault budget, and converges from every one of them: at most one committed
/// embodiment of the incarnation, no lost title, no silent re-issue, the held slot claimable by the genuine return,
/// and the records alone folding to the live custody facts. Each review finding is also a named trace.</summary>
public sealed class CompanionCustodyModelLawTests(ITestOutputHelper output) {
    // Each scope: the player's route, whether B declares the role, the fault classes enabled, and how many faults a
    // trace may carry. Every class is explored exhaustively against every protocol interleaving of its route.
    public static TheoryData<string, bool, Fault, int> Scopes => new() {
        { "AB", true, Fault.Loss | Fault.Dup, 2 },
        { "AB", false, Fault.Loss | Fault.Dup, 2 },
        { "AB", true, Fault.Crash | Fault.Expiry, 2 },
        { "AB", true, Fault.Partition | Fault.Disconnect, 2 },
        { "AB", true, Fault.Install | Fault.Recall | Fault.Disconnect, 2 },
        { "AB", true, Fault.Timeout | Fault.Forge | Fault.Disconnect, 2 },
        { "AB", true, Fault.All, 1 },
        { "AH", true, Fault.Loss | Fault.Crash, 2 },
        { "AH", true, Fault.Install | Fault.Recall | Fault.Disconnect, 2 },
        { "ABH", true, Fault.Loss, 1 },
        { "ABH", true, Fault.Disconnect | Fault.Partition, 2 },
    };

    [Theory]
    [MemberData(nameof(Scopes))]
    public void EveryReachableStateHoldsTheInvariantsAndConverges(string route, bool bAccepts, Fault allowed, int faults) {
        var result = Explore(aAccepts: true, bAccepts: bAccepts, route: Route(route), faults: faults, allowed: allowed);

        output.WriteLine(message: $"route={route} B={bAccepts} faults={allowed}x{faults}: {result}");
        Assert.True(condition: (result.States > 100), userMessage: $"the scope explored only {result.States} states");
        Assert.True(condition: result.Converged > 0);
    }

    [Fact]
    public void WithoutFaultsTheCompanionFollowsAndComesHome() {
        var result = Explore(aAccepts: true, bAccepts: true, route: Route("ABH"), faults: 0, allowed: Fault.None);

        output.WriteLine(message: $"fault-free ABH: {result}");
        Assert.True(condition: result.States > 10);
    }

    // ---- one trace per review finding ------------------------------------------------------------------------------

    [Fact]
    public void H1_TheTicketArrivesInTheCommitAndTheReturnLandsReadopted() {
        var s = Start(bAccepts: true, route: "AB");

        s = Drive(s: s, "cross H>A");
        s = Drain(s: s);
        Assert.Equal(expected: 1, actual: Fold(s: s, Node.A).CompanionEpoch);
        Assert.Equal(expected: CustodyKind.Away, actual: Fold(s: s, Node.H).Custody);
        s = Drive(s: s, "disconnect at A");
        s = Converge(s: s);
        Assert.Contains(collection: s.Nodes[0].Records.OfType<Arr>(), filter: static a => a.Readopted);
        Assert.True(condition: Fold(s: s, Node.H).CompanionBody);
    }

    [Fact]
    public void H2_H10_AdmissionAndBindingArePerMemberById() {
        var s = Start(bAccepts: false, route: "AB");

        s = Drive(s: s, "cross H>A");
        s = Drain(s: s);
        s = Drive(s: s, "cross A>B");
        s = Drain(s: s);

        var landed = s.Nodes[2].Records.OfType<Arr>().Single();

        Assert.Equal(expected: Member.PlayerId, actual: Assert.Single(collection: landed.Members).Id);
        Assert.Contains(collection: s.Nodes[1].Records.OfType<Notice>(), filter: static n => n.Text.Contains("refused by B: Role", StringComparison.Ordinal));
        s = Converge(s: s);
        Assert.True(condition: Fold(s: s, Node.H).CompanionBody);
        Assert.Equal(expected: Node.B, actual: s.PlayerAt);
    }

    [Fact]
    public void H3_TheReturnIsItsOwnOperationBoundByTheHomeRecord() {
        var s = Start(bAccepts: true, route: "AB");

        s = Drive(s: s, "cross H>A");
        s = Drain(s: s);
        s = Drive(s: s, "disconnect at A");
        s = Drive(s: s, "sweep A");
        s = Drain(s: s);

        var home = Fold(s: s, Node.H);

        Assert.Equal(expected: CustodyKind.Home, actual: home.Custody);
        Assert.Equal(expected: 1, actual: home.Epoch);
        Assert.True(condition: home.CompanionBody && !home.CompanionFollows);
    }

    [Fact]
    public void H4_HomeNeverRefusesItsOwnReturnAndLandsItReadopted() {
        var s = Start(bAccepts: true, route: "AH");

        s = Drive(s: s, "cross H>A");
        s = Drain(s: s);
        s = Drive(s: s, "cross A>H");
        s = Drain(s: s);
        Assert.DoesNotContain(collection: s.Nodes[1].Records, filter: static r => r is Retire);
        Assert.Contains(collection: s.Nodes[0].Records.OfType<Arr>(), filter: static a => a.Readopted && a.Members.Any(static m => m.Kind == MemberKind.Player));
        Assert.True(condition: Fold(s: s, Node.H).CompanionFollows);
    }

    [Fact]
    public void H5_ReleaseIsHomesRecordAndTheCustodianRetiresOnIt() {
        var s = Start(bAccepts: true, route: "AB");

        s = Drive(s: s, "cross H>A");
        s = Drain(s: s);
        s = Drive(s: s, "partition H-A");
        s = Drive(s: s, "install");
        s = Drive(s: s, "disconnect at A");
        s = Drive(s: s, "sweep A");
        s = Drive(s: s, "heal H-A");
        s = Converge(s: s);
        Assert.Equal(expected: CustodyKind.Released, actual: Fold(s: s, Node.H).Custody);
        Assert.Contains(collection: s.Nodes[1].Records, filter: static r => (r is Retire { Cause: "Released" }) || (r is Settle { Retired: true }));
        Assert.Contains(collection: s.Nodes[0].Records.OfType<Notice>(), filter: static n => n.Text.Contains("released", StringComparison.Ordinal));
    }

    [Fact]
    public void H6_AStaleTicketIsRefusedWhileTheCurrentCustodianKeepsCustody() {
        var s = Start(bAccepts: true, route: "AB");

        s = Drive(s: s, "cross H>A");
        s = Drain(s: s);
        s = Drive(s: s, "cross A>B");
        s = Drain(s: s);
        Assert.Equal(expected: CustodyKind.Away, actual: Fold(s: s, Node.H).Custody);
        Assert.Equal(expected: Node.B, actual: Fold(s: s, Node.H).Custodian);
        s = Drive(s: s, "forge A");
        s = Drain(s: s);
        Assert.Equal(expected: Node.B, actual: Fold(s: s, Node.H).Custodian);
        Assert.Empty(collection: s.Nodes[0].Leases);
        Assert.True(condition: Fold(s: s, Node.B).CompanionBody);
        _ = Converge(s: s);
    }

    [Fact]
    public void H7_APendingHopIsResolvedNeverAnsweredNoRow() {
        var s = Start(bAccepts: true, route: "AB");

        s = Drive(s: s, "cross H>A");
        s = Drain(s: s);
        s = Drive(s: s, "cross A>B");
        s = DrainUntil(s: s, static m => m.Kind == MsgKind.HopSettled);
        s = Drive(s: s, "drop A>H:HopSettled");
        s = Drain(s: s);
        Assert.Equal(expected: CustodyKind.Pending, actual: Fold(s: s, Node.H).Custody);
        s = Drive(s: s, "disconnect at B");
        s = Drive(s: s, "sweep B");
        s = DrainUntil(s: s, static m => (m.Kind == MsgKind.ReserveRefused) && (m.Arg == "Pending"));
        Assert.Equal(expected: CustodyKind.Pending, actual: Fold(s: s, Node.H).Custody);
        s = Converge(s: s);
        Assert.DoesNotContain(collection: s.Nodes[2].Records, filter: static r => r is Retire);
        Assert.True(condition: Fold(s: s, Node.H).CompanionBody);
    }

    [Fact]
    public void H8_AnInstallWinsOverADepartureInDoubt() {
        var s = Start(bAccepts: true, route: "AB");

        s = Drive(s: s, "cross H>A");
        s = Drive(s: s, "deliver H>A:Reserve");
        s = Drive(s: s, "deliver A>H:Reserved");
        Assert.Single(collection: Fold(s: s, Node.H).InDoubt);
        s = Drive(s: s, "install");
        s = Drive(s: s, "expire A:");
        s = Converge(s: s);
        Assert.Contains(collection: s.Nodes[0].Records, filter: static r => r is Settle { Retired: true });
        Assert.False(condition: Fold(s: s, Node.H).CompanionBody);
        Assert.False(condition: Fold(s: s, Node.A).CompanionBody);
    }

    [Fact]
    public void H9_TheSettlementPayloadNamesWhoLandedSoALateGrantMovesNoCustody() {
        var s = Start(bAccepts: true, route: "AB");

        s = Drive(s: s, "cross H>A");
        s = Drain(s: s);
        s = Drive(s: s, "cross A>B");
        s = Drive(s: s, "deliver A>B:Reserve");
        s = Drive(s: s, "deliver B>A:Reserved");
        Assert.Contains(collection: s.Network, filter: static m => m.Kind == MsgKind.HopRequest);
        s = Drive(s: s, "hop timeout A:");
        s = Converge(s: s);

        var home = Fold(s: s, Node.H);

        Assert.Contains(collection: s.Nodes[0].Records.OfType<HopDone>(), filter: static d => !d.CompanionArrived);
        Assert.Equal(expected: CustodyKind.Home, actual: home.Custody);
        Assert.True(condition: home.CompanionBody);
        Assert.Equal(expected: Node.B, actual: s.PlayerAt);
    }

    [Fact]
    public void M1_ASecondReservationForTheSameIncarnationIsRefusedAsADuplicate() {
        var s = Start(bAccepts: true, route: "AB");

        s = Drive(s: s, "cross H>A");
        s = Drain(s: s);
        s = Drive(s: s, "disconnect at A");
        s = Drive(s: s, "sweep A");
        s = Drive(s: s, "deliver A>H:Reserve");
        s = Drive(s: s, "forge A");
        s = Drive(s: s, "deliver A>H:Reserve");
        Assert.Contains(collection: s.Network, filter: static m => (m.Kind == MsgKind.ReserveRefused) && (m.Arg == "Duplicate"));
        s = Converge(s: s);
        Assert.Single(collection: s.Nodes[1].Records.OfType<Dep>());
    }

    [Fact]
    public void M2_TheGraceWindowAndTheShedReturnSurviveACrash() {
        var s = Start(bAccepts: true, route: "AB");

        s = Drive(s: s, "cross H>A");
        s = Drain(s: s);
        s = Drive(s: s, "partition H-A");
        s = Drive(s: s, "disconnect at A");
        s = Drive(s: s, "sweep A");
        Assert.Contains(collection: s.Nodes[1].Records, filter: static r => r is Stranded);
        s = Drive(s: s, "crash A");
        Assert.Contains(collection: s.Nodes[1].Records, filter: static r => r is Stranded);
        s = Drive(s: s, "shed A");
        Assert.False(condition: Fold(s: s, Node.A).CompanionBody);
        Assert.Single(collection: Fold(s: s, Node.A).InDoubt);
        s = Drive(s: s, "heal H-A");
        s = Converge(s: s);
        Assert.True(condition: Fold(s: s, Node.H).CompanionBody);
        Assert.Contains(collection: s.Nodes[1].Records, filter: static r => r is Settle { Arrived: true });
    }

    [Fact]
    public void Recall_SupersedesTheAwayCopyWhichRetiresOnReappearance() {
        var s = Start(bAccepts: true, route: "AB");

        s = Drive(s: s, "cross H>A");
        s = Drain(s: s);
        s = Drive(s: s, "disconnect at A");
        s = Drive(s: s, "reconnect");
        s = Drive(s: s, "partition H-A");
        s = Drive(s: s, "recall");
        Assert.Equal(expected: 2, actual: Fold(s: s, Node.H).Epoch);
        Assert.True(condition: Fold(s: s, Node.H).CompanionBody);
        s = Drive(s: s, "heal H-A");
        s = Converge(s: s);
        Assert.Contains(collection: s.Nodes[1].Records, filter: static r => r is Retire { Cause: "Stale" });
        Assert.Contains(collection: s.Nodes[0].Records.OfType<Notice>(), filter: static n => n.Text.StartsWith("companion recalled", StringComparison.Ordinal));
    }

    // ---- the explorer ---------------------------------------------------------------------------------------------

    private static Node[] Route(string route) => route.Select(static c => Enum.Parse<Node>(value: c.ToString())).ToArray();
    private static ModelState Start(bool bAccepts, string route) => new(aAccepts: true, bAccepts: bAccepts, route: Route(route));
    private static View Fold(ModelState s, Node node) => CustodyModel.Fold(node: s.At(node: node));

    private static ModelState Drive(ModelState s, string prefix, bool optional = false) {
        foreach (var (name, next) in CustodyModel.Successors(s: s, faultBudget: int.MaxValue, adversary: true)) {
            if (name.StartsWith(prefix, StringComparison.Ordinal)) {
                next.Trace.Add(item: name);
                CustodyModel.CheckInvariants(s: next);
                return next;
            }
        }
        if (optional) {
            return s;
        }
        throw new Xunit.Sdk.XunitException($"no enabled action starts with '{prefix}'; enabled: {string.Join(" | ", CustodyModel.Successors(s: s, faultBudget: int.MaxValue, adversary: true).Select(static p => p.Name))}");
    }
    private static ModelState Drain(ModelState s) => DrainUntil(s: s, static _ => false);
    private static ModelState DrainUntil(ModelState s, Func<Msg, bool> stop) {
        for (var guard = 0; guard < 200; guard++) {
            var msg = s.Network.Where(m => s.LinkUp[(int)m.From, (int)m.To]).OrderBy(static m => m.Canon, StringComparer.Ordinal).FirstOrDefault();

            if ((msg is null) || stop(msg)) {
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

    private static Exploration Explore(bool aAccepts, bool bAccepts, Node[] route, int faults, Fault allowed) {
        var start = new ModelState(aAccepts: aAccepts, bAccepts: bAccepts, route: route);
        var adversary = (allowed != Fault.None);

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
                throw new Xunit.Sdk.XunitException($"{violation.Message}\nscope: A={aAccepts} B={bAccepts} route={string.Join("", route)} faults={faults}\ntrace: {string.Join(" -> ", state.Trace)}");
            }
            foreach (var (name, next) in CustodyModel.Successors(s: state, faultBudget: faults, adversary: adversary)) {
                var canon = next.Canon();

                if (!visited.Add(item: canon)) {
                    continue;
                }
                next.Trace.Add(item: name);
                queue.Enqueue(item: (next, depth + 1));
            }
            if (visited.Count > StateCap) {
                throw new Xunit.Sdk.XunitException($"the scope A={aAccepts} B={bAccepts} route={string.Join("", route)} faults={faults} exceeds {StateCap} states; tighten its bounds");
            }
        }

        return new Exploration(States: visited.Count, Converged: converged, MaxDepth: maxDepth, Milliseconds: watch.ElapsedMilliseconds);
    }
}
