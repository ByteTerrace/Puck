namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// Tier-C stage: the link cable — two consoles, one deterministic multiplayer exchange. A parent and a child each
/// boot their half of the <see cref="MicroRoms"/> link protocol and are advanced together through an
/// <see cref="AgbLinkSession"/> in per-frame budgets (the same shape a host engine drives). The parent clocks
/// <see cref="MicroRoms.LinkRounds"/> SIO multiplayer rounds; every round must deliver BOTH sides' send words into
/// BOTH sides' SIOMULTI slots — and because each child reply after the first is a transform of the parent word it
/// received the round before, the recorded slots prove data really crossed the cable, not idle lines. Both sides must
/// also observe one serial IRQ request per round and read back the correct daisy-chain id bits (parent 0, child 1)
/// with the busy bit clear. The whole linked scenario then runs a SECOND time from fresh machines with the identical
/// budget schedule and must reproduce byte-identical final whole-machine snapshots on both consoles — the
/// replay-identical proof that the link (and its furthest-behind interleave) adds no nondeterminism. Self-contained:
/// the protocol polls IF rather than taking interrupts, so it runs on the zeroed stub BIOS, anywhere.
/// </summary>
internal sealed class LinkReplayStage : IPostStage {
    private const int Frames = 4;
    private const uint ExpectedParentControl = 0x6003u; // multiplayer | 115200 bps | IRQ-enable, start clear, id 0
    private const uint ExpectedChildControl = 0x6013u;  // as the parent, with daisy-chain id 1 in bits 4-5

    /// <inheritdoc/>
    public string Name =>
        "link-replay";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);

        var first = RunLinkedScenario(bios: context.BiosImage);

        if (Verify(result: first) is { } failure) {
            return PostStageOutcome.Fail(detail: failure);
        }

        var second = RunLinkedScenario(bios: context.BiosImage);

        if (!first.ParentState.ContentEquals(other: second.ParentState)) {
            return PostStageOutcome.Fail(detail: $"the parent console's final state differed between two identical linked runs — {HashDivergenceProbe.DescribeDivergence(a: first.ParentState, b: second.ParentState)}");
        }

        if (!first.ChildState.ContentEquals(other: second.ChildState)) {
            return PostStageOutcome.Fail(detail: $"the child console's final state differed between two identical linked runs — {HashDivergenceProbe.DescribeDivergence(a: first.ChildState, b: second.ChildState)}");
        }

        var lastParentWord = (ushort)((MicroRoms.LinkParentSendBase + MicroRoms.LinkRounds) - 1);
        var lastChildWord = (ushort)(((MicroRoms.LinkParentSendBase + MicroRoms.LinkRounds) - 2) ^ MicroRoms.LinkChildTransformMask);

        return PostStageOutcome.Pass(
            detail: $"{MicroRoms.LinkRounds} multiplayer rounds exchanged both ways (parent 0x{MicroRoms.LinkParentSendBase:X4}..0x{lastParentWord:X4} ↔ child 0x{MicroRoms.LinkChildSeedWord:X4},transforms..0x{lastChildWord:X4}), {MicroRoms.LinkRounds} serial IRQs per side, ids 0/1, replay-identical across two runs ({first.ParentState.Size}+{first.ChildState.Size} state bytes)"
        );
    }

    // One complete linked scenario from freshly built consoles: connect, run the per-frame budget schedule, read both
    // sides' IWRAM verdicts, snapshot. Fully self-contained so the determinism leg can repeat it identically.
    private static LinkScenarioResult RunLinkedScenario(ReadOnlyMemory<byte> bios) {
        using var parent = CreateConsole(bios: bios, kind: "link-parent");
        using var child = CreateConsole(bios: bios, kind: "link-child");
        using var session = new AgbLinkSession(parent, child);

        for (var frame = 0; (frame < Frames); ++frame) {
            session.Run(cycles: PostMachine.CyclesPerFrame);
        }

        return new LinkScenarioResult(
            ParentVerdict: ReadVerdict(console: parent),
            ChildVerdict: ReadVerdict(console: child),
            ParentState: parent.Machine.Snapshot(),
            ChildState: child.Machine.Snapshot()
        );
    }
    private static AgbMachineInstance CreateConsole(ReadOnlyMemory<byte> bios, string kind) {
        var console = AgbMachineFactory.Create(configuration: new AgbMachineConfiguration(bios: bios, rom: MicroRoms.GenerateBytes(kind: kind)));

        console.Machine.DirectBoot();

        return console;
    }

    // Reads a side's IWRAM verdict through the side-effect-free debug peek (no clock movement, so read order can
    // never perturb the snapshots).
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

    // Judges the first run's protocol outcomes; null means every expectation held.
    private static string? Verify(LinkScenarioResult result) =>
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

        // Round k's slots on EVERY console: slot 0 = the parent's word, slot 1 = the child's reply (the seed for
        // round 0, the transform of the previous parent word after), slots 2/3 = absent (0xFFFF).
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

    private readonly record struct LinkScenarioResult(
        LinkSideVerdict ParentVerdict,
        LinkSideVerdict ChildVerdict,
        AgbMachineSnapshot ParentState,
        AgbMachineSnapshot ChildState
    );
    private readonly record struct LinkSideVerdict(
        uint IrqCount,
        uint Marker,
        uint SerialControl,
        (uint Low, uint High)[] Rounds
    );
}
