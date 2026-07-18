using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-C stage: the cross-machine infrared channel — the IR analogue of the serial-link battery, and the Mystery Gift
/// transport's foundation. Two Color machines boot <see cref="InfraredRom"/>'s TURN-BASED exchange program (M-02
/// hardware self-sensing makes simultaneous bidirectional exchange ambiguous — see the ROM's own remarks): one side
/// transmits its DISTINCT pattern over the CGB infrared port (RP, <c>0xFF56</c>) while the other receives, then the
/// roles swap, and are advanced together through an <see cref="IrLinkSession"/> on a fixed schedule of small T-cycle
/// budgets. The half-duplex level exchange has no hardware handshake: it works purely because the session's
/// furthest-behind interleave keeps both machines cycle-locked to within one instruction, so a transmitted bit is
/// always stable before its matching receive-phase sample.
/// <para>
/// Three proofs. Correctness: each side receives the OTHER side's full pattern back, bit-for-bit — the sent and received
/// transcripts match exactly on both sides, so real light crossed the medium both ways. Determinism: two fresh runs on the
/// identical schedule reproduce both received transcripts and both final snapshots. Churn: at a mid-exchange budget
/// boundary the session is <see cref="IrLinkSession.Suspend">suspended</see> for its resume token, both machines are
/// snapshotted, restored into FRESH machines, and reconnected WITH the token; the remaining budgets then produce
/// transcripts and final snapshots bit-identical to the unchurned run — proving the transceiver's whole state (RP register,
/// cart LED latch) serializes and the credit-preserving token continues the exact pacing.
/// </para>
/// </summary>
internal sealed class InfraredExchangeStage : IPostStage {
    private const ulong BudgetStep = 256;
    private const int StepCount = 512;
    // Two distinct, non-uniform 24-bit patterns so every received bit is attributable to its sender and neither transcript
    // is a trivial all-0/all-1 line.
    private static readonly byte[] FirstPatternSource = [0xB4, 0x6C, 0x39];
    private static readonly byte[] SecondPatternSource = [0x1E, 0xC3, 0x5A];

    /// <inheritdoc/>
    public string Name =>
        "infrared-exchange";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (RunSelfSensingProbes() is { } selfSensingFailure) {
            return PostStageOutcome.Fail(detail: selfSensingFailure);
        }

        var firstPattern = InfraredRom.ExpandPattern(sourceBytes: FirstPatternSource);
        var secondPattern = InfraredRom.ExpandPattern(sourceBytes: SecondPatternSource);
        var reference = RunScenario(firstPattern: firstPattern, secondPattern: secondPattern, churnAtStep: -1);

        if (Judge(result: reference, firstPattern: firstPattern, secondPattern: secondPattern) is { } failure) {
            return PostStageOutcome.Fail(detail: failure);
        }

        // The probed progress is the FIRST machine's RECEIVE progress — how many of the SECOND machine's bits it has
        // received so far (its own transmit phase never touches ProgressAddress).
        var churnStep = PickChurnStep(probes: reference.Probes, total: secondPattern.Length);

        if (churnStep < 0) {
            return PostStageOutcome.Fail(detail: "no mid-exchange budget boundary appeared; the pattern length or budget schedule is wrong");
        }

        // Determinism: a second fresh run on the same schedule reproduces both transcripts and both final snapshots.
        var replay = RunScenario(firstPattern: firstPattern, secondPattern: secondPattern, churnAtStep: -1);

        if (Difference(expected: reference, actual: replay, leg: "replay") is { } replayFailure) {
            return PostStageOutcome.Fail(detail: replayFailure);
        }

        // Churn: suspend/snapshot/restore/reconnect (with the resume token) mid-exchange, continue, demand the identical tail.
        var churned = RunScenario(firstPattern: firstPattern, secondPattern: secondPattern, churnAtStep: churnStep);

        if (Difference(expected: reference, actual: churned, leg: "churn") is { } churnFailure) {
            return PostStageOutcome.Fail(detail: churnFailure);
        }

