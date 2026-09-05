using Puck.Abstractions.Machines;
using Puck.GamingBricks;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-A stage: a live cable link is replay-identical and rewinds as one coupled group. Two runs of the same scripted
/// per-seat input over two freshly booted linked hosts must produce the same cable traffic fingerprint and the same
/// group state image — both members' snapshots plus the pair-stepper's own overshoot credits, which no member's
/// snapshot carries. Then, in one run, a rewind must land the whole group byte-identical to the recorded instant it
/// seeks to, and replaying the recorded input tail from that landing must reproduce the un-rewound future exactly.
/// Fast-forward closes the leg: one submission must buy the factor's worth of emulated cycles for the whole group
/// while still publishing exactly one frame per member.
/// </summary>
internal sealed class LinkedHostTimeTravelStage : IPostStage<PostContext> {
    private const int FastForwardFactor = 4;
    private const int RewindFrames = 6;
    private const int ScriptedSteps = 28;

    /// <inheritdoc/>
    public string Name =>
        "linked-host-time-travel";
    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var reference = RunScript(captureEveryStep: true);
        var replay = RunScript(captureEveryStep: false);

        if (reference.TrafficFingerprint != replay.TrafficFingerprint) {
            return PostStageOutcome.Fail(detail: $"two identical linked runs produced different cable traffic (0x{reference.TrafficFingerprint:X16} then 0x{replay.TrafficFingerprint:X16})");
        }

        if (reference.CompletedTransfers != replay.CompletedTransfers) {
            return PostStageOutcome.Fail(detail: $"two identical linked runs exchanged {reference.CompletedTransfers} then {replay.CompletedTransfers} bytes");
        }

        if (!reference.Images[^1].AsSpan().SequenceEqual(other: replay.Images[^1])) {
            return PostStageOutcome.Fail(detail: "two identical linked runs ended on different group state images; the linked pair is not replay-identical");
        }

