using System.Numerics;
using Puck.Abstractions.Machines;
using Puck.GamingBricks;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-A stage: two RUNNING queued <see cref="MachineHost"/>s cable-linked into one
/// <see cref="LinkedMachineGroup"/> behave as one owned unit. The link steps, the members do not; per-seat pads reach
/// the right machine; both members keep publishing frames, audio, and step counts through their own hosts; the group's
/// bounded pending window backpressures as a unit; and disposing the link severs the cable and returns both machines to
/// independent stepping. Each seat's synthetic ROM samples its own joypad, publishes it to a readable work-RAM cell,
/// and exchanges it over the cable, so a routing mistake reads back as the wrong seat's button image.
/// </summary>
internal sealed class LinkedHostCableStage : IPostStage<PostContext> {
    private const int AudioSampleRate = 32_000;
    private const int BackpressureSubmissions = 64;
    private const int LinkedSteps = 24;
    private const int SeveredSteps = 4;
    // The images the two seats' ROMs must sample: South folds to the console's A (action bit 0) and Start to its Start
    // (action bit 3), so the seats are distinguishable in one byte.
    private const byte FirstSeatImage = 0x01;
    private const byte SecondSeatImage = 0x08;

    /// <inheritdoc/>
    public string Name =>
        "linked-host-cable";
    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        using var first = LinkedHostFixture.NewHost(
            audioSampleRate: AudioSampleRate,
            internalClock: true
        );
        using var second = LinkedHostFixture.NewHost(
            audioSampleRate: AudioSampleRate,
            internalClock: false
        );
        var engine = new GamingBrickEngine();

        if (!engine.TryLink(
            machines: [first, second],
            link: out var established,
            reason: out var reason
        )) {
            return PostStageOutcome.Fail(detail: $"the engine refused to link two running machines: {reason}");
        }

        var link = ((LinkedMachineGroup)established!);
        var firstPad = LinkedHostFixture.Pad(buttons: MachineButtons.South);
        var secondPad = LinkedHostFixture.Pad(buttons: MachineButtons.Start);
        var stepsBeforeLink = (First: first.CompletedSteps, Second: second.CompletedSteps);
        var lightBeforeLink = (First: first.EmittedLight, Second: second.EmittedLight);

        try {
            for (var step = 0; (step < LinkedSteps); ++step) {
                link.Step(
                    deltaTicks: LinkedHostFixture.FrameTicks,
                    inputs: [firstPad, secondPad]
                );
            }

            if (link.CompletedTransfers <= 0L) {
                return PostStageOutcome.Fail(detail: $"the link carried no serial traffic across {LinkedSteps} stepped frames; the cable is dormant, not live");
            }

            if (Exclusivity(
                first: first,
                firstPad: in firstPad,
                second: second,
                secondPad: in secondPad
            ) is { } exclusivityFailure) {
                return PostStageOutcome.Fail(detail: exclusivityFailure);
            }

            if (Routing(
                first: first,
                second: second
            ) is { } routingFailure) {
                return PostStageOutcome.Fail(detail: routingFailure);
            }

            if (Publication(
                first: first,
                lightBeforeLink: lightBeforeLink,
                second: second,
                stepsBeforeLink: stepsBeforeLink
            ) is { } publicationFailure) {
                return PostStageOutcome.Fail(detail: publicationFailure);
            }

            if (Backpressure(
                firstPad: in firstPad,
                link: link,
                secondPad: in secondPad
            ) is { } backpressureFailure) {
                return PostStageOutcome.Fail(detail: backpressureFailure);
            }
        } finally {
            link.Dispose();
        }

