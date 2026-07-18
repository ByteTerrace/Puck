namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// Tier-C stage: the multiplayer link cable under a mid-exchange churn — the credit-preserving suspend/resume-token
/// proof <see cref="AgbLinkSession.Suspend"/> exists for, mirroring the Humble <c>link-churn</c> shape. A parent and a
/// child boot the same <see cref="MicroRoms"/> link-parent/link-child protocol <see cref="LinkReplayStage"/> uses
/// (<see cref="MicroRoms.LinkRounds"/> SIO multiplayer rounds) and are advanced together through an
/// <see cref="AgbLinkSession"/> on a fixed schedule of small master-cycle budgets. Each round's completion is visible
/// without touching either console (the round record both ROMs already write to IWRAM is non-zero once written, and
/// SIOCNT's start/busy bit — bit 7 — clears the instant a round lands and stays clear until the next round's SIOCNT
/// write), so a transfer-idle, mid-exchange boundary is found by peeking both, never by modifying the protocol.
/// <para>
/// Two proofs, both against an uninterrupted reference run. (a) Determinism: a second fresh run on the identical
/// budget schedule reproduces the reference's protocol verdicts and final snapshots — the exchange and its
/// furthest-behind interleave add no nondeterminism. (b) Churn, doubled: at the first two transfer-idle budget
/// boundaries mid-exchange, the session is <see cref="AgbLinkSession.Suspend">suspended</see> for its resume token,
/// both consoles are snapshotted, restored into FRESH machines, and reconnected WITH the token — twice in the same
/// run — and the remaining budgets still reproduce the reference's verdicts and final snapshots. The token is what
/// makes this exact: the plain constructor re-anchors targets at the current instant and discards each console's
/// instruction-overshoot credit, running extra cycles and diverging by construction.
/// </para>
/// </summary>
internal sealed class LinkChurnStage : IPostStage {
    private const long BudgetStep = 64L;
    private const uint ExpectedParentControl = 0x6003u; // multiplayer | 115200 bps | IRQ-enable, start clear, id 0
    private const uint ExpectedChildControl = 0x6013u;  // as the parent, with daisy-chain id 1 in bits 4-5
    private const uint SioCntAddress = 0x04000128u;
    private const uint SioCntStartMask = 0x0080u;
    private const int StepCount = 700;

