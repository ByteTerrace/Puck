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
    // Each scope: the player's route, how many companions follow, whether B declares the role, the fault classes
    // enabled, how many faults a trace may carry, and how many of one class (partitions and deadlines allow one more).
    // Each scope: the player's route, how many companions follow, slots a custodian offers, whether B declares the
    // role, the fault classes enabled, how many faults a trace may carry, and how many of one class (partitions and
    // deadlines allow one more).
    // Each scope: the player's route, how many companions follow, slots a custodian offers, whether B declares the
    // role, the fault classes enabled, how many faults a trace may carry, how many of one class (partitions and
    // deadlines allow one more), and the scope's state cap (measured at about 100 MB of managed memory per million).
    // The small scopes run with the ordinary suite; the large ones (over about 100,000 states) are the model check,
    // run by the build lane and by any change to the protocol:
    //   dotnet test tests/Puck.World.Protocol.Tests/Puck.World.Protocol.Tests.csproj -c Release --no-build --explicit on --filter-trait Category=ModelCheck
    public static TheoryData<string, int, int, bool, Fault, int, int, int> SmallScopes => new() {
        { "A", 1, 3, true, Fault.Loss | Fault.Dup | Fault.Reconnect, 2, 2, 1_500_000 },
        { "A", 1, 3, true, Fault.Loss | Fault.Deadline | Fault.Reconnect, 3, 2, 1_500_000 },
        { "A", 1, 3, true, Fault.Partition | Fault.Disconnect | Fault.Reconnect, 2, 1, 1_500_000 },
        { "A", 1, 3, true, Fault.Install | Fault.Recall | Fault.Disconnect, 3, 2, 1_500_000 },
        { "A", 1, 3, true, Fault.Deadline | Fault.Forge | Fault.Disconnect, 2, 1, 1_500_000 },
        { "A", 1, 3, true, Fault.Write | Fault.Census | Fault.Disconnect | Fault.Recall, 3, 1, 1_500_000 },
        { "A", 1, 3, true, Fault.All, 1, 1, 1_500_000 },
        { "A", 2, 3, true, Fault.Crash | Fault.Log, 2, 1, 1_500_000 },
        { "A", 2, 2, true, Fault.Loss | Fault.Disconnect | Fault.Admission, 2, 1, 1_500_000 },
        { "AB", 1, 3, true, Fault.Disconnect | Fault.Partition, 1, 1, 1_500_000 },
        { "AH", 1, 3, true, Fault.Loss, 1, 1, 1_500_000 },
        { "AB", 2, 3, true, Fault.None, 0, 1, 1_500_000 },
        { "ABH", 1, 3, true, Fault.None, 0, 1, 1_500_000 },
    };
    public static TheoryData<string, int, int, bool, Fault, int, int, int> LargeScopes => new() {
        { "A", 1, 3, true, Fault.Crash | Fault.Deadline | Fault.Disconnect, 3, 2, 1_500_000 },
        { "A", 1, 3, true, Fault.Log | Fault.Crash | Fault.Disconnect, 3, 2, 1_500_000 },
        { "A", 2, 3, true, Fault.Loss | Fault.Crash | Fault.Admission | Fault.Install, 4, 2, 4_000_000 },
        { "A", 2, 3, true, Fault.Admission | Fault.Disconnect, 2, 2, 1_500_000 },
        { "A", 2, 3, true, Fault.Install | Fault.Recall | Fault.Disconnect, 2, 1, 1_500_000 },
        { "AB", 1, 3, true, Fault.Loss | Fault.Crash, 2, 1, 1_500_000 },
        { "AB", 1, 3, true, Fault.Log | Fault.Crash, 1, 1, 1_500_000 },
        { "AB", 1, 3, true, Fault.Forge | Fault.Disconnect | Fault.Recall, 2, 1, 1_500_000 },
        { "AB", 1, 3, true, Fault.Deadline | Fault.Reconnect | Fault.Loss, 1, 1, 1_500_000 },
        { "AB", 1, 3, false, Fault.Disconnect | Fault.Loss, 1, 1, 1_500_000 },
        { "AB", 2, 3, true, Fault.Admission | Fault.Install, 1, 1, 4_000_000 },
    };

    [Theory]
    [MemberData(nameof(SmallScopes))]
    public void EveryReachableStateHoldsTheInvariantsAndConverges(string route, int companions, int custodianSlots, bool bAccepts, Fault allowed, int faults, int limit, int cap) => ExploreScope(route: route, companions: companions, custodianSlots: custodianSlots, bAccepts: bAccepts, allowed: allowed, faults: faults, limit: limit, cap: cap);

    [Theory(Explicit = true)]
    [Trait("Category", "ModelCheck")]
    [MemberData(nameof(LargeScopes))]
    public void EveryReachableStateHoldsTheInvariantsAndConvergesAtScale(string route, int companions, int custodianSlots, bool bAccepts, Fault allowed, int faults, int limit, int cap) => ExploreScope(route: route, companions: companions, custodianSlots: custodianSlots, bAccepts: bAccepts, allowed: allowed, faults: faults, limit: limit, cap: cap);

    private void ExploreScope(string route, int companions, int custodianSlots, bool bAccepts, Fault allowed, int faults, int limit, int cap) {
        var result = Explore(bAccepts: bAccepts, route: Route(route), companions: companions, custodianSlots: custodianSlots, faults: faults, allowed: allowed, limit: limit, cap: cap);

        output.WriteLine(message: $"route={route} companions={companions} slots={custodianSlots} B={bAccepts} faults={allowed}x{faults} limit={limit}: {result}");
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
        s = Drive(s: s, "shed-mint A:2");
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
        // The commit is held while A asks home again; the arrival is appended on the answer, and that append is uncertain.
        s = Drive(s: s, "deliver H>A:Commit");
        s = Drive(s: s, "deliver A>H:HopQuery");
        s = Drive(s: s, "deliver H>A:HopQueryReply");
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
        s = Drive(s: s, "forge A>H:2 under Aforged2");
        s = Drive(s: s, "deliver A>H:Reserve:Aforged2");
        Assert.Contains(collection: s.Network, filter: static m => (m.Kind == MsgKind.ReserveRefused) && (m.Arg == "Duplicate"));
        s = Converge(s: s);
        Assert.Single(collection: s.Nodes[1].Records.OfType<Dep>());
    }

    // ---- the round-3 review's counterexamples, each reproduced through the model's own successors ----------------

    [Fact]
    public void ACancelledAuthorizationDoesNotVerifyAForgedHop() {
        var s = Start(route: "AB");

        s = Drive(s: s, "cross H>A");
        s = Drain(s: s);
        s = Drive(s: s, "cross A>B");
        s = Drive(s: s, "deliver A>H:HopRequest");
        Assert.Contains(collection: s.Nodes[0].Records, filter: static r => r is Hop { Key: "A1" });
        s = Drive(s: s, "crash A");
        s = Drive(s: s, "abandon A:A1");
        s = Drain(s: s);
        Assert.Equal(expected: (CustodyKind.Away, Node.A, 1), actual: Title(s: s, Ids.C1));
        s = Drive(s: s, "disconnect at A");
        s = Converge(s: s);
        Assert.Equal(expected: (CustodyKind.Home, Node.H, 2), actual: Title(s: s, Ids.C1));
        // The former custodian presents its cancelled key and a ticket for the current epoch at B.
        s = Drive(s: s, "forge A>B:2 under A1");
        s = Drain(s: s);
        Assert.False(condition: Fold(s: s, Node.B).Has(id: Ids.C1));
        Assert.Contains(collection: s.Trace, filter: static t => t.Contains("B>A:ReserveRefused", StringComparison.Ordinal));
        _ = Converge(s: s);
    }

    [Fact]
    public void ASettlementNoticeThatOvertakesItsAuthorizationIsRemembered() {
        var s = Start(route: "AB");

        s = Drive(s: s, "cross H>A");
        s = Drain(s: s);
        s = Drive(s: s, "cross A>B");
        Assert.Contains(collection: s.Network, filter: static m => m.Kind == MsgKind.HopRequest);
        s = Drive(s: s, "crash A");
        s = Drive(s: s, "abandon A:A1");
        s = Drive(s: s, "deliver A>H:HopSettled");
        s = Drive(s: s, "deliver H>A:HopSettledAck");
        Assert.Contains(collection: s.Nodes[1].Records, filter: static r => r is HopAcked);
        s = Drive(s: s, "deliver A>H:HopRequest");
        Assert.NotEqual(expected: CustodyKind.Pending, actual: Title(s: s, Ids.C1).Kind);
        s = Converge(s: s);
        Assert.True(condition: Fold(s: s, Node.A).Follows(id: Ids.C1));
    }

    [Fact]
    public void ShedAttemptsNeverReuseAKey() {
        var s = Start(route: "AB");

        s.Budget.Limit = 2;
        s = Drive(s: s, "cross H>A");
        s = Drain(s: s);
        s = Drive(s: s, "partition H-A");
        s = Drive(s: s, "disconnect at A");
        s = Drive(s: s, "sweep A:2");
        s = Drive(s: s, "strand A:2");
        s = Drive(s: s, "elapse A:2");
        s = Drive(s: s, "shed-mint A:2");
        s = Drive(s: s, "shed A:2");
        s = Drive(s: s, "heal H-A");
        // The sweep's own lease at home makes the shed's first reservation a duplicate until that lease is gone.
        s = Drain(s: s);
        s = Drive(s: s, "crash H");
        s = Drive(s: s, "retry reserve A:A2");
        s = DrainUntil(s: s, static m => (m.Kind == MsgKind.Reserved) && (m.From == Node.H) && (m.Lane == 1));
        // The first attempt's lease expires; the second attempt's lease expires too; the third lands.
        s = Drive(s: s, "expire H:");
        s = DrainUntil(s: s, static m => (m.Kind == MsgKind.Reserved) && (m.From == Node.H) && (m.Key != "A2"));
        s = Drive(s: s, "expire H:");
        s = Drain(s: s);

        var attempts = s.Nodes[1].Records.OfType<Minted>().Where(static m => m.Image is not null).Select(static m => m.Key).ToArray();

        Assert.Equal(expected: attempts.Length, actual: attempts.Distinct().Count());
        Assert.True(condition: attempts.Length >= 2);
        s = Converge(s: s);
        Assert.True(condition: Fold(s: s, Node.H).Has(id: Ids.C1));
    }

    [Fact]
    public void HomeHoldsTheSlotOfItsOwnDepartureInDoubt() {
        var s = Start(route: "AB");

        s = Drive(s: s, "cross H>A");
        s = DrainUntil(s: s, static m => m.Kind == MsgKind.Commit);
        Assert.Single(collection: Fold(s: s, Node.H).InDoubt);
        // An answer about home's own hop reaches home while its departure is still in doubt.
        CustodyModel.Deliver(s: s, msg: new Msg(From: Node.A, To: Node.H, Kind: MsgKind.CustodyReply, Key: "H1", Members: [], Arg: $"{Ids.C1},Dead", Dup: false));
        CustodyModel.CheckInvariants(s: s);
        s = Drive(s: s, "census");
        Assert.NotEqual(expected: 1, actual: Fold(s: s, Node.H).Bodies.Values.Single(static b => b.Id >= Ids.BotBase).Slot);
        s = Drive(s: s, "expire A:");
        s = Converge(s: s);
        Assert.Equal(expected: 1, actual: Fold(s: s, Node.H).Body(id: Ids.C1)!.Slot);
    }

    [Fact]
    public void TwoReleasedCompanionsRetireInSeparateSteps() {
        var s = Start(route: "AB", companions: 2);

        s.Budget.Limit = 2;
        s = Drive(s: s, "cross H>A");
        s = Drain(s: s);
        s = Drive(s: s, "install 2");
        s = Drive(s: s, "install 3");
        s = Drive(s: s, "cross A>B");
        s = Drive(s: s, "deliver A>H:HopRequest");
        s = Drive(s: s, "deliver H>A:HopAnswer");
        // Home refused both; the custodian retires one copy per step, never two records in one append.
        Assert.Equal(expected: 0, actual: s.Nodes[1].Records.Count(static r => r is Retire));
        s = Drive(s: s, "retire A:2");
        Assert.Equal(expected: 1, actual: s.Nodes[1].Records.Count(static r => r is Retire));
        s = Drive(s: s, "retire A:3");
        s = Converge(s: s);
        Assert.Equal(expected: CustodyKind.Released, actual: Title(s: s, Ids.C1).Kind);
        Assert.Equal(expected: Node.B, actual: s.PlayerAt);
    }

    [Fact]
    public void AnUncertainHopRecordIsNotServedBeforeRecovery() {
        var s = Start(route: "AB");

        s = Drive(s: s, "cross H>A");
        s = Drain(s: s);
        s = Drive(s: s, "cross A>B");
        s = Drive(s: s, "append UncertainLanded H");
        s = Drive(s: s, "deliver A>H:HopRequest");
        Assert.True(condition: s.Nodes[0].Latched);
        Assert.Contains(collection: s.Nodes[0].Records, filter: static r => r is Hop { Key: "A1" });
        Assert.Contains(collection: s.Network, filter: static m => (m.Kind == MsgKind.HopAnswer) && m.Arg.Contains("Unavailable", StringComparison.Ordinal));

        var probe = s.Clone();

        CustodyModel.Deliver(s: probe, msg: new Msg(From: Node.B, To: Node.H, Kind: MsgKind.HopQuery, Key: "A1", Members: [], Arg: Ids.C1.ToString(), Dup: false));
        Assert.Contains(collection: probe.Network, filter: static m => (m.Kind == MsgKind.HopQueryReply) && m.Arg.EndsWith("=-1", StringComparison.Ordinal));
        Assert.Equal(expected: CustodyKind.Away, actual: Title(s: s, Ids.C1).Kind);
        s = Converge(s: s);
        Assert.True(condition: Fold(s: s, Node.B).Follows(id: Ids.C1));
    }

    // ---- the round-4 review's counterexamples ---------------------------------------------------------------------

    [Fact]
    public void ADelayedVerificationAnswerCannotAuthorizeARevokedHop() {
        var s = Start(route: "AB");

        s = Drive(s: s, "cross H>A");
        s = Drain(s: s);
        s = Drive(s: s, "cross A>B");
        s = DrainUntil(s: s, static m => m.Kind == MsgKind.HopQueryReply);
        Assert.Contains(collection: s.Network, filter: static m => (m.Kind == MsgKind.HopQueryReply) && m.Arg.EndsWith("=2", StringComparison.Ordinal));
        s = Drive(s: s, "disconnect at A");
        s = Drive(s: s, "reconnect");
        // While the hop is pending home cannot recall: its destination may hold a verified answer.
        Assert.DoesNotContain(collection: CustodyModel.Successors(s: s, faultBudget: int.MaxValue).Select(static p => p.Name), filter: static n => n.StartsWith("recall", StringComparison.Ordinal));
        s = Drive(s: s, "deliver H>B:HopQueryReply");
        s = Drain(s: s);
        // B leased on the delayed answer, but the player had left A, so nothing departs; the lease dies, home resolves
        // the hop against B's tombstone, and the separated companion returns. No copy was ever committed at B.
        Assert.Equal(expected: CustodyKind.Pending, actual: Title(s: s, Ids.C1).Kind);
        s = Drive(s: s, "expire B:");
        s = Converge(s: s);
        Assert.Equal(expected: (CustodyKind.Home, Node.H, 2), actual: Title(s: s, Ids.C1));
        Assert.DoesNotContain(collection: s.Nodes[2].Records.OfType<Arr>().SelectMany(static a => a.Members), filter: static m => m.Id == Ids.C1);
        Assert.Contains(collection: s.Nodes[2].Records, filter: static r => r is Dead { Key: "A1" });
    }

    [Fact]
    public void ACommitLandsOnlyUnderTheGrantAsItStandsAtTheCommit() {
        var s = Start(route: "AB");

        s = Drive(s: s, "cross H>A");
        s = Drain(s: s);
        s = Drive(s: s, "cross A>B");
        s = DrainUntil(s: s, static m => m.Kind == MsgKind.Commit);
        s = Drive(s: s, "deliver A>B:Commit");
        // The commit is held while B asks home again; a release recorded meanwhile leaves the companion unlanded.
        Assert.Single(collection: s.Nodes[2].HeldCommits);
        s = Drive(s: s, "install 2");
        s = Drain(s: s);
        Assert.DoesNotContain(collection: s.Nodes[2].Records.OfType<Arr>().SelectMany(static a => a.Members), filter: static m => m.Id == Ids.C1);
        Assert.Equal(expected: Node.B, actual: s.PlayerAt);
        s = Converge(s: s);
        Assert.Equal(expected: CustodyKind.Released, actual: Title(s: s, Ids.C1).Kind);
    }

    [Fact]
    public void InstallDuringAPartialLandingRetiresTheUnlandedCompanion() {
        var s = Start(route: "AB", companions: 2);

        s = Drive(s: s, "cross H>A");
        s = DrainUntil(s: s, static m => (m.Kind == MsgKind.Reserved) && (m.From == Node.A));
        s = Drive(s: s, "refuse at commit A:H1:2");
        s = DrainUntil(s: s, static m => m.Kind == MsgKind.Committed);
        s = Drive(s: s, "install 2");
        s = Drain(s: s);
        Assert.Contains(collection: s.Nodes[0].Records, filter: static r => r is Settle { Retired: [Ids.C1] });
        Assert.False(condition: Fold(s: s, Node.H).Has(id: Ids.C1));
        Assert.True(condition: Fold(s: s, Node.A).Follows(id: Ids.C2));
        _ = Converge(s: s);
    }

    [Fact]
    public void CanonicalTextKeepsTheOrderWithinALane() {
        var a = Start(route: "AB");
        var b = a.Clone();
        var first = new Msg(From: Node.A, To: Node.H, Kind: MsgKind.Reserve, Key: "A1", Members: [], Arg: "", Dup: false);
        var second = new Msg(From: Node.A, To: Node.H, Kind: MsgKind.Reserve, Key: "A2", Members: [], Arg: "", Dup: false);

        a.Network.AddRange(collection: [first, second]);
        b.Network.AddRange(collection: [second, first]);
        Assert.NotEqual(expected: a.Canon(), actual: b.Canon());
        Assert.Equal(expected: "A1", actual: CustodyModel.LaneHeads(s: a).Single().Key);
        Assert.Equal(expected: "A2", actual: CustodyModel.LaneHeads(s: b).Single().Key);
    }

    [Fact]
    public void AStatusQueryMayPassADelayedCommitAfterAReconnect() {
        var s = Start(route: "AB");

        s = Drive(s: s, "cross H>A");
        s = DrainUntil(s: s, static m => m.Kind == MsgKind.Commit);
        s = Drive(s: s, "expire A:");
        s = Drive(s: s, "reconnect H>A");
        s = Drive(s: s, "retry status H:H1");
        Assert.Contains(collection: CustodyModel.LaneHeads(s: s), filter: static m => m.Kind == MsgKind.Status);
        s = Drive(s: s, "deliver H>A:Status");
        s = Drive(s: s, "deliver A>H:StatusReply:H1::Missing");
        Assert.True(condition: Fold(s: s, Node.H).Has(id: Ids.C1) && Fold(s: s, Node.H).Has(id: Ids.Player));
        s = Drive(s: s, "deliver H>A:Commit");
        Assert.Contains(collection: s.Network, filter: static m => m.Kind == MsgKind.Missing);
        s = Converge(s: s);
        Assert.Equal(expected: (CustodyKind.Home, Node.H, 0), actual: Title(s: s, Ids.C1));
    }

    [Fact]
    public void ALaterHopDoesNotContradictAnEarlierRecall() {
        var s = Start(route: "AB");

        s = Drive(s: s, "cross H>A");
        s = Drain(s: s);
        s = Drive(s: s, "disconnect at A");
        s = Drive(s: s, "reconnect");
        s = Drive(s: s, "recall 2");
        Assert.Equal(expected: 2, actual: Title(s: s, Ids.C1).Epoch);
        s = Drive(s: s, "cross H>B");
        s = Drive(s: s, "authorize H2");
        Assert.Contains(collection: s.Nodes[0].Records, filter: static r => r is Hop { Key: "H2" });
        s = Converge(s: s);
        Assert.Equal(expected: (CustodyKind.Away, Node.B, 3), actual: Title(s: s, Ids.C1));
    }

    [Fact]
    public void ABindingNamesTheFollowedGeneration() {
        var s = Start(route: "AA");

        s = Drive(s: s, "cross H>A");
        s = Drain(s: s);
        Assert.True(condition: Fold(s: s, Node.A).Follows(id: Ids.C1));
        s = Drive(s: s, "disconnect at A");
        s = Drive(s: s, "reconnect");
        // The player lands at A again at a new generation of the same slot; the old binding does not follow it.
        s = Drive(s: s, "cross H>A");
        s = Drain(s: s);
        Assert.Equal(expected: Node.A, actual: s.PlayerAt);
        Assert.False(condition: Fold(s: s, Node.A).Follows(id: Ids.C1));
        s = Converge(s: s);
        Assert.True(condition: Fold(s: s, Node.H).Has(id: Ids.C1));
    }

    [Fact]
    public void AFullCustodianRefusesTheSecondCompanion() {
        var s = Start(route: "AB", companions: 2);

        s.CustodianSlots = 2;
        s = Drive(s: s, "cross H>A");
        s = Drain(s: s);
        Assert.Contains(collection: s.Trace, filter: static t => t.Contains("A>H:Reserved", StringComparison.Ordinal) && t.Contains("3:Full", StringComparison.Ordinal));
        Assert.True(condition: Fold(s: s, Node.A).Follows(id: Ids.C1));
        Assert.True(condition: Fold(s: s, Node.H).Has(id: Ids.C2));
        Assert.Equal(expected: (CustodyKind.Home, Node.H, 0), actual: Title(s: s, Ids.C2));
        _ = Converge(s: s);
    }

    [Fact]
    public void ShedKeysComeFromTheCommonAllocator() {
        var s = Start(route: "AB");

        s = Drive(s: s, "cross H>A");
        s = Drain(s: s);
        s = Drive(s: s, "partition H-A");
        s = Drive(s: s, "disconnect at A");
        s = Drive(s: s, "sweep A:2");
        s = Drive(s: s, "strand A:2");
        s = Drive(s: s, "elapse A:2");
        s = Drive(s: s, "shed-mint A:2");
        Assert.Contains(collection: s.Nodes[1].Records, filter: static r => r is Minted { Shed: true, Key: "A2" });
        s = Drive(s: s, "shed A:2");
        Assert.Contains(collection: s.Nodes[1].Records, filter: static r => r is Dep { Shed: true, Key: "A2" });
        s = Drive(s: s, "heal H-A");
        s = Converge(s: s);
        Assert.True(condition: Fold(s: s, Node.H).Has(id: Ids.C1));
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
                CustodyModel.CheckStep(before: s, after: next, name: name);
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
            var msg = CustodyModel.LaneHeads(s: s).OrderBy(static m => m.Canon, StringComparer.Ordinal).FirstOrDefault();

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

    private readonly record struct Exploration(int States, int Converged, int MaxDepth, long Milliseconds, long PeakMegabytes);

    // Visited states are kept as 128-bit digests of their canonical text (two independent 64-bit folds), not the text.
    private static (ulong, ulong) Digest(string canon) {
        var a = 14695981039346656037UL;
        var b = 0x9E3779B97F4A7C15UL;

        foreach (var c in canon) {
            a = ((a ^ c) * 1099511628211UL);
            b = (System.Numerics.BitOperations.RotateLeft(b ^ c, 27) * 0xBF58476D1CE4E5B9UL) + 0x94D049BB133111EBUL;
        }

        return (a, b);
    }

    // Depth-first over the fault budget, so the frontier stays small while the visited set carries the breadth.
    private static Exploration Explore(bool bAccepts, Node[] route, int companions, int faults, Fault allowed, int limit = 1, int custodianSlots = CustodyModel.Capacity - 1, int cap = 1_500_000) {
        var start = new ModelState(aAccepts: true, bAccepts: bAccepts, route: route, companions: companions) { CustodianSlots = custodianSlots };

        start.Budget.Allowed = allowed;
        start.Budget.Limit = limit;

        var visited = new HashSet<(ulong, ulong)> { Digest(canon: start.Canon()) };
        var stack = new Stack<(ModelState State, int Depth)>();
        var converged = 0;
        var maxDepth = 0;
        var peak = GC.GetTotalMemory(forceFullCollection: false);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        stack.Push(item: (start, 0));
        while (stack.Count > 0) {
            var (state, depth) = stack.Pop();

            maxDepth = Math.Max(maxDepth, depth);
            try {
                CustodyModel.CheckInvariants(s: state);
                // Every state reaches a quiescent one by deliveries alone, and the search explores those, so
                // convergence is checked from each quiescent state.
                if (state.Network.Count == 0) {
                    _ = CustodyModel.Converge(s: state);
                    converged++;
                }
                foreach (var (name, next) in CustodyModel.Successors(s: state, faultBudget: faults)) {
                    CustodyModel.CheckStep(before: state, after: next, name: name);
                    if (!visited.Add(item: Digest(canon: next.Canon()))) {
                        continue;
                    }
                    next.Trace.Add(item: name);
                    stack.Push(item: (next, depth + 1));
                }
            } catch (ModelViolation violation) {
                throw new Xunit.Sdk.XunitException($"{violation.Message}\nscope: route={string.Join("", route)} companions={companions} slots={custodianSlots} B={bAccepts} faults={allowed}x{faults} limit={limit}\ntrace: {string.Join(" -> ", state.Trace)}");
            }
            if ((visited.Count & 0xFFFF) == 0) {
                peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: false));
            }
            if (visited.Count > cap) {
                throw new Xunit.Sdk.XunitException($"the scope route={string.Join("", route)} companions={companions} faults={allowed}x{faults} exceeds {cap} states; tighten its bounds");
            }
        }

        return new Exploration(States: visited.Count, Converged: converged, MaxDepth: maxDepth, Milliseconds: watch.ElapsedMilliseconds, PeakMegabytes: Math.Max(peak, GC.GetTotalMemory(forceFullCollection: false)) >> 20);
    }
}