        return PostStageOutcome.Pass(
            detail: $"self-sensing (unpaired CGB via RP and HuC1, unpaired Agb suppressed without a HuC cartridge, cross-view consistency with one present) plus {firstPattern.Length} IR bits exchanged each way (two cgb machines over RP), each side received the peer's pattern exactly, replay- and churn-identical (severed mid-exchange at budget step {churnStep}, {reference.FirstState.Size}+{reference.SecondState.Size} state bytes)"
        );
    }

    // M-02 probes: hardware self-sensing (SameBoy Core/memory.c ~723, Core/timing.c ~136-140 — see
    // InfraredPort.ReceivedLight for the full citation and the deliberate cross-view unification). Single, unpaired
    // machines — no link session — since self-sensing is defined with NO peer attached.
    private static string? RunSelfSensingProbes() {
        if (VerifyCgbSelfSensesOwnLedViaRp() is { } rpFailure) {
            return rpFailure;
        }

        if (VerifyCgbSelfSensesOwnCartLedViaHuC1Window() is { } huc1Failure) {
            return huc1Failure;
        }

        if (VerifyAgbRpSelfSenseSuppressedWithoutHuC() is { } agbFailure) {
            return agbFailure;
        }

        if (VerifyCrossViewConsistencyWithHuCCartridge() is { } crossViewFailure) {
            return crossViewFailure;
        }

        return null;
    }

    // An unpaired CGB arms RP's data-read-enable bits and lights its own LED bit — with no peer at all, it must read its
    // own light back (RP bit 1 clear), not dark.
    private static string? VerifyCgbSelfSensesOwnLedViaRp() {
        using var machine = PostMachine.Build(model: ConsoleModel.Cgb, rom: SyntheticRom.Create());
        var bus = machine.GetRequiredService<ISystemBus>();

        bus.WriteByte(address: MemoryMap.InfraredPort, value: 0xC1);

        var value = bus.ReadByte(address: MemoryMap.InfraredPort);

        return ((value == 0xFD)
            ? null
            : $"an unpaired CGB with RP's own LED lit read RP=0x{value:X2}; expected 0xFD (bit 1 clear — self-sensed light)");
    }
    // An unpaired CGB selects a HuC1 cartridge's IR window and lights the shared cart LED through it — the SAME window
    // must read its own light back.
    private static string? VerifyCgbSelfSensesOwnCartLedViaHuC1Window() {
        var rom = SyntheticRom.Create(cartridgeType: 0xFF, ramSize: 0x02); // HuC1 + RAM + battery

        using var machine = PostMachine.Build(model: ConsoleModel.Cgb, rom: rom);

        var cartridge = machine.GetRequiredService<ICartridge>();

        cartridge.WriteControl(address: 0x0000, value: 0x0E); // route the external window to the IR register
        cartridge.WriteRam(address: 0xA000, value: 0x01); // light the shared cart LED

        var value = cartridge.ReadRam(address: 0xA000);

        return ((value == 0xC1)
            ? null
            : $"an unpaired CGB with its cart IR LED lit read the HuC1 window=0x{value:X2}; expected 0xC1 (bit 0 set — self-sensed light)");
    }
    // An unpaired Agb costume with no HuC cartridge lights its own RP LED bit and must still read dark — Puck's
    // pre-existing correct behavior, now guarded as a regression probe against the self-sensing fix.
    private static string? VerifyAgbRpSelfSenseSuppressedWithoutHuC() {
        using var machine = PostMachine.Build(model: ConsoleModel.Agb, rom: SyntheticRom.Create());
        var bus = machine.GetRequiredService<ISystemBus>();

        bus.WriteByte(address: MemoryMap.InfraredPort, value: 0xC1);

        var value = bus.ReadByte(address: MemoryMap.InfraredPort);

        return ((value == 0xFF)
            ? null
            : $"an unpaired Agb costume with no HuC cartridge and RP's own LED lit read RP=0x{value:X2}; expected 0xFF (bit 1 set — RP self-sensing stays suppressed)");
    }
    // An Agb costume WITH a HuC1 cartridge present: a HuC cartridge widens Agb self-sensing (SameBoy's "unless a HuC
    // cartridge is present" exception), and because Puck's one shared transceiver feeds every view identically, driving
    // the LED through RP alone must be visible through the HuC1 window too — cross-view consistency.
    private static string? VerifyCrossViewConsistencyWithHuCCartridge() {
        var rom = SyntheticRom.Create(cartridgeType: 0xFF, ramSize: 0x02); // HuC1 + RAM + battery

        using var machine = PostMachine.Build(model: ConsoleModel.Agb, rom: rom);

        var bus = machine.GetRequiredService<ISystemBus>();
        var cartridge = machine.GetRequiredService<ICartridge>();

        bus.WriteByte(address: MemoryMap.InfraredPort, value: 0xC1);

        var rpValue = bus.ReadByte(address: MemoryMap.InfraredPort);

        cartridge.WriteControl(address: 0x0000, value: 0x0E);

        var huc1Value = cartridge.ReadRam(address: 0xA000);

        if (rpValue != 0xFD) {
            return $"an Agb costume with a HuC1 cartridge present and RP's own LED lit read RP=0x{rpValue:X2}; expected 0xFD (a present HuC cartridge widens Agb self-sensing)";
        }

        if (huc1Value != 0xC1) {
            return $"the same Agb+HuC1 machine's HuC1 window read 0x{huc1Value:X2} for the RP-driven LED; expected 0xC1 (cross-view: RP and HuC1 must see the same effective light)";
        }

        return null;
    }

    // One complete linked scenario on the fixed budget schedule. The first machine transmits its pattern, then receives
    // the second's; the second receives first, then transmits — pairing the two roles keeps each transmit phase inside
    // the peer's matching receive phase (see InfraredRom's remarks). With churnAtStep >= 0 the session is suspended at
    // that boundary (which the reference confirmed mid-exchange), both machines snapshotted and restored into fresh
    // machines, and the cable reconnected with the resume token before the remaining budgets run.
    private static InfraredScenarioResult RunScenario(byte[] firstPattern, byte[] secondPattern, int churnAtStep) {
        var firstRom = InfraredRom.CreatePrimary(patternBits: firstPattern, expectedReceiveCount: secondPattern.Length);
        var secondRom = InfraredRom.CreateSecondary(patternBits: secondPattern, expectedReceiveCount: firstPattern.Length);
        var first = PostMachine.Build(model: ConsoleModel.Cgb, rom: firstRom);
        var second = PostMachine.Build(model: ConsoleModel.Cgb, rom: secondRom);
        var probes = new List<int>(capacity: StepCount);

        try {
            var session = new IrLinkSession(first: first, second: second);

            try {
                for (var step = 0; (step < StepCount); ++step) {
                    probes.Add(item: ReadProgress(instance: first));

                    if (step == churnAtStep) {
                        var token = session.Suspend();
                        var firstState = first.Machine.Snapshot();
                        var secondState = second.Machine.Snapshot();
                        var freshFirst = PostMachine.Build(model: ConsoleModel.Cgb, rom: firstRom);
                        var freshSecond = PostMachine.Build(model: ConsoleModel.Cgb, rom: secondRom);

                        freshFirst.Machine.Restore(snapshot: firstState);
                        freshSecond.Machine.Restore(snapshot: secondState);

                        first.Dispose();
                        second.Dispose();

                        first = freshFirst;
                        second = freshSecond;

                        session = new IrLinkSession(first: first, second: second, resumeToken: token);
                    }

                    session.Run(tCycles: BudgetStep);
                }

                return new InfraredScenarioResult(
                    FirstReceived: ReadReceived(instance: first, count: secondPattern.Length),
                    FirstMarker: ReadByte(instance: first, address: InfraredRom.CompletionMarkerAddress),
                    FirstProgress: ReadProgress(instance: first),
                    FirstState: first.Machine.Snapshot(),
                    Probes: probes,
                    SecondReceived: ReadReceived(instance: second, count: firstPattern.Length),
                    SecondMarker: ReadByte(instance: second, address: InfraredRom.CompletionMarkerAddress),
                    SecondProgress: ReadProgress(instance: second),
                    SecondState: second.Machine.Snapshot()
                );
            } finally {
                session.Dispose();
            }
        } finally {
            first.Dispose();
            second.Dispose();
        }
    }

    // Judges the reference run: both sides finished every bit, and each side received the OTHER side's exact pattern — so
    // the light really crossed both ways and was read back bit-for-bit.
    private static string? Judge(InfraredScenarioResult result, byte[] firstPattern, byte[] secondPattern) {
        if (result.FirstMarker != InfraredRom.CompletionMarker) {
            return $"the first machine never finished its exchange (marker 0x{result.FirstMarker:X2})";
        }

        if (result.SecondMarker != InfraredRom.CompletionMarker) {
            return $"the second machine never finished its exchange (marker 0x{result.SecondMarker:X2})";
        }

        if (result.FirstProgress != secondPattern.Length) {
            return $"the first machine received {result.FirstProgress} bits; expected {secondPattern.Length}";
        }

        if (result.SecondProgress != firstPattern.Length) {
            return $"the second machine received {result.SecondProgress} bits; expected {firstPattern.Length}";
        }

        // The first machine must have received the SECOND's pattern, and vice versa — the attributable transcript match.
        if (!result.FirstReceived.AsSpan().SequenceEqual(other: secondPattern)) {
            return "the first machine's received transcript did not match the second machine's sent pattern";
        }

        if (!result.SecondReceived.AsSpan().SequenceEqual(other: firstPattern)) {
            return "the second machine's received transcript did not match the first machine's sent pattern";
        }

        return null;
    }

    // Compares a later run against the reference: both received transcripts and both final snapshots must match exactly.
    private static string? Difference(InfraredScenarioResult expected, InfraredScenarioResult actual, string leg) {
        if (!expected.FirstReceived.AsSpan().SequenceEqual(other: actual.FirstReceived)) {
            return $"the {leg} first-machine received transcript diverged";
        }

        if (!expected.SecondReceived.AsSpan().SequenceEqual(other: actual.SecondReceived)) {
            return $"the {leg} second-machine received transcript diverged";
        }

        if (!expected.FirstState.ContentEquals(other: actual.FirstState)) {
            return $"the {leg} first-machine final state diverged — {HashDivergenceProbe.DescribeDivergence(a: expected.FirstState, b: actual.FirstState)}";
        }

        if (!expected.SecondState.ContentEquals(other: actual.SecondState)) {
            return $"the {leg} second-machine final state diverged — {HashDivergenceProbe.DescribeDivergence(a: expected.SecondState, b: actual.SecondState)}";
        }

        return null;
    }

    // The first budget boundary with at least one bit received but not all — a genuine mid-exchange severable instant. (Any
    // IR boundary is a clean severing instant — no bit is ever mid-shift — so only the mid-exchange window matters here.)
    private static int PickChurnStep(List<int> probes, int total) {
        for (var step = 0; (step < probes.Count); ++step) {
            if ((probes[index: step] >= 1) && (probes[index: step] < total)) {
                return step;
            }
        }

        return -1;
    }

    private static byte[] ReadReceived(MachineInstance instance, int count) {
        var bus = instance.GetRequiredService<ISystemBus>();
        var received = new byte[count];

        for (var index = 0; (index < count); ++index) {
            received[index] = bus.ReadByte(address: (ushort)(InfraredRom.ReceiveBufferAddress + index));
        }

        return received;
    }
    private static int ReadProgress(MachineInstance instance) =>
        ReadByte(instance: instance, address: InfraredRom.ProgressAddress);
    private static byte ReadByte(MachineInstance instance, ushort address) =>
        instance.GetRequiredService<ISystemBus>().ReadByte(address: address);

    private sealed record InfraredScenarioResult(
        byte[] FirstReceived,
        byte FirstMarker,
        int FirstProgress,
        MachineSnapshot FirstState,
        List<int> Probes,
        byte[] SecondReceived,
        byte SecondMarker,
        int SecondProgress,
        MachineSnapshot SecondState
    );
}
