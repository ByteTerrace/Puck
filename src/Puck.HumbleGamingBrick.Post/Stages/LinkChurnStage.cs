using Puck.HumbleGamingBrick.Interfaces;
using ITimer = Puck.HumbleGamingBrick.Interfaces.ITimer;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-C stage: the serial link cable under a longer multi-round exchange AND a mid-exchange churn — the
/// replay-through-churn proof the M5 link story otherwise lacks. A DMG master and a Color slave boot the gapped
/// <see cref="SerialLinkRom.CreateChurn"/> protocol (sixteen transfers each way with a deliberate idle gap between them)
/// and are advanced together through a <see cref="SerialLinkSession"/> on a fixed schedule of small T-cycle budgets.
/// Every completed transfer on both ports is folded into a per-side FNV-1a traffic fingerprint (the same idiom
/// <see cref="LinkReplay"/> uses); the trace is anchored by the connect phase — both machines' raw DIV counter and SC
/// register at the instant the cable was joined. There is NO resync at connect (a cable does not touch DIV): the
/// canonical phase IS each profile's deterministic post-boot phase, so recording it pins the no-resync decision into the
/// gate.
/// <para>
/// Two proofs. (a) Determinism: two fresh runs on the identical budget schedule produce a bit-identical connect phase,
/// per-side traffic fingerprint, and final snapshots — the exchange and its pair-stepping interleave add no
/// nondeterminism. (b) Churn: at the first transfer-idle budget boundary mid-exchange (SC bit 7 clear on BOTH ports,
/// asserted), the session is <see cref="SerialLinkSession.Suspend">suspended</see> for its resume token, both machines
/// are snapshotted, restored into FRESH machines, and reconnected WITH the token; the remaining identical budgets then
/// produce a traffic tail and final snapshots bit-identical to the unchurned run. The token is what makes this exact: a
/// naive reconnect re-anchors targets at the current instant and discards each machine's instruction-overshoot credit,
/// running extra cycles and diverging by construction.
/// </para>
/// </summary>
internal sealed class LinkChurnStage : IPostStage {
    private const ulong BudgetStep = 256;
    private const ulong FnvOffsetBasis = 0xCBF29CE484222325ul;
    private const ulong FnvPrime = 0x100000001B3ul;
    private const byte IdleDelay = 64;
    private const byte MasterSendBase = 0x10;
    private const ushort SerialControlAddress = 0xFF02;
    private const byte SlaveSendBase = 0xA0;
    private const int StepCount = 512;
    private const byte TransferCount = 16;

    /// <inheritdoc/>
    public string Name =>
        "link-churn";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        // The reference (unchurned) run also probes every budget boundary, so the churn point is chosen deterministically
        // from its own transfer-idle windows.
        var reference = RunScenario(churnAtStep: -1);

        if (Judge(result: reference) is { } failure) {
            return PostStageOutcome.Fail(detail: failure);
        }

        var churnStep = PickChurnStep(probes: reference.Probes);

        if (churnStep < 0) {
            return PostStageOutcome.Fail(detail: "no transfer-idle budget boundary appeared mid-exchange; the idle gap or budget schedule is wrong");
        }

        // (a) Determinism: a second fresh run on the same schedule reproduces the connect phase, traffic, and snapshots.
        var replay = RunScenario(churnAtStep: -1);

        if (Difference(expected: reference, actual: replay, leg: "replay") is { } replayFailure) {
            return PostStageOutcome.Fail(detail: replayFailure);
        }

        // (b) Churn: suspend/snapshot/restore/reconnect at the idle boundary, continue, and demand the identical tail.
        var churned = RunScenario(churnAtStep: churnStep);

        if (Difference(expected: reference, actual: churned, leg: "churn") is { } churnFailure) {
            return PostStageOutcome.Fail(detail: churnFailure);
        }