        return Severed(
            first: first,
            firstPad: in firstPad,
            link: link,
            second: second,
            secondPad: in secondPad
        );
    }

    // Sustained pressure on the group's own bounded window: submissions outrun the link thread and wait for capacity
    // rather than dropping or coalescing a segment, and the synchronous step still drains the whole backlog.
    private static string? Backpressure(LinkedMachineGroup link, in MachinePadState firstPad, in MachinePadState secondPad) {
        for (var submission = 0; (submission < BackpressureSubmissions); ++submission) {
            if (link.Submit(
                deltaTicks: LinkedHostFixture.FrameTicks,
                inputs: [firstPad, secondPad]
            ) == QueuedMachineSubmission.Rejected) {
                return $"the link rejected group segment {submission} while accepting work";
            }
        }

        if (link.BackpressureEvents <= 0L) {
            return $"{BackpressureSubmissions} back-to-back group submissions never filled the link's {link.MaximumPendingSteps}-segment window; the group is not backpressuring as a unit";
        }

        link.Step(
            deltaTicks: LinkedHostFixture.FrameTicks,
            inputs: [firstPad, secondPad]
        );

        return ((link.PendingSteps == 0L)
            ? null
            : $"the link's synchronous step returned with {link.PendingSteps} segments still pending; it did not drain"
        );
    }
    // The link is the only stepper while the cable is in: a member's own step surface refuses work and advances nothing.
    private static string? Exclusivity(MachineHost first, MachineHost second, in MachinePadState firstPad, in MachinePadState secondPad) {
        var before = (First: first.CompletedSteps, Second: second.CompletedSteps);

        if (first.Step(
            deltaTicks: LinkedHostFixture.FrameTicks,
            input: in firstPad
        )) {
            return "the first member's own Step advanced it while it was linked";
        }

        if (second.Submit(
            deltaTicks: LinkedHostFixture.FrameTicks,
            input: in secondPad
        ) != QueuedMachineSubmission.Rejected) {
            return "the second member accepted a direct submission while it was linked";
        }

        return (((first.CompletedSteps == before.First) && (second.CompletedSteps == before.Second))
            ? null
            : "a direct step on a linked member advanced its completed-step count"
        );
    }
    // Every accepted group segment publishes through each member's OWN host: its framebuffer stages, its audio ring
    // fills, and its completed-step count climbs, exactly as an independently stepped machine's would.
    private static string? Publication(
        MachineHost first,
        MachineHost second,
        (long First, long Second) stepsBeforeLink,
        (Vector3 First, Vector3 Second) lightBeforeLink
    ) {
        if (first.CompletedSteps <= stepsBeforeLink.First) {
            return "the first member's completed-step count never advanced while linked; its host stopped publishing";
        }

        if (second.CompletedSteps <= stepsBeforeLink.Second) {
            return "the second member's completed-step count never advanced while linked; its host stopped publishing";
        }

        if (first.EmittedLight == lightBeforeLink.First) {
            return "the first member's framebuffer never changed while linked; its host staged no frame";
        }

        if (second.EmittedLight == lightBeforeLink.Second) {
            return "the second member's framebuffer never changed while linked; its host staged no frame";
        }

        Span<short> samples = stackalloc short[1_024];

        if (first.ReadSamples(destination: samples) <= 0) {
            return "the first member's audio ring stayed empty while linked; its host drained no audio";
        }

        return ((second.ReadSamples(destination: samples) > 0)
            ? null
            : "the second member's audio ring stayed empty while linked; its host drained no audio"
        );
    }
    // The seat check: each ROM publishes its OWN sampled joypad image and the last image it received, so a link that fed
    // both machines the same pad, or swapped the seats, reads back here rather than hiding inside a hash.
    private static string? Routing(MachineHost first, MachineHost second) {
        var firstImage = first.PeekByte(address: LinkedHostFixture.JoypadImageAddress);
        var secondImage = second.PeekByte(address: LinkedHostFixture.JoypadImageAddress);

        if (firstImage != FirstSeatImage) {
            return $"the first seat's machine sampled joypad image 0x{firstImage:X2}; expected 0x{FirstSeatImage:X2}";
        }

        if (secondImage != SecondSeatImage) {
            return $"the second seat's machine sampled joypad image 0x{secondImage:X2}; expected 0x{SecondSeatImage:X2}";
        }

        var firstPeer = first.PeekByte(address: LinkedHostFixture.PeerImageAddress);
        var secondPeer = second.PeekByte(address: LinkedHostFixture.PeerImageAddress);

        if (firstPeer != SecondSeatImage) {
            return $"the first seat received 0x{firstPeer:X2} over the cable; expected the second seat's 0x{SecondSeatImage:X2}";
        }

        return ((secondPeer == FirstSeatImage)
            ? null
            : $"the second seat received 0x{secondPeer:X2} over the cable; expected the first seat's 0x{FirstSeatImage:X2}"
        );
    }
    // After the cable is unplugged both machines step independently again and the cable carries nothing more.
    private static PostStageOutcome Severed(
        LinkedMachineGroup link,
        MachineHost first,
        MachineHost second,
        in MachinePadState firstPad,
        in MachinePadState secondPad
    ) {
        var transfersAtSever = link.CompletedTransfers;
        var stepsAtSever = (First: first.CompletedSteps, Second: second.CompletedSteps);
        // Read the seats' own counters at the sever: past it the master keeps clocking into an empty socket while the
        // slave's armed transfer waits forever, so only the pre-sever values are comparable.
        var counters = (First: first.PeekByte(address: LinkedHostFixture.TransferCountAddress), Second: second.PeekByte(address: LinkedHostFixture.TransferCountAddress));

        for (var step = 0; (step < SeveredSteps); ++step) {
            if (!first.Step(
                deltaTicks: LinkedHostFixture.FrameTicks,
                input: in firstPad
            )) {
                return PostStageOutcome.Fail(detail: "the first member did not step independently after the link was disposed");
            }

            if (!second.Step(
                deltaTicks: LinkedHostFixture.FrameTicks,
                input: in secondPad
            )) {
                return PostStageOutcome.Fail(detail: "the second member did not step independently after the link was disposed");
            }
        }

        if (
            (first.CompletedSteps != (stepsAtSever.First + SeveredSteps)) ||
            (second.CompletedSteps != (stepsAtSever.Second + SeveredSteps))
        ) {
            return PostStageOutcome.Fail(detail: $"independent stepping after the sever completed {(first.CompletedSteps - stepsAtSever.First)}/{(second.CompletedSteps - stepsAtSever.Second)} of {SeveredSteps} segments per member");
        }

        if (link.CompletedTransfers != transfersAtSever) {
            return PostStageOutcome.Fail(detail: $"the cable carried {(link.CompletedTransfers - transfersAtSever)} more bytes after it was severed");
        }

        // Unplugging is immediate and hardware-faithful: the internal-clock seat keeps clocking into an empty socket and
        // completes its own transfers, while the external-clock seat's armed transfer waits for edges that never arrive.
        if (first.PeekByte(address: LinkedHostFixture.TransferCountAddress) == counters.First) {
            return PostStageOutcome.Fail(detail: "the internal-clock seat completed no transfer after the sever; its own clock stopped with the cable");
        }

        if (second.PeekByte(address: LinkedHostFixture.TransferCountAddress) != counters.Second) {
            return PostStageOutcome.Fail(detail: "the external-clock seat completed a transfer after the sever; an unplugged cable still delivered edges");
        }

        return PostStageOutcome.Pass(detail: $"{transfersAtSever} bytes crossed the live cable (seat transfer counters 0x{counters.First:X2}/0x{counters.Second:X2}), per-seat pads landed 0x{FirstSeatImage:X2}/0x{SecondSeatImage:X2} on the right machines, both members published frames and audio throughout, the group backpressured as a unit, and the sever left the external-clock seat's transfer pending while both machines stepped independently again");
    }
}