    /// <inheritdoc/>
    public string Name =>
        "link-churn";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);

        // The reference (unchurned) run also probes every budget boundary, so both churn points are chosen
        // deterministically from its own transfer-idle windows.
        var reference = RunScenario(bios: context.BiosImage, churnAtSteps: []);

        if (Verify(result: reference) is { } failure) {
            return PostStageOutcome.Fail(detail: failure);
        }

        var (firstChurn, secondChurn) = PickChurnSteps(probes: reference.Probes);

        if ((firstChurn < 0) || (secondChurn < 0)) {
            return PostStageOutcome.Fail(detail: "fewer than two transfer-idle budget boundaries appeared mid-exchange; the round schedule is wrong");
        }

        // (a) Determinism: a second fresh, uninterrupted run reproduces the protocol verdicts and final snapshots.
        var replay = RunScenario(bios: context.BiosImage, churnAtSteps: []);

        if (Difference(expected: reference, actual: replay, leg: "replay") is { } replayFailure) {
            return PostStageOutcome.Fail(detail: replayFailure);
        }

        // (b) Churn, twice: suspend/snapshot/restore/reconnect at both idle boundaries, continue, demand the
        // identical tail.
        var churned = RunScenario(bios: context.BiosImage, churnAtSteps: [firstChurn, secondChurn]);

        if (Difference(expected: reference, actual: churned, leg: "churn") is { } churnFailure) {
            return PostStageOutcome.Fail(detail: churnFailure);
        }

        // (c) Reordered/substituted resume: a token bound to (parent, child) applied to (child, parent) must be
        // rejected and must leave both consoles unlinked (M-03).
        if (VerifyReorderedResumeRejected(bios: context.BiosImage) is { } reorderFailure) {
            return PostStageOutcome.Fail(detail: reorderFailure);
        }

        // (d) Invalid resume tokens — null (the "default"/absent token) and a credit-count mismatch — are rejected
        // with a clean ArgumentException family, never an NRE, and also leave both consoles unlinked (M-03).
        if (VerifyInvalidResumeTokenRejected(bios: context.BiosImage) is { } tokenFailure) {
            return PostStageOutcome.Fail(detail: tokenFailure);
        }

        // (e) Mid-transfer suspend: Suspend() at a busy (non-transfer-idle) budget boundary is rejected with
        // InvalidOperationException, and the session is left fully live — continuing it to completion reproduces
        // the reference exactly (M-04).
        if (VerifyMidTransferSuspendRejected(bios: context.BiosImage, reference: reference) is { } suspendFailure) {
            return PostStageOutcome.Fail(detail: suspendFailure);
        }

        return PostStageOutcome.Pass(
            detail: $"{MicroRoms.LinkRounds} multiplayer rounds (parent 0 / child 1), severed transfer-idle at budget steps {firstChurn} and {secondChurn}, replay-identical and churn-identical across both cycles ({reference.ParentState.Size}+{reference.ChildState.Size} state bytes); reordered/substituted resume rejected+unlinked, null/mismatched resume token rejected+unlinked, mid-transfer Suspend rejected with the session left live and driven to an identical completion"
        );
    }

    // One complete scenario on the fixed budget schedule. At each step index in churnAtSteps the live session is
    // suspended (which the reference confirmed transfer-idle), both consoles snapshotted and restored into fresh
    // machines, and the cable reconnected with the resume token before the remaining budgets run.
    private static LinkChurnScenarioResult RunScenario(ReadOnlyMemory<byte> bios, int[] churnAtSteps) {
        var parentRom = MicroRoms.GenerateBytes(kind: "link-parent");
        var childRom = MicroRoms.GenerateBytes(kind: "link-child");

        var parent = CreateConsole(bios: bios, rom: parentRom);
        var child = CreateConsole(bios: bios, rom: childRom);
        var probes = new List<BoundaryProbe>(capacity: StepCount);
        var session = new AgbLinkSession(parent, child);

        try {
            for (var step = 0; (step < StepCount); ++step) {
                probes.Add(item: Probe(parent: parent, child: child));

                if (Array.IndexOf(array: churnAtSteps, value: step) >= 0) {
                    if (!IsTransferIdle(parent: parent, child: child)) {
                        throw new InvalidOperationException(message: $"the churn boundary at budget step {step} is not transfer-idle on both consoles.");
                    }

                    var token = session.Suspend();
                    var parentState = parent.Machine.Snapshot();
                    var childState = child.Machine.Snapshot();
                    var freshParent = CreateConsole(bios: bios, rom: parentRom);
                    var freshChild = CreateConsole(bios: bios, rom: childRom);

                    freshParent.Machine.Restore(snapshot: parentState);
                    freshChild.Machine.Restore(snapshot: childState);

                    parent.Dispose();
                    child.Dispose();

                    parent = freshParent;
                    child = freshChild;

                    session = new AgbLinkSession(token, parent, child);
                }

                session.Run(cycles: BudgetStep);
            }

            return new LinkChurnScenarioResult(
                ParentVerdict: ReadVerdict(console: parent),
                ChildVerdict: ReadVerdict(console: child),
                ParentState: parent.Machine.Snapshot(),
                ChildState: child.Machine.Snapshot(),
                Probes: probes
            );
        } finally {
            session.Dispose();
            parent.Dispose();
            child.Dispose();
        }
    }
    private static AgbMachineInstance CreateConsole(ReadOnlyMemory<byte> bios, byte[] rom) {
        var console = AgbMachineFactory.Create(configuration: new AgbMachineConfiguration(bios: bios, rom: rom));

        console.Machine.DirectBoot();

        return console;
    }

    // A budget-boundary snapshot of the exchange's progress: whether both consoles are transfer-idle right now, and
    // how many of the parent's round records have landed in IWRAM (a round's low word is never zero once the parent
    // has written it, so a non-zero slot is proof the round completed — no protocol change needed to observe it).
    private static BoundaryProbe Probe(AgbMachineInstance parent, AgbMachineInstance child) =>
        new(Idle: IsTransferIdle(parent: parent, child: child), RecordedRounds: CountRecordedRounds(console: parent));
    private static bool IsTransferIdle(AgbMachineInstance parent, AgbMachineInstance child) =>
        (((DebugReadSioCnt(console: parent) & SioCntStartMask) == 0)
            && ((DebugReadSioCnt(console: child) & SioCntStartMask) == 0));
    private static int CountRecordedRounds(AgbMachineInstance console) {
        var bus = (AgbBus)console.Machine.Bus;
        var count = 0;

        while ((count < MicroRoms.LinkRounds) && (bus.DebugRead32(address: (MicroRoms.LinkRecordAddress + ((uint)count * 8u))) != 0u)) {
            ++count;
        }

        return count;
    }
    private static ushort DebugReadSioCnt(AgbMachineInstance console) =>
        ((AgbBus)console.Machine.Bus).DebugRead16(address: SioCntAddress);

    // Reads a side's IWRAM verdict through the side-effect-free debug peek (no clock movement, so read order can
    // never perturb the snapshots) — the same fields LinkReplayStage verifies.
    private static LinkSideVerdict ReadVerdict(AgbMachineInstance console) {
        var bus = (AgbBus)console.Machine.Bus;
        var rounds = new (uint Low, uint High)[MicroRoms.LinkRounds];

        for (var round = 0; (round < rounds.Length); ++round) {
            var recordAddress = (MicroRoms.LinkRecordAddress + ((uint)round * 8u));

            rounds[round] = (bus.DebugRead32(address: recordAddress), bus.DebugRead32(address: (recordAddress + 4u)));
        }

        return new LinkSideVerdict(
            IrqCount: bus.DebugRead32(address: MicroRoms.LinkIrqCountAddress),
            Marker: bus.DebugRead32(address: MicroRoms.LinkMarkerAddress),
            SerialControl: bus.DebugRead32(address: MicroRoms.LinkControlAddress),
            Rounds: rounds
        );
    }

    // The first two budget boundaries that are transfer-idle mid-exchange (at least one round recorded, not all of
    // them, at a STRICTLY increasing recorded-round count) — two genuine, distinct severable instants.
    private static (int First, int Second) PickChurnSteps(List<BoundaryProbe> probes) {
        var first = -1;
        var firstRecorded = 0;

        for (var step = 0; (step < probes.Count); ++step) {
            var probe = probes[index: step];

            if (!probe.Idle || (probe.RecordedRounds < 1) || (probe.RecordedRounds >= MicroRoms.LinkRounds)) {
                continue;
            }

            if (first < 0) {
                first = step;
                firstRecorded = probe.RecordedRounds;

                continue;
            }

            if (probe.RecordedRounds > firstRecorded) {
                return (first, step);
            }
        }

        return (first, -1);
    }

    // The first budget boundary that is NOT transfer-idle (SIOCNT's start/busy bit set on the parent, the child, or
    // both) — the M-04 probe's mid-transfer instant.
    private static int PickBusyStep(List<BoundaryProbe> probes) {
        for (var step = 0; (step < probes.Count); ++step) {
            if (!probes[index: step].Idle) {
                return step;
            }
        }

        return -1;
    }

    // M-03 probe (a): a resume token bound to (parent, child) applied to (child, parent) — a reordering that changes
    // every slot's expected identity, since the two ROMs differ — must be rejected by AgbMachineIdentity binding, and
    // rejection must leave both consoles fully unlinked: a fresh, non-resume session over the very same pair (in the
    // very same rejected order) must still connect cleanly afterward, proof nothing from the aborted attempt leaked.
    private static string? VerifyReorderedResumeRejected(ReadOnlyMemory<byte> bios) {
        var parentRom = MicroRoms.GenerateBytes(kind: "link-parent");
        var childRom = MicroRoms.GenerateBytes(kind: "link-child");
        var parent = CreateConsole(bios: bios, rom: parentRom);
        var child = CreateConsole(bios: bios, rom: childRom);

        try {
            var session = new AgbLinkSession(parent, child);

            session.Run(cycles: BudgetStep);

            if (!IsTransferIdle(parent: parent, child: child)) {
                return "the reordered-resume probe's suspend point is not transfer-idle; pick a different budget";
            }

            var token = session.Suspend();
            var parentState = parent.Machine.Snapshot();
            var childState = child.Machine.Snapshot();
            var freshParent = CreateConsole(bios: bios, rom: parentRom);
            var freshChild = CreateConsole(bios: bios, rom: childRom);

            freshParent.Machine.Restore(snapshot: parentState);
            freshChild.Machine.Restore(snapshot: childState);

            try {
                ArgumentException? caught = null;

                try {
                    using var rejected = new AgbLinkSession(token, freshChild, freshParent); // reordered
                } catch (ArgumentException ex) {
                    caught = ex;
                }

                if (caught is null) {
                    return "a resume with a reordered console pair (child, parent instead of parent, child) did not throw ArgumentException";
                }

                try {
                    using var proof = new AgbLinkSession(freshChild, freshParent);
                } catch (Exception ex) {
                    return $"a fresh session over the same consoles failed after the rejected reorder ({ex.GetType().Name}: {ex.Message}); the rejected resume must have left a console linked";
                }

                return null;
            } finally {
                freshParent.Dispose();
                freshChild.Dispose();
            }
        } finally {
            parent.Dispose();
            child.Dispose();
        }
    }

    // M-03 probe (b): a null ("default"/absent) resume token, and a token whose credit count does not match the
    // console count, are both rejected with a clean ArgumentException family (never an NRE), and both leave every
    // console unlinked.
    private static string? VerifyInvalidResumeTokenRejected(ReadOnlyMemory<byte> bios) {
        var parentRom = MicroRoms.GenerateBytes(kind: "link-parent");
        var childRom = MicroRoms.GenerateBytes(kind: "link-child");
        var parent = CreateConsole(bios: bios, rom: parentRom);
        var child = CreateConsole(bios: bios, rom: childRom);
        AgbMachineInstance? third = null;

        try {
            ArgumentNullException? nullCaught = null;

            try {
                using var rejected = new AgbLinkSession(resumeToken: null!, parent, child);
            } catch (ArgumentNullException ex) {
                nullCaught = ex;
            }

            if (nullCaught is null) {
                return "a resume with a null (default) token did not throw ArgumentNullException";
            }

            try {
                using var proof = new AgbLinkSession(parent, child);
            } catch (Exception ex) {
                return $"a fresh session over the same consoles failed after the null-token rejection ({ex.GetType().Name}: {ex.Message}); the rejected resume must have left a console linked";
            }

            var session = new AgbLinkSession(parent, child);

            session.Run(cycles: BudgetStep);

            if (!IsTransferIdle(parent: parent, child: child)) {
                return "the mismatched-token probe's suspend point is not transfer-idle; pick a different budget";
            }

            var token = session.Suspend();

            third = CreateConsole(bios: bios, rom: parentRom);

            ArgumentException? mismatchCaught = null;

            try {
                using var rejected = new AgbLinkSession(token, parent, child, third);
            } catch (ArgumentException ex) {
                mismatchCaught = ex;
            }

            if (mismatchCaught is null) {
                return "a resume with a credit-count/console-count mismatch did not throw ArgumentException";
            }

            try {
                using var proof = new AgbLinkSession(parent, child);
            } catch (Exception ex) {
                return $"a fresh session over the same consoles failed after the mismatched-token rejection ({ex.GetType().Name}: {ex.Message}); the rejected resume must have left a console linked";
            }

            return null;
        } finally {
            third?.Dispose();
            parent.Dispose();
            child.Dispose();
        }
    }

    // M-04 probe (c): Suspend() at a busy (non-transfer-idle) budget boundary — found from the reference run's own
    // probe schedule, so it is a genuine, reproducible mid-transfer instant, not a fabricated one — must throw
    // InvalidOperationException, and the session must be left fully live: driving the SAME session on to completion
    // reproduces the reference's protocol verdicts and final snapshots exactly.
    private static string? VerifyMidTransferSuspendRejected(ReadOnlyMemory<byte> bios, LinkChurnScenarioResult reference) {
        var busyStep = PickBusyStep(probes: reference.Probes);

        if (busyStep < 0) {
            return "no busy (non-transfer-idle) budget boundary appeared mid-exchange; the round schedule is wrong";
        }

        var parentRom = MicroRoms.GenerateBytes(kind: "link-parent");
        var childRom = MicroRoms.GenerateBytes(kind: "link-child");
        var parent = CreateConsole(bios: bios, rom: parentRom);
        var child = CreateConsole(bios: bios, rom: childRom);
        var session = new AgbLinkSession(parent, child);

        try {
            for (var step = 0; (step < busyStep); ++step) {
                session.Run(cycles: BudgetStep);
            }

            if (IsTransferIdle(parent: parent, child: child)) {
                return $"the mid-transfer probe step {busyStep} is transfer-idle on replay; the round schedule is not reproducible";
            }

            InvalidOperationException? caught = null;

            try {
                session.Suspend();
            } catch (InvalidOperationException ex) {
                caught = ex;
            }

            if (caught is null) {
                return $"Suspend() at busy budget step {busyStep} (SIOCNT start/busy bit set) did not throw InvalidOperationException";
            }

            // The rejected Suspend must have left the session live and untouched: keep driving THIS session (never a
            // fresh one) through the remaining budgets of the exact same schedule the reference used.
            for (var step = busyStep; (step < StepCount); ++step) {
                session.Run(cycles: BudgetStep);
            }

            var actual = new LinkChurnScenarioResult(
                ParentVerdict: ReadVerdict(console: parent),
                ChildVerdict: ReadVerdict(console: child),
                ParentState: parent.Machine.Snapshot(),
                ChildState: child.Machine.Snapshot(),
                Probes: []
            );

            return Difference(expected: reference, actual: actual, leg: "mid-transfer-suspend");
        } finally {
            session.Dispose();
            parent.Dispose();
            child.Dispose();
        }
    }

    // Judges the reference run: both sides completed every round with the right IRQ count and daisy-chain id, and
    // every round's recorded slots prove data actually crossed the cable.
    private static string? Verify(LinkChurnScenarioResult result) =>
        (VerifySide(verdict: result.ParentVerdict, side: "parent", expectedControl: ExpectedParentControl)
            ?? VerifySide(verdict: result.ChildVerdict, side: "child", expectedControl: ExpectedChildControl));
    private static string? VerifySide(LinkSideVerdict verdict, string side, uint expectedControl) {
        if (verdict.Marker != MicroRoms.LinkCompletionMarker) {
            return $"the {side} never completed its {MicroRoms.LinkRounds} rounds (marker 0x{verdict.Marker:X8})";
        }

        if (verdict.IrqCount != MicroRoms.LinkRounds) {
            return $"the {side} observed {verdict.IrqCount} serial IRQ requests; expected {MicroRoms.LinkRounds}";
        }

        if (verdict.SerialControl != expectedControl) {
            return $"the {side}'s final SIOCNT is 0x{verdict.SerialControl:X4}; expected 0x{expectedControl:X4} (id bits / busy)";
        }

        var childWord = MicroRoms.LinkChildSeedWord;

        for (var round = 0; (round < verdict.Rounds.Length); ++round) {
            var parentWord = (ushort)(MicroRoms.LinkParentSendBase + round);
            var expectedLow = (uint)(parentWord | (childWord << 16));

            if (verdict.Rounds[round].Low != expectedLow) {
                return $"the {side}'s round {round} SIOMULTI0/1 is 0x{verdict.Rounds[round].Low:X8}; expected 0x{expectedLow:X8}";
            }

            if (verdict.Rounds[round].High != 0xFFFFFFFFu) {
                return $"the {side}'s round {round} SIOMULTI2/3 is 0x{verdict.Rounds[round].High:X8}; expected 0xFFFFFFFF (absent players)";
            }

            childWord = (ushort)(parentWord ^ MicroRoms.LinkChildTransformMask);
        }

        return null;
    }

    // Compares a later run against the reference: both protocol verdicts and both final snapshots must match.
    // Snapshot equality also checks Identity (free rigor). Probes are the schedule's own instrument, not compared.
    private static string? Difference(LinkChurnScenarioResult expected, LinkChurnScenarioResult actual, string leg) {
        if (!VerdictsEqual(a: expected.ParentVerdict, b: actual.ParentVerdict)) {
            return $"the {leg} parent protocol verdict diverged from the reference";
        }

        if (!VerdictsEqual(a: expected.ChildVerdict, b: actual.ChildVerdict)) {
            return $"the {leg} child protocol verdict diverged from the reference";
        }

        if (!expected.ParentState.ContentEquals(other: actual.ParentState)) {
            return $"the {leg} parent final state diverged — {HashDivergenceProbe.DescribeDivergence(a: expected.ParentState, b: actual.ParentState)}";
        }

        if (!expected.ChildState.ContentEquals(other: actual.ChildState)) {
            return $"the {leg} child final state diverged — {HashDivergenceProbe.DescribeDivergence(a: expected.ChildState, b: actual.ChildState)}";
        }

        return null;
    }
    // Value equality for LinkSideVerdict: the record's auto-generated Equals would compare the Rounds array by
    // reference (each run reads a freshly allocated array), so this compares its elements explicitly.
    private static bool VerdictsEqual(LinkSideVerdict a, LinkSideVerdict b) =>
        ((a.IrqCount == b.IrqCount) && (a.Marker == b.Marker) && (a.SerialControl == b.SerialControl) && a.Rounds.AsSpan().SequenceEqual(other: b.Rounds));

    private readonly record struct BoundaryProbe(
        bool Idle,
        int RecordedRounds
    );
    private readonly record struct LinkChurnScenarioResult(
        LinkSideVerdict ParentVerdict,
        LinkSideVerdict ChildVerdict,
        AgbMachineSnapshot ParentState,
        AgbMachineSnapshot ChildState,
        List<BoundaryProbe> Probes
    );
    private readonly record struct LinkSideVerdict(
        uint IrqCount,
        uint Marker,
        uint SerialControl,
        (uint Low, uint High)[] Rounds
    );
}