        return PostStageOutcome.Pass(
            detail: $"{TransferCount} transfers each way (dmg master ↔ cgb slave), connect phase pinned, replay-identical and churn-identical (severed transfer-idle at budget step {churnStep}, {reference.MasterState.Size}+{reference.SlaveState.Size} state bytes)"
        );
    }

    // One complete scenario on the fixed budget schedule. With churnAtStep >= 0 the session is suspended at that boundary
    // (which the reference confirmed transfer-idle), both machines snapshotted and restored into fresh machines, and the
    // cable reconnected with the resume token before the remaining budgets run. The traffic tallies are host-side and
    // survive the machine swap by being re-attached to the fresh ports around the same accumulators.
    private static ChurnScenarioResult RunScenario(int churnAtStep) {
        var masterRom = SerialLinkRom.CreateChurn(internalClock: true, sendBase: MasterSendBase, transferCount: TransferCount, idleDelay: IdleDelay);
        var slaveRom = SerialLinkRom.CreateChurn(internalClock: false, sendBase: SlaveSendBase, transferCount: TransferCount, idleDelay: IdleDelay);

        var master = PostMachine.Build(model: ConsoleModel.Dmg, rom: masterRom);
        var slave = PostMachine.Build(model: ConsoleModel.Cgb, rom: slaveRom);
        var masterTally = new TrafficTally();
        var slaveTally = new TrafficTally();
        var probes = new List<BoundaryProbe>(capacity: StepCount);

        try {
            Observe(instance: master, tally: masterTally);
            Observe(instance: slave, tally: slaveTally);

            var session = new SerialLinkSession(first: master, second: slave);
            var connectPhase = ReadPhase(master: master, slave: slave);

            try {
                for (var step = 0; (step < StepCount); ++step) {
                    probes.Add(item: new BoundaryProbe(Idle: IsTransferIdle(master: master, slave: slave), Completed: masterTally.Completions));

                    if (step == churnAtStep) {
                        if (!IsTransferIdle(master: master, slave: slave)) {
                            throw new InvalidOperationException(message: $"the churn boundary at budget step {step} is not transfer-idle on both ports.");
                        }

                        var token = session.Suspend();
                        var masterState = master.Machine.Snapshot();
                        var slaveState = slave.Machine.Snapshot();
                        var freshMaster = PostMachine.Build(model: ConsoleModel.Dmg, rom: masterRom);
                        var freshSlave = PostMachine.Build(model: ConsoleModel.Cgb, rom: slaveRom);

                        freshMaster.Machine.Restore(snapshot: masterState);
                        freshSlave.Machine.Restore(snapshot: slaveState);

                        master.Dispose();
                        slave.Dispose();

                        master = freshMaster;
                        slave = freshSlave;

                        Observe(instance: master, tally: masterTally);
                        Observe(instance: slave, tally: slaveTally);

                        session = new SerialLinkSession(first: master, second: slave, resumeToken: token);
                    }

                    session.Run(tCycles: BudgetStep);
                }

                return new ChurnScenarioResult(
                    ConnectPhase: connectPhase,
                    MasterCompletion: ReadCompletion(instance: master),
                    MasterState: master.Machine.Snapshot(),
                    MasterTraffic: masterTally.ToTraffic(),
                    Probes: probes,
                    SlaveCompletion: ReadCompletion(instance: slave),
                    SlaveState: slave.Machine.Snapshot(),
                    SlaveTraffic: slaveTally.ToTraffic()
                );
            } finally {
                session.Dispose();
            }
        } finally {
            master.Dispose();
            slave.Dispose();
        }
    }

    // Wire a port's completed-transfer and internal-send observers to a host-side tally. Host wiring — never serialized,
    // so it survives a snapshot/restore only by being re-attached to the fresh port here.
    private static void Observe(MachineInstance instance, TrafficTally tally) {
        var port = instance.GetRequiredService<SerialComponent>();

        port.ByteTransmitted = tally.OnSend;
        port.TransferCompleted = tally.OnComplete;
    }

    // The cable touches DIV on neither machine, so "the phase at connect" is each machine's own post-boot DIV/serial
    // phase; recording both pins the no-resync decision into the gate.
    private static ConnectPhase ReadPhase(MachineInstance master, MachineInstance slave) =>
        new(
            MasterControl: master.GetRequiredService<ISystemBus>().ReadByte(address: SerialControlAddress),
            MasterDiv: master.GetRequiredService<ITimer>().DivCounter,
            SlaveControl: slave.GetRequiredService<ISystemBus>().ReadByte(address: SerialControlAddress),
            SlaveDiv: slave.GetRequiredService<ITimer>().DivCounter
        );
    private static bool IsTransferIdle(MachineInstance master, MachineInstance slave) =>
        (((master.GetRequiredService<ISystemBus>().ReadByte(address: SerialControlAddress) & 0x80) == 0)
            && ((slave.GetRequiredService<ISystemBus>().ReadByte(address: SerialControlAddress) & 0x80) == 0));
    private static LinkSideCompletion ReadCompletion(MachineInstance instance) {
        var bus = instance.GetRequiredService<ISystemBus>();

        return new LinkSideCompletion(
            CompletionMarker: bus.ReadByte(address: SerialLinkRom.CompletionMarkerAddress),
            InterruptCount: bus.ReadByte(address: SerialLinkRom.InterruptCountAddress)
        );
    }

    // The first budget boundary that is transfer-idle with at least one transfer done but not all — a genuine
    // mid-exchange severable instant.
    private static int PickChurnStep(List<BoundaryProbe> probes) {
        for (var step = 0; (step < probes.Count); ++step) {
            var probe = probes[index: step];

            if (probe.Idle && (probe.Completed >= 1) && (probe.Completed < TransferCount)) {
                return step;
            }
        }

        return -1;
    }

    // Judges the reference run: both sides completed every transfer with the right interrupt count, and each side's
    // traffic tally shows the full transfer count (a non-idle fingerprint that actually carried bytes across the cable).
    private static string? Judge(ChurnScenarioResult result) {
        if (CompletionFault(completion: result.MasterCompletion, side: "master") is { } masterFault) {
            return masterFault;
        }

        if (CompletionFault(completion: result.SlaveCompletion, side: "slave") is { } slaveFault) {
            return slaveFault;
        }

        if ((result.MasterTraffic.Completions != TransferCount) || (result.SlaveTraffic.Completions != TransferCount)) {
            return $"the traffic tally holds {result.MasterTraffic.Completions} master and {result.SlaveTraffic.Completions} slave completions; expected {TransferCount} each";
        }

        if (result.MasterTraffic.MasterSends != TransferCount) {
            return $"the master started {result.MasterTraffic.MasterSends} internal-clock transfers; expected {TransferCount}";
        }

        return null;
    }
    private static string? CompletionFault(LinkSideCompletion completion, string side) {
        if (completion.CompletionMarker != SerialLinkRom.CompletionMarker) {
            return $"the {side} never finished its {TransferCount} transfers (marker 0x{completion.CompletionMarker:X2})";
        }

        if (completion.InterruptCount != TransferCount) {
            return $"the {side} observed {completion.InterruptCount} serial interrupts; expected {TransferCount}";
        }

        return null;
    }

    // Compares a later run against the reference: connect phase, both traffic fingerprints, and both final snapshots must
    // match. Snapshot equality also checks Identity (free rigor). Probes are the schedule's own instrument, not compared.
    private static string? Difference(ChurnScenarioResult expected, ChurnScenarioResult actual, string leg) {
        if (expected.ConnectPhase != actual.ConnectPhase) {
            return $"the {leg} connect phase diverged (expected {expected.ConnectPhase}, got {actual.ConnectPhase})";
        }

        if (expected.MasterTraffic != actual.MasterTraffic) {
            return $"the {leg} master traffic diverged (expected {expected.MasterTraffic}, got {actual.MasterTraffic})";
        }

        if (expected.SlaveTraffic != actual.SlaveTraffic) {
            return $"the {leg} slave traffic diverged (expected {expected.SlaveTraffic}, got {actual.SlaveTraffic})";
        }

        if (!expected.MasterState.ContentEquals(other: actual.MasterState)) {
            return $"the {leg} master final state diverged — {HashDivergenceProbe.DescribeDivergence(a: expected.MasterState, b: actual.MasterState)}";
        }

        if (!expected.SlaveState.ContentEquals(other: actual.SlaveState)) {
            return $"the {leg} slave final state diverged — {HashDivergenceProbe.DescribeDivergence(a: expected.SlaveState, b: actual.SlaveState)}";
        }

        return null;
    }

    // A host-side, never-serialized tally of one port's serial traffic, folding the LinkReplay fingerprint idiom: it
    // counts the internal-clock sends and every completion and folds each completed byte into an FNV-1a stream hash.
    // Kept as a mutable class so the same accumulators survive being re-attached to a fresh port after a churn.
    private sealed class TrafficTally {
        public int Completions;
        public ulong Hash = FnvOffsetBasis;
        public int MasterSends;

        public void OnComplete(byte value) {
            ++Completions;
            Hash = ((Hash ^ value) * FnvPrime);
        }
        public void OnSend(byte value) =>
            ++MasterSends;
        public SideTraffic ToTraffic() =>
            new(Completions: Completions, Hash: Hash, MasterSends: MasterSends);
    }
    private readonly record struct BoundaryProbe(
        bool Idle,
        int Completed
    );
    private readonly record struct ChurnScenarioResult(
        ConnectPhase ConnectPhase,
        LinkSideCompletion MasterCompletion,
        MachineSnapshot MasterState,
        SideTraffic MasterTraffic,
        List<BoundaryProbe> Probes,
        LinkSideCompletion SlaveCompletion,
        MachineSnapshot SlaveState,
        SideTraffic SlaveTraffic
    );
    private readonly record struct ConnectPhase(
        byte MasterControl,
        ushort MasterDiv,
        byte SlaveControl,
        ushort SlaveDiv
    );
    private readonly record struct LinkSideCompletion(
        byte CompletionMarker,
        byte InterruptCount
    );
    private readonly record struct SideTraffic(
        int Completions,
        ulong Hash,
        int MasterSends
    );
}