        return Rewind(reference: reference);
    }

    // The rewind leg reuses the reference run's own recorded instants: after moving back, the group's shared cycle count
    // identifies exactly which recorded step it landed on, so the landed image is compared against that step's image
    // rather than against a guess, and the input tail replayed forward is exactly the one the un-rewound run ran.
    private static PostStageOutcome Rewind(ScriptRun reference) {
        using var first = LinkedHostFixture.NewHost(internalClock: true);
        using var second = LinkedHostFixture.NewHost(internalClock: false);
        var engine = new GamingBrickEngine();

        if (!engine.TryLink(
            machines: [first, second],
            link: out var established,
            reason: out var reason
        )) {
            return PostStageOutcome.Fail(detail: $"the engine refused to link two running machines: {reason}");
        }

        using var link = ((LinkedMachineGroup)established!);

        link.SetRewindEnabled(enabled: true);

        for (var step = 0; (step < ScriptedSteps); ++step) {
            StepScript(
                link: link,
                step: step
            );
        }

        var rewound = link.RewindBy(frames: RewindFrames);

        if (rewound <= 0) {
            return PostStageOutcome.Fail(detail: $"a {RewindFrames}-frame rewind of the linked group moved nothing; the coupled ring captured no history");
        }

        var landedCycles = link.CycleCount;
        var landedStep = Array.IndexOf(
            array: reference.Cycles,
            value: landedCycles
        );

        if (landedStep < 0) {
            return PostStageOutcome.Fail(detail: $"the rewind landed at cycle {landedCycles}, which is not one of the recorded instants; the group's replay does not reconstruct a captured frame");
        }

        var landedImage = link.CaptureState();

        if (!landedImage.AsSpan().SequenceEqual(other: reference.Images[landedStep])) {
            return PostStageOutcome.Fail(detail: $"the rewind landed on recorded step {landedStep}'s cycle but not its state; a member or the pair-stepper's pacing did not restore");
        }

        for (var step = (landedStep + 1); (step < ScriptedSteps); ++step) {
            StepScript(
                link: link,
                step: step
            );
        }

        var resumedImage = link.CaptureState();

        if (!resumedImage.AsSpan().SequenceEqual(other: reference.Images[^1])) {
            return PostStageOutcome.Fail(detail: $"replaying the input tail from recorded step {landedStep} did not reproduce the un-rewound future; the resumed group diverged");
        }

        if (FastForward(
            first: first,
            link: link,
            second: second
        ) is { } fastForwardFailure) {
            return PostStageOutcome.Fail(detail: fastForwardFailure);
        }

        return PostStageOutcome.Pass(detail: $"two identical linked runs agreed on {reference.CompletedTransfers} exchanged bytes, traffic fingerprint 0x{reference.TrafficFingerprint:X16}, and a {reference.Images[^1].Length}-byte group state image; a {rewound}-frame coupled rewind landed byte-identical on recorded step {landedStep} and its replayed tail reproduced the un-rewound future; x{FastForwardFactor} advanced the group by exactly {FastForwardFactor} segments per submission with both members still one published frame apiece");
    }
    // Fast-forward is a group-level segment repeat, not a clock multiplier: one submission buys the factor's worth of
    // emulated cycles for every member at once, and still publishes one frame per member.
    private static string? FastForward(LinkedMachineGroup link, MachineHost first, MachineHost second) {
        var before = link.CycleCount;

        StepScript(
            link: link,
            step: 0
        );

        var oneSegment = (link.CycleCount - before);

        link.SetFastForward(factor: FastForwardFactor);

        var stepsBefore = (First: first.CompletedSteps, Second: second.CompletedSteps);
        var cyclesBefore = link.CycleCount;

        StepScript(
            link: link,
            step: 0
        );

        var advanced = (link.CycleCount - cyclesBefore);
        var expected = (oneSegment * FastForwardFactor);

        link.SetFastForward(factor: 1);

        // Stepping is instruction-atomic and the tick-to-cycle accumulator carries a remainder, so a measured advance
        // overshoots its budget by up to one instruction. The band is stated in whole segments rather than cycles: it
        // still separates a repeated segment from an unrepeated one, without pinning an instruction-length constant.
        if (
            (advanced < (oneSegment * (FastForwardFactor - 1))) ||
            (advanced > (oneSegment * (FastForwardFactor + 1)))
        ) {
            return $"an x{FastForwardFactor} group submission advanced {advanced} cycles; {FastForwardFactor} repeats of the {oneSegment}-cycle segment is about {expected}";
        }

        return ((((first.CompletedSteps - stepsBefore.First) == 1L) && ((second.CompletedSteps - stepsBefore.Second) == 1L))
            ? null
            : $"an x{FastForwardFactor} group submission published {(first.CompletedSteps - stepsBefore.First)}/{(second.CompletedSteps - stepsBefore.Second)} member frames; fast-forward must skip intermediate presentation, not multiply it"
        );
    }
    // One complete scripted run from freshly booted machines. The script's seat pads are a pure function of the step
    // index, so the whole run is reproducible; the per-step capture leg records the instants the rewind leg seeks.
    private static ScriptRun RunScript(bool captureEveryStep) {
        using var first = LinkedHostFixture.NewHost(internalClock: true);
        using var second = LinkedHostFixture.NewHost(internalClock: false);
        var engine = new GamingBrickEngine();

        if (!engine.TryLink(
            machines: [first, second],
            link: out var established,
            reason: out var reason
        )) {
            throw new InvalidOperationException(message: $"the engine refused to link two running machines: {reason}");
        }

        using var link = ((LinkedMachineGroup)established!);
        var cycles = new long[ScriptedSteps];
        var images = new byte[ScriptedSteps][];

        for (var step = 0; (step < ScriptedSteps); ++step) {
            StepScript(
                link: link,
                step: step
            );

            if (
                captureEveryStep ||
                (step == (ScriptedSteps - 1))
            ) {
                cycles[step] = link.CycleCount;
                images[step] = link.CaptureState();
            } else {
                images[step] = [];
            }
        }

        return new ScriptRun(
            CompletedTransfers: link.CompletedTransfers,
            Cycles: cycles,
            Images: images,
            TrafficFingerprint: link.TrafficFingerprint
        );
    }
    // The scripted seat input: each seat holds a different button for a different stretch of the run, so the two seats'
    // recorded histories are distinguishable and neither is constant across the rewind window.
    private static void StepScript(LinkedMachineGroup link, int step) {
        var firstPad = LinkedHostFixture.Pad(buttons: (((step % 4) < 2)
            ? MachineButtons.South
            : MachineButtons.East));
        var secondPad = LinkedHostFixture.Pad(buttons: (((step % 6) < 3)
            ? MachineButtons.Start
            : MachineButtons.Back));

        link.Step(
            deltaTicks: LinkedHostFixture.FrameTicks,
            inputs: [firstPad, secondPad]
        );
    }

    private readonly record struct ScriptRun(
        long CompletedTransfers,
        long[] Cycles,
        byte[][] Images,
        ulong TrafficFingerprint
    );
}
