using Puck.Abstractions.Gpu;
using Puck.Abstractions.Machines;

namespace Puck.Hosting;

/// <summary>The neutral outcome of a <see cref="QueuedHostContractProbe"/> check — a pass/fail flag and a detail message
/// each battery maps to its own Post outcome type.</summary>
/// <param name="Passed">Whether the checked contract held.</param>
/// <param name="Detail">The human-readable detail (the failure reason, or the pass summary).</param>
public readonly record struct QueuedHostProbeResult(bool Passed, string Detail) {
    /// <summary>A passing result.</summary>
    public static QueuedHostProbeResult Pass(string detail) => new(Passed: true, Detail: detail);

    /// <summary>A failing result.</summary>
    public static QueuedHostProbeResult Fail(string detail) => new(Passed: false, Detail: detail);
}

/// <summary>
/// The machine-neutral contract prover for the <see cref="QueuedMachineWorker"/> substrate — the single source of truth
/// each core's Post battery exercises against its own host. It pins the observable behavior the substrate owns: the
/// bounded pending-segment window with producer backpressure, exactly-once completion, an immutable frame lease across a
/// blocked GPU upload, and device-loss/disposal serialization with the uploader. A host supplies factories that build it
/// with the core's own synthetic content; the probe drives only the neutral
/// <see cref="IScreenMachine"/>/<see cref="IQueuedScreenMachine"/> surface, so both hosts run the identical checks.
/// </summary>
public static class QueuedHostContractProbe {
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(value: 15);

    /// <summary>Verifies the bounded queue admits a finite number of incomplete exact segments, waits for one completion
    /// under sustained pressure, reports that backpressure observably, and then accepts and completes every segment
    /// without dropping any accepted work.</summary>
    /// <typeparam name="THost">The queued host under test.</typeparam>
    /// <param name="withContent">Builds a fresh assigned host (with the core's synthetic content).</param>
    /// <returns>The contract result.</returns>
    public static QueuedHostProbeResult VerifyBackpressure<THost>(Func<THost> withContent)
        where THost : IScreenMachine, IQueuedScreenMachine {
        using var host = withContent();
        var input = MachinePadState.Neutral;
        var accepted = 0L;
        var observedBackpressure = false;
        var submissionLimit = (host.MaximumPendingSteps + 32);

        for (var index = 0; (index < submissionLimit); ++index) {
            var submission = host.Submit(deltaTicks: EngineTicks.PerSecond, input: in input);

            if (submission == QueuedMachineSubmission.Rejected) {
                return QueuedHostProbeResult.Fail(detail: $"healthy assigned host rejected segment {index + 1}");
            }

            ++accepted;

            if (submission == QueuedMachineSubmission.AcceptedAfterBackpressure) {
                observedBackpressure = true;

                break;
            }
        }

        if (!observedBackpressure) {
            return QueuedHostProbeResult.Fail(
                detail: $"producer never observed the {host.MaximumPendingSteps}-segment capacity over {submissionLimit} one-second segments"
            );
        }

        if ((host.PendingSteps < 0L) || (host.PendingSteps > host.MaximumPendingSteps)) {
            return QueuedHostProbeResult.Fail(
                detail: $"pending segment count {host.PendingSteps} escaped capacity {host.MaximumPendingSteps}"
            );
        }

        if (host.BackpressureEvents != 1L) {
            return QueuedHostProbeResult.Fail(detail: $"expected one backpressure event, observed {host.BackpressureEvents}");
        }

        host.FlushSave();

        if ((host.CompletedSteps != accepted) || (host.PendingSteps != 0L)) {
            return QueuedHostProbeResult.Fail(
                detail: $"accepted {accepted} exact segments but drained completed={host.CompletedSteps}, pending={host.PendingSteps}"
            );
        }

        return QueuedHostProbeResult.Pass(
            detail: $"bounded queue accepted and completed all {accepted} exact segments; capacity={host.MaximumPendingSteps}, backpressure={host.BackpressureEvents}"
        );
    }

    /// <summary>Verifies a blocked CPU-to-GPU upload leases an immutable complete frame without holding the worker's frame
    /// lock, that later native frames keep completing, that concurrent publishes serialize, and that device loss and
    /// disposal wait until the uploader has finished consuming its caller-owned pixels.</summary>
    /// <typeparam name="THost">The queued host under test.</typeparam>
    /// <param name="withContent">Builds a fresh assigned host (with the core's synthetic content).</param>
    /// <param name="empty">Builds a fresh empty (unassigned) host.</param>
    /// <returns>The contract result.</returns>
    public static QueuedHostProbeResult VerifyFramePublication<THost>(Func<THost> withContent, Func<THost> empty)
        where THost : IScreenMachine, IQueuedScreenMachine {
        var publication = VerifyPublicationLease(withContent: withContent);

        if (publication is not null) {
            return publication.Value;
        }

        var deviceLoss = VerifyDeviceLossSerialization(empty: empty);

        if (deviceLoss is not null) {
            return deviceLoss.Value;
        }

        var disposal = VerifyDisposalSerialization(empty: empty);

        return (disposal ?? QueuedHostProbeResult.Pass(
            detail: "blocked upload bytes stayed immutable across worker publications; publish, device loss, and disposal serialized"
        ));
    }

    /// <summary>Verifies the neutral audio capability is gated strictly on host attachment: a host built with no audio
    /// consumer reports <see cref="IAudioMachine.SampleRate"/> 0 and never yields a sample no matter how much it runs
    /// (the observable proof that its core performs zero presentation-side synthesis while detached), while a host
    /// built WITH an audio consumer reports the requested rate and, after running enough segments to cover at least
    /// one output frame, yields samples through the same off-thread ring the frame publication lease already proves
    /// a consumer never touches the emulation thread through.</summary>
    /// <typeparam name="THost">The queued host under test.</typeparam>
    /// <param name="attached">Builds a fresh assigned host requesting audio at <paramref name="requestedRate"/>.</param>
    /// <param name="detached">Builds a fresh assigned host requesting no audio (rate 0).</param>
    /// <param name="requestedRate">The nonzero rate <paramref name="attached"/> requests.</param>
    /// <returns>The contract result.</returns>
    public static QueuedHostProbeResult VerifyAudio<THost>(Func<THost> attached, Func<THost> detached, int requestedRate)
        where THost : IScreenMachine, IAudioMachine {
        using var silentHost = detached();
        var input = MachinePadState.Neutral;

        if (silentHost.SampleRate != 0) {
            return QueuedHostProbeResult.Fail(detail: $"a host built with no audio consumer reported SampleRate={silentHost.SampleRate} (expected 0)");
        }

        _ = silentHost.Step(deltaTicks: EngineTicks.PerSecond, input: in input);

        Span<short> probe = stackalloc short[256];
        var silentSamples = silentHost.ReadSamples(destination: probe);

        if (silentSamples != 0) {
            return QueuedHostProbeResult.Fail(detail: $"a detached host yielded {silentSamples} audio samples after a full second of ticks (expected 0 — zero synthesis while unattached)");
        }

        using var soundedHost = attached();

        if (soundedHost.SampleRate != requestedRate) {
            return QueuedHostProbeResult.Fail(detail: $"a host built with a {requestedRate} Hz audio consumer reported SampleRate={soundedHost.SampleRate}");
        }

        // One second of ticks guarantees at least one output frame regardless of whether the ROM's mix is silent —
        // a powered mixer emits recentered zero frames, and a powered-off one emits true-silence frames; either way
        // the ring is non-empty once configured, so this proves the drain path itself, not ROM content.
        _ = soundedHost.Step(deltaTicks: EngineTicks.PerSecond, input: in input);

        var drained = 0;
        int written;

        do {
            written = soundedHost.ReadSamples(destination: probe);
            drained += written;
        } while (written == probe.Length);

        if (drained == 0) {
            return QueuedHostProbeResult.Fail(detail: $"an attached {requestedRate} Hz host yielded 0 audio samples after a full second of ticks");
        }

        return QueuedHostProbeResult.Pass(
            detail: $"detached host: SampleRate=0, 0 samples over 1s of ticks; attached host: SampleRate={requestedRate}, {drained} samples drained"
        );
    }

    /// <summary>Verifies the debug memory window (<see cref="IMachineMemoryPeek"/>) is marshaled through the worker
    /// rather than racing the running core: the marshaled poke path is deterministic (the serial reference — two
    /// identical ordered step/poke schedules reach a byte-identical image), cross-thread peek hammering leaves state
    /// replay-identical to an unhammered run (peek is side-effect-free even under concurrency), and, while a producer
    /// thread streams exact step segments, poke/peek round-trips to an address the streaming ROM never writes stay
    /// coherent and never fault the queue. Only a host that advertises <see cref="IMachineMemoryPeek"/> runs this.</summary>
    /// <typeparam name="THost">The queued host under test.</typeparam>
    /// <param name="withContent">Builds a fresh assigned host (with the core's synthetic content).</param>
    /// <param name="scratchAddress">A writable bus address the synthetic content's ROM never writes, used for the
    /// poke/peek round-trips.</param>
    /// <param name="regionStart">The first bus address of the ROM-written region compared for reproducibility.</param>
    /// <param name="regionLength">The length of that compared region.</param>
    /// <returns>The contract result.</returns>
    public static QueuedHostProbeResult VerifyConcurrentMemoryAccess<THost>(Func<THost> withContent, int scratchAddress, int regionStart, int regionLength)
        where THost : IScreenMachine, IQueuedScreenMachine, IMachineMemoryPeek {
        const int Steps = 90;
        const int Hammers = 400;

        var budget = EngineTicks.PerRate(ratePerSecond: 60);
        var input = MachinePadState.Neutral;
        var lastPoke = (byte)((Steps - 1) & 0xFF);

        // Check 1 — the marshaled poke path is deterministic (the serial reference). Two hosts driven with the identical
        // strictly-ordered [step, poke] schedule reach a byte-identical ROM-written image; synchronous Step drains and
        // PokeByte blocks on its barrier, so the schedule is fully ordered and each poke lands coherently between steps.
        byte[] referenceRegion;

        using (var host = withContent()) {
            DriveOrderedPokeSchedule(host: host, steps: Steps, budget: budget, input: in input, scratchAddress: scratchAddress);

            if (host.PeekByte(address: scratchAddress) != lastPoke) {
                return QueuedHostProbeResult.Fail(detail: $"a marshaled poke did not land: peeked 0x{host.PeekByte(address: scratchAddress):X2} at 0x{scratchAddress:X4}, expected 0x{lastPoke:X2}");
            }

            referenceRegion = SnapshotRegion(host: host, start: regionStart, length: regionLength);

            if (host.QueueFault is { } fault) {
                return QueuedHostProbeResult.Fail(detail: $"ordered poke-schedule host faulted: {fault}");
            }
        }

        using (var replay = withContent()) {
            DriveOrderedPokeSchedule(host: replay, steps: Steps, budget: budget, input: in input, scratchAddress: scratchAddress);

            if (!SnapshotRegion(host: replay, start: regionStart, length: regionLength).AsSpan().SequenceEqual(other: referenceRegion)) {
                return QueuedHostProbeResult.Fail(detail: "the marshaled poke schedule was not reproducible — two identical [step, poke] runs diverged");
            }
        }

        // Check 2 — peek is side-effect-free under concurrency. Drive an identical step schedule on two hosts, hammering
        // one with cross-thread peeks throughout (pokes absent); a peek that perturbed the core or advanced a
        // time-dependent component would leave the hammered run diverged from the unhammered one.
        var concurrent = VerifyPeekIsSideEffectFree(withContent: withContent, steps: Steps, budget: budget, input: in input, regionStart: regionStart, regionLength: regionLength);

        if (concurrent is not null) {
            return concurrent.Value;
        }

        // Check 3 — concurrent poke/peek coherence and fault-freedom while a producer thread streams exact step segments.
        var stress = VerifyConcurrentPokeStress(withContent: withContent, budget: budget, input: in input, scratchAddress: scratchAddress, hammers: Hammers);

        return (stress ?? QueuedHostProbeResult.Pass(
            detail: $"marshaled poke schedule reproducible; {Hammers} concurrent poke/peek round-trips coherent and fault-free; peek-hammering left state replay-identical"
        ));
    }

    private static QueuedHostProbeResult? VerifyPeekIsSideEffectFree<THost>(Func<THost> withContent, int steps, ulong budget, in MachinePadState input, int regionStart, int regionLength)
        where THost : IScreenMachine, IQueuedScreenMachine, IMachineMemoryPeek {
        var padState = input;
        byte[] hammeredRegion;

        using (var hammered = withContent()) {
            using var stop = new ManualResetEventSlim(initialState: false);
            var peekThread = new Thread(start: () => {
                while (!stop.IsSet) {
                    for (var address = 0; (address <= 0xFFFF); address += 0x40) {
                        _ = hammered.PeekByte(address: address);
                    }
                }
            });

            peekThread.Start();

            for (var step = 0; (step < steps); ++step) {
                _ = hammered.Step(deltaTicks: budget, input: in padState);
            }

            stop.Set();
            peekThread.Join();
            hammeredRegion = SnapshotRegion(host: hammered, start: regionStart, length: regionLength);

            if (hammered.QueueFault is { } fault) {
                return QueuedHostProbeResult.Fail(detail: $"peek-hammered host faulted: {fault}");
            }
        }

        using (var quiet = withContent()) {
            for (var step = 0; (step < steps); ++step) {
                _ = quiet.Step(deltaTicks: budget, input: in padState);
            }

            if (!SnapshotRegion(host: quiet, start: regionStart, length: regionLength).AsSpan().SequenceEqual(other: hammeredRegion)) {
                return QueuedHostProbeResult.Fail(detail: "cross-thread peek hammering perturbed machine state — the peek-hammered run diverged from an unhammered one");
            }
        }

        return null;
    }

    private static QueuedHostProbeResult? VerifyConcurrentPokeStress<THost>(Func<THost> withContent, ulong budget, in MachinePadState input, int scratchAddress, int hammers)
        where THost : IScreenMachine, IQueuedScreenMachine, IMachineMemoryPeek {
        var padState = input;

        using var host = withContent();
        using var stop = new ManualResetEventSlim(initialState: false);
        Exception? producerFault = null;
        var producer = new Thread(start: () => {
            try {
                while (!stop.IsSet) {
                    if (host.Submit(deltaTicks: budget, input: in padState) == QueuedMachineSubmission.Rejected) {
                        break;
                    }
                }
            } catch (Exception exception) {
                producerFault = exception;
            }
        });

        producer.Start();

        try {
            for (var hammer = 0; (hammer < hammers); ++hammer) {
                var value = (byte)(hammer & 0xFF);

                host.PokeByte(address: scratchAddress, value: value);

                var readback = host.PeekByte(address: scratchAddress);

                if (readback != value) {
                    return QueuedHostProbeResult.Fail(detail: $"a concurrent poke/peek round-trip tore at 0x{scratchAddress:X4}: poked 0x{value:X2}, peeked 0x{readback:X2}");
                }
            }
        } finally {
            stop.Set();
            producer.Join();
        }

        if (producerFault is not null) {
            return QueuedHostProbeResult.Fail(detail: $"the step producer faulted during concurrent memory access: {producerFault}");
        }

        return ((host.QueueFault is { } fault)
            ? QueuedHostProbeResult.Fail(detail: $"the queue faulted under concurrent step/peek/poke: {fault}")
            : null);
    }

    private static void DriveOrderedPokeSchedule<THost>(THost host, int steps, ulong budget, in MachinePadState input, int scratchAddress)
        where THost : IScreenMachine, IQueuedScreenMachine, IMachineMemoryPeek {
        for (var step = 0; (step < steps); ++step) {
            _ = host.Step(deltaTicks: budget, input: in input);
            host.PokeByte(address: scratchAddress, value: (byte)(step & 0xFF));
        }
    }

    private static byte[] SnapshotRegion<THost>(THost host, int start, int length)
        where THost : IMachineMemoryPeek {
        var region = new byte[length];

        for (var offset = 0; (offset < length); ++offset) {
            region[offset] = host.PeekByte(address: (start + offset));
        }

        return region;
    }

    /// <summary>Verifies the machine-neutral time-travel contract the <see cref="MachineTimeTravel{TInput}"/> layer owns
    /// over the queued host: rewind lands on the true past frame and a restored instant plus identical future ticks buys
    /// identical budgets (the host tick-to-cycle accumulator is restored atomically with the core); the runahead
    /// lookahead stays exactly N native frames ahead over a long mismatched-cadence horizon and under fast-forward
    /// without ever perturbing the authoritative machine; an over-cap fast-forward factor clamps instead of faulting the
    /// worker loop; and a rewind clears the abandoned future's queued host audio. The <paramref name="observe"/> delegate
    /// returns a deterministic fingerprint of the AUTHORITATIVE machine's state — a peek fold for a host that exposes a
    /// debug window, its emitted light otherwise — so both cores run the identical checks over whatever each can see.</summary>
    /// <typeparam name="THost">The queued host under test.</typeparam>
    /// <param name="withContent">Builds a fresh assigned host (no audio) with the core's synthetic content.</param>
    /// <param name="withAudio">Builds a fresh assigned host WITH an audio consumer and the core's synthetic content.</param>
    /// <param name="observe">A deterministic fingerprint of the authoritative machine's current state.</param>
    /// <returns>The contract result.</returns>
    public static QueuedHostProbeResult VerifyTimeTravel<THost>(Func<THost> withContent, Func<THost> withAudio, Func<THost, long> observe)
        where THost : IScreenMachine, IQueuedScreenMachine, ITimeTravelMachine, IAudioMachine, IFeedbackMachine {
        // A remainder-bearing per-frame budget: 60 Hz submissions against a ~59.73 Hz native cadence leave a tick→cycle
        // remainder every frame, so both the accumulator restoration (rewind) and the mismatched-cadence lead (runahead)
        // are genuinely exercised rather than hidden behind a whole-cycle budget.
        var budget = EngineTicks.PerRate(ratePerSecond: 60);

        var rewind = VerifyRewindDeterminism(withContent: withContent, observe: observe, budget: budget);

        if (rewind is not null) {
            return rewind.Value;
        }

        var runahead = VerifyRunaheadLead(withContent: withContent, observe: observe, budget: budget);

        if (runahead is not null) {
            return runahead.Value;
        }

        var cap = VerifyFastForwardCap(withContent: withContent, budget: budget);

        if (cap is not null) {
            return cap.Value;
        }

        var budgetCeiling = VerifyRewindBudgetCeiling();

        if (budgetCeiling is not null) {
            return budgetCeiling.Value;
        }

        var audio = VerifyRewindClearsAudio(withAudio: withAudio, budget: budget);

        return (audio ?? QueuedHostProbeResult.Pass(
            detail: $"rewind lands the true past frame and restores accumulator phase (identical future ticks → identical state); runahead held its {RunaheadFrames}-frame lead over {LongHorizon} mismatched-cadence frames and under x{FastForwardFactor} fast-forward without perturbing the authority; over-cap fast-forward clamped to x{MachineTimeTravel<MachinePadState>.MaxFastForwardFactor} without faulting; a below-two-span budget retained one span (over-reported footprint) and a below-one-span budget was rejected; rewind cleared the abandoned-future audio"
        ));
    }

    private const int RewindDriveFrames = 90;
    private const int RewindBackFrames = 30;
    private const int RunaheadFrames = 6;
    private const int LongHorizon = 240;
    private const int FastForwardFactor = 4;

    // A deterministic varying full-input image for frame f: cycling face/dpad buttons plus a swept tilt and light level,
    // so the recorded ring carries — and a rewind replays — the whole sensor image, not just the buttons.
    private static MachinePadState ScheduledInput(int frame) {
        var buttons = (MachineButtons)(1 << (frame % 8));
        var tilt = new System.Numerics.Vector2(x: (((frame % 5) - 2) * 0.25f), y: (((frame % 3) - 1) * 0.5f));

        return new MachinePadState(
            Buttons: buttons,
            LeftStick: default,
            RightStick: default,
            LeftTrigger: 0f,
            RightTrigger: 0f,
            Tilt: tilt,
            LightLevel: (byte)((frame * 7) & 0xFF)
        );
    }

    private static void DriveSchedule<THost>(THost host, int from, int count, ulong budget)
        where THost : IScreenMachine {
        for (var frame = from; (frame < (from + count)); ++frame) {
            var input = ScheduledInput(frame: frame);

            _ = host.Step(deltaTicks: budget, input: in input);
        }
    }

    private static QueuedHostProbeResult? VerifyRewindDeterminism<THost>(Func<THost> withContent, Func<THost, long> observe, ulong budget)
        where THost : IScreenMachine, IQueuedScreenMachine, ITimeTravelMachine {
        // Held input over remainder-bearing 60 Hz submissions: the timeline is then a pure function of the tick→cycle
        // phase alone, so restoring the wrong accumulator on a rewind is the only thing that can stop a re-driven suffix
        // from re-tracing the original tail. Fingerprint the authority after every submission so the re-drive can be
        // matched against the true recorded timeline without depending on the submission↔native-frame ratio.
        var held = MachinePadState.Neutral with { Buttons = MachineButtons.South };
        var timeline = new long[RewindDriveFrames];

        using var host = withContent();

        host.SetRewindEnabled(enabled: true);

        for (var frame = 0; (frame < RewindDriveFrames); ++frame) {
            _ = host.Step(deltaTicks: budget, input: in held);
            timeline[frame] = observe(host);
        }

        if (host.RewindBy(frames: RewindBackFrames) <= 0) {
            return QueuedHostProbeResult.Fail(detail: $"rewind captured no history after {RewindDriveFrames} recorded frames");
        }

        // Re-drive held submissions from the landed instant: with the accumulator phase restored atomically with the
        // core (H-04) and the recorded input replayed verbatim, the re-driven states re-trace the original timeline
        // exactly, so the ORIGINAL END fingerprint reappears — confirmed genuine (not a fingerprint collision) by
        // requiring the two states just before it to also match the original tail. A stale phase re-buys different
        // budgets and this three-in-a-row reconvergence never occurs.
        var probeSpan = (RewindBackFrames + 16);
        var last = timeline[RewindDriveFrames - 1];
        var prior1 = timeline[RewindDriveFrames - 2];
        var prior2 = timeline[RewindDriveFrames - 3];
        var first = 0L;
        var second = 0L;

        for (var step = 0; (step < probeSpan); ++step) {
            _ = host.Step(deltaTicks: budget, input: in held);

            var third = observe(host);

            if ((step >= 2) && (third == last) && (second == prior1) && (first == prior2)) {
                return ((host.QueueFault is { } fault) ? QueuedHostProbeResult.Fail(detail: $"the rewind host faulted: {fault}") : null);
            }

            first = second;
            second = third;
        }

        return QueuedHostProbeResult.Fail(detail: "a rewound-then-replayed run never re-traced the original timeline — the restored accumulator phase or recorded input did not reproduce the recorded budgets");
    }

    private static QueuedHostProbeResult? VerifyRunaheadLead<THost>(Func<THost> withContent, Func<THost, long> observe, ulong budget)
        where THost : IScreenMachine, IQueuedScreenMachine, ITimeTravelMachine, IFeedbackMachine {
        // Held input over a long horizon (60 Hz submissions vs the ~59.73 Hz native cadence, so the authority completes
        // a native frame only ~every submission) plus a fast-forward pass: the measured lead must stay pinned to N the
        // whole way, within one native frame. The reported lead is now the FORK's own native-frame index minus the
        // authority's (H-03) — not a synthetic per-RunFrame counter that reads N regardless of where the fork actually
        // sits — so it reports the TRUE lead. Because the layer drives the fork to its own index reaching authority+N,
        // that true lead is N, or N+1 in the instant an instruction's overshoot carries the boundary-reaching frame past
        // the target (it self-corrects to N the next submission). The bug this pins would let the lead drift without
        // bound under a mismatched cadence; the honest fork-index read holds it to exactly [N, N+1].
        var held = MachinePadState.Neutral with { Buttons = MachineButtons.South };

        foreach (var factor in (int[])[1, FastForwardFactor]) {
            using var host = withContent();
            using var baseline = withContent();

            host.SetFastForward(factor: factor);
            host.SetRunahead(frames: RunaheadFrames);

            for (var frame = 0; (frame < LongHorizon); ++frame) {
                _ = host.Step(deltaTicks: budget, input: in held);

                var status = host.TimeTravelStatus;

                // Once primed the lead holds at N (or N+1 in an overshoot instant); before priming (the first submission)
                // it reads 0.
                if (((status.RunaheadLeadFrames < RunaheadFrames) || (status.RunaheadLeadFrames > (RunaheadFrames + 1))) && (frame > 0)) {
                    return QueuedHostProbeResult.Fail(
                        detail: $"runahead lead drifted to {status.RunaheadLeadFrames} (expected {RunaheadFrames} or {RunaheadFrames + 1}) at frame {frame} under x{factor} fast-forward — the lookahead did not track the authority's native-frame delta"
                    );
                }

                if (status.FastForwardFactor != factor) {
                    return QueuedHostProbeResult.Fail(detail: $"fast-forward factor read {status.FastForwardFactor}, expected {factor}");
                }
            }

            // Runahead drives a separate fork only: the authoritative machine must be byte-identical to a baseline that
            // ran the same input with no runahead armed. Disarm runahead and take one more identical step on both so a
            // light-based observable reads the AUTHORITY's staged frame (not the lookahead's) on both sides.
            host.SetRunahead(frames: 0);
            baseline.SetFastForward(factor: factor);

            for (var frame = 0; (frame <= LongHorizon); ++frame) {
                _ = baseline.Step(deltaTicks: budget, input: in held);
            }

            _ = host.Step(deltaTicks: budget, input: in held);

            if (observe(host) != observe(baseline)) {
                return QueuedHostProbeResult.Fail(detail: $"runahead perturbed the authoritative machine under x{factor} fast-forward — it diverged from an un-runahead baseline");
            }

            if (host.QueueFault is { } fault) {
                return QueuedHostProbeResult.Fail(detail: $"the runahead host faulted under x{factor} fast-forward: {fault}");
            }
        }

        return null;
    }

    private static QueuedHostProbeResult? VerifyFastForwardCap<THost>(Func<THost> withContent, ulong budget)
        where THost : IScreenMachine, IQueuedScreenMachine, ITimeTravelMachine {
        using var host = withContent();
        var input = MachinePadState.Neutral;
        var cap = MachineTimeTravel<MachinePadState>.MaxFastForwardFactor;

        // An extreme factor must clamp to the cap (never overflow the checked per-frame multiply into the worker-loop
        // catch that stops the queue): set int.MaxValue, then step and confirm the worker kept running (H-07).
        host.SetFastForward(factor: int.MaxValue);

        if (host.TimeTravelStatus.FastForwardFactor != cap) {
            return QueuedHostProbeResult.Fail(detail: $"an int.MaxValue fast-forward factor read {host.TimeTravelStatus.FastForwardFactor}, expected the clamp to x{cap}");
        }

        _ = host.Step(deltaTicks: budget, input: in input);

        if (host.QueueFault is { } fault) {
            return QueuedHostProbeResult.Fail(detail: $"an int.MaxValue fast-forward factor faulted the worker loop instead of clamping: {fault}");
        }

        return null;
    }

    private const int BudgetProbeInterval = 8;
    private const int BudgetProbeStateBytes = 512;

    // M-07: the rewind ring must honor its memory ceiling. Driven directly against a fixed-size fake core (so perSpan is
    // known exactly), machine-neutral so both batteries prove it. Two documented behaviors: a budget below two spans but
    // holding one retains exactly ONE span (never the old forced-minimum two) and over-reports its footprint against the
    // truly retained bytes; a budget too small for even one span is rejected loudly at the first capture.
    private static QueuedHostProbeResult? VerifyRewindBudgetCeiling() {
        // TInput = byte, so PerFrameRecordBytes = (interval-1) * (32 + sizeof(byte)); perSpan = state image + that.
        var perFrameBytes = ((long)(BudgetProbeInterval - 1) * (32L + 1L));
        var perSpan = ((long)BudgetProbeStateBytes + perFrameBytes);

        // (1) 1.5 spans → a one-span ring; SegmentCount must never exceed one, and ByteFootprint must be an honest
        // over-report (>= the one-span retained bytes: the eager per-frame arrays plus a keyframe buffer at the image).
        var oneSpanBudget = (perSpan + (perSpan / 2L));
        var oneSpanCore = new FixedStateCore(stateBytes: BudgetProbeStateBytes);

        using (var oneSpanRing = new MachineTimeTravel<byte>(core: oneSpanCore, keyframeIntervalFrames: BudgetProbeInterval, memoryBudgetBytes: oneSpanBudget)) {
            oneSpanRing.SetRewindEnabled(enabled: true);

            for (var frame = 0; (frame < (BudgetProbeInterval * 4)); ++frame) {
                var input = (byte)frame;

                oneSpanCore.Advance();
                oneSpanRing.Record(input: in input, budget: 1L, hostAccumulator: 0UL);
            }

            var status = oneSpanRing.GetStatus();

            if (status.SegmentCount > 1) {
                return QueuedHostProbeResult.Fail(detail: $"a below-two-span rewind budget ({oneSpanBudget} B, perSpan {perSpan} B) retained {status.SegmentCount} spans — the forced min-2 clamp still overruns the ceiling");
            }

            if (status.ByteFootprint < perSpan) {
                return QueuedHostProbeResult.Fail(detail: $"reported ByteFootprint {status.ByteFootprint} B underreports the one-span retained lower bound {perSpan} B");
            }
        }

        // (2) A budget below a single span is an impossible configuration — the first capture must throw rather than
        // silently retain more than the ceiling.
        var impossibleCore = new FixedStateCore(stateBytes: BudgetProbeStateBytes);
        var rejected = false;

        using (var impossibleRing = new MachineTimeTravel<byte>(core: impossibleCore, keyframeIntervalFrames: BudgetProbeInterval, memoryBudgetBytes: (perSpan - 1L))) {
            impossibleRing.SetRewindEnabled(enabled: true);

            try {
                var input = (byte)0;

                impossibleCore.Advance();
                impossibleRing.Record(input: in input, budget: 1L, hostAccumulator: 0UL);
            } catch (InvalidOperationException) {
                rejected = true;
            }
        }

        return (rejected
            ? null
            : QueuedHostProbeResult.Fail(detail: $"a rewind budget below one span ({perSpan - 1L} B < {perSpan} B) was accepted instead of rejected"));
    }

    private static QueuedHostProbeResult? VerifyRewindClearsAudio<THost>(Func<THost> withAudio, ulong budget)
        where THost : IScreenMachine, IQueuedScreenMachine, ITimeTravelMachine, IAudioMachine {
        using var host = withAudio();
        var input = MachinePadState.Neutral;

        host.SetRewindEnabled(enabled: true);
        DriveSchedule(host: host, from: 0, count: RewindDriveFrames, budget: budget);

        Span<short> drain = stackalloc short[512];
        var flowing = 0;
        int written;

        do {
            written = host.ReadSamples(destination: drain);
            flowing += written;
        } while (written == drain.Length);

        if (flowing == 0) {
            return QueuedHostProbeResult.Fail(detail: "the audio host produced no samples over the drive — cannot prove the rewind cleared them");
        }

        // Refill the ring with abandoned-future audio, then rewind: the barrier must clear the host ring so no consumer
        // hears the discarded future (M-01). A fresh drain right after the rewind must therefore come back empty.
        DriveSchedule(host: host, from: RewindDriveFrames, count: RewindBackFrames, budget: budget);

        if (host.RewindBy(frames: RewindBackFrames) <= 0) {
            return QueuedHostProbeResult.Fail(detail: "rewind captured no history on the audio host");
        }

        var afterRewind = host.ReadSamples(destination: drain);

        if (afterRewind != 0) {
            return QueuedHostProbeResult.Fail(detail: $"a rewind left {afterRewind} abandoned-future audio samples in the host ring (expected a cleared ring)");
        }

        return ((host.QueueFault is { } fault) ? QueuedHostProbeResult.Fail(detail: $"the audio-rewind host faulted: {fault}") : null);
    }

    private static QueuedHostProbeResult? VerifyPublicationLease<THost>(Func<THost> withContent)
        where THost : IScreenMachine, IQueuedScreenMachine {
        using var host = withContent();
        using var upload = new BlockingSurfaceUpload();
        var gpu = new TestGpuComputeServices(factory: new TestSurfaceTransferFactory(upload));
        var device = new TestGpuDeviceContext();
        var accepted = 0L;
        var input = MachinePadState.Neutral;

        for (var index = 0; (index < host.MaximumPendingSteps); ++index) {
            var submission = host.Submit(deltaTicks: EngineTicks.PerSecond, input: in input);

            if (submission == QueuedMachineSubmission.Rejected) {
                return QueuedHostProbeResult.Fail(detail: $"healthy assigned host rejected priming segment {index + 1}");
            }

            ++accepted;
        }

        Exception? firstPublishFault = null;
        Exception? secondPublishFault = null;
        var firstPublish = new Thread(start: () => {
            try {
                host.PublishFrame(deviceContext: device, gpu: gpu);
            } catch (Exception exception) {
                firstPublishFault = exception;
            }
        });
        var secondPublish = new Thread(start: () => {
            try {
                host.PublishFrame(deviceContext: device, gpu: gpu);
            } catch (Exception exception) {
                secondPublishFault = exception;
            }
        });

        firstPublish.Start();

        if (!upload.WaitUntilEntered(timeout: OperationTimeout)) {
            upload.Release();
            firstPublish.Join();

            return QueuedHostProbeResult.Fail(detail: "first publish never entered the blocking uploader");
        }

        var completedAtLease = host.CompletedSteps;

        while ((accepted - completedAtLease) < 2L) {
            var submission = host.Submit(deltaTicks: EngineTicks.PerSecond, input: in input);

            if (submission == QueuedMachineSubmission.Rejected) {
                upload.Release();
                firstPublish.Join();

                return QueuedHostProbeResult.Fail(detail: "host rejected the additional lease-proof segment");
            }

            ++accepted;
        }

        secondPublish.Start();
        var progressed = SpinWait.SpinUntil(
            condition: () => host.CompletedSteps >= (completedAtLease + 2L),
            timeout: OperationTimeout
        );
        var serializedWhileBlocked = (upload.CallCount == 1);

        upload.Release();
        var firstJoined = firstPublish.Join(millisecondsTimeout: (int)OperationTimeout.TotalMilliseconds);
        var secondJoined = secondPublish.Join(millisecondsTimeout: (int)OperationTimeout.TotalMilliseconds);

        if (!firstJoined || !secondJoined) {
            return QueuedHostProbeResult.Fail(detail: "publish thread did not retire after the blocking uploader was released");
        }

        if (firstPublishFault is not null) {
            return QueuedHostProbeResult.Fail(detail: $"first publish faulted: {firstPublishFault.GetType().Name}: {firstPublishFault.Message}");
        }

        if (secondPublishFault is not null) {
            return QueuedHostProbeResult.Fail(detail: $"second publish faulted: {secondPublishFault.GetType().Name}: {secondPublishFault.Message}");
        }

        if (!progressed) {
            return QueuedHostProbeResult.Fail(detail: "worker could not publish two newer frames while upload was blocked");
        }

        if (upload.SourceChanged) {
            return QueuedHostProbeResult.Fail(detail: "leased upload bytes changed before Upload returned");
        }

        if (!serializedWhileBlocked || (upload.MaximumConcurrentCalls != 1)) {
            return QueuedHostProbeResult.Fail(
                detail: $"publish calls overlapped (calls={upload.CallCount}, max-concurrent={upload.MaximumConcurrentCalls})"
            );
        }

        if ((upload.CallCount != 2) || (0 == host.NativeImageViewHandle)) {
            return QueuedHostProbeResult.Fail(
                detail: $"expected two serialized changed-frame uploads and a bound view; calls={upload.CallCount}, view={host.NativeImageViewHandle}"
            );
        }

        return null;
    }

    private static QueuedHostProbeResult? VerifyDeviceLossSerialization<THost>(Func<THost> empty)
        where THost : IScreenMachine {
        using var host = empty();
        using var firstUpload = new BlockingSurfaceUpload();
        using var replacementUpload = new BlockingSurfaceUpload(blockFirstCall: false);
        var factory = new TestSurfaceTransferFactory(firstUpload, replacementUpload);
        var gpu = new TestGpuComputeServices(factory: factory);
        var device = new TestGpuDeviceContext();
        Exception? publishFault = null;
        Exception? deviceLossFault = null;
        var publish = new Thread(start: () => {
            try {
                host.PublishFrame(deviceContext: device, gpu: gpu);
            } catch (Exception exception) {
                publishFault = exception;
            }
        });
        var deviceLoss = new Thread(start: () => {
            try {
                host.NotifyDeviceLost();
            } catch (Exception exception) {
                deviceLossFault = exception;
            }
        });

        publish.Start();

        if (!firstUpload.WaitUntilEntered(timeout: OperationTimeout)) {
            firstUpload.Release();
            publish.Join();

            return QueuedHostProbeResult.Fail(detail: "device-loss proof never entered the blocking uploader");
        }

        deviceLoss.Start();
        var deviceLossBlocked = !deviceLoss.Join(millisecondsTimeout: 50);
        var disposedDuringUpload = firstUpload.DisposedDuringCall;

        firstUpload.Release();
        var publishJoined = publish.Join(millisecondsTimeout: (int)OperationTimeout.TotalMilliseconds);
        var deviceLossJoined = deviceLoss.Join(millisecondsTimeout: (int)OperationTimeout.TotalMilliseconds);

        if (!publishJoined || !deviceLossJoined) {
            return QueuedHostProbeResult.Fail(detail: "publish/device-loss serialization did not retire after upload release");
        }

        if (publishFault is not null) {
            return QueuedHostProbeResult.Fail(detail: $"device-loss-proof publish faulted: {publishFault.GetType().Name}: {publishFault.Message}");
        }

        if (deviceLossFault is not null) {
            return QueuedHostProbeResult.Fail(detail: $"device loss faulted: {deviceLossFault.GetType().Name}: {deviceLossFault.Message}");
        }

        if (!deviceLossBlocked || disposedDuringUpload || !firstUpload.IsDisposed) {
            return QueuedHostProbeResult.Fail(
                detail: $"device loss escaped upload serialization (blocked={deviceLossBlocked}, disposed-during-call={disposedDuringUpload}, disposed={firstUpload.IsDisposed})"
            );
        }

        host.PublishFrame(deviceContext: device, gpu: gpu);

        if ((replacementUpload.CallCount != 1) || (0 == host.NativeImageViewHandle)) {
            return QueuedHostProbeResult.Fail(
                detail: $"post-loss publish did not create a replacement upload (calls={replacementUpload.CallCount}, view={host.NativeImageViewHandle})"
            );
        }

        return null;
    }

    private static QueuedHostProbeResult? VerifyDisposalSerialization<THost>(Func<THost> empty)
        where THost : IScreenMachine {
        var host = empty();
        using var upload = new BlockingSurfaceUpload();
        var gpu = new TestGpuComputeServices(factory: new TestSurfaceTransferFactory(upload));
        var device = new TestGpuDeviceContext();
        Exception? publishFault = null;
        Exception? disposeFault = null;
        var publish = new Thread(start: () => {
            try {
                host.PublishFrame(deviceContext: device, gpu: gpu);
            } catch (Exception exception) {
                publishFault = exception;
            }
        });
        var dispose = new Thread(start: () => {
            try {
                host.Dispose();
            } catch (Exception exception) {
                disposeFault = exception;
            }
        });

        publish.Start();

        if (!upload.WaitUntilEntered(timeout: OperationTimeout)) {
            upload.Release();
            publish.Join();
            host.Dispose();

            return QueuedHostProbeResult.Fail(detail: "disposal proof never entered the blocking uploader");
        }

        dispose.Start();
        var disposeBlocked = !dispose.Join(millisecondsTimeout: 50);
        var disposedDuringUpload = upload.DisposedDuringCall;

        upload.Release();
        var publishJoined = publish.Join(millisecondsTimeout: (int)OperationTimeout.TotalMilliseconds);
        var disposeJoined = dispose.Join(millisecondsTimeout: (int)OperationTimeout.TotalMilliseconds);

        if (!publishJoined || !disposeJoined) {
            return QueuedHostProbeResult.Fail(detail: "publish/dispose serialization did not retire after upload release");
        }

        if (publishFault is not null) {
            return QueuedHostProbeResult.Fail(detail: $"disposal-proof publish faulted: {publishFault.GetType().Name}: {publishFault.Message}");
        }

        if (disposeFault is not null) {
            return QueuedHostProbeResult.Fail(detail: $"dispose faulted: {disposeFault.GetType().Name}: {disposeFault.Message}");
        }

        if (!disposeBlocked || disposedDuringUpload || !upload.IsDisposed) {
            return QueuedHostProbeResult.Fail(
                detail: $"upload lifetime escaped serialization (dispose-blocked={disposeBlocked}, disposed-during-call={disposedDuringUpload}, disposed={upload.IsDisposed})"
            );
        }

        return null;
    }

    // A minimal deterministic core with a fixed-size state image — just enough for the rewind budget-ceiling check to run
    // the ring at a known per-span cost. Only Record's read surface is exercised (capture length, cycle, native frame);
    // the restore/input/run/lookahead surface is never driven by that check.
    private sealed class FixedStateCore(int stateBytes) : ITimeTravelMachineCore<byte> {
        private long m_cycle;
        private long m_frame;

        public long CycleCount => m_cycle;
        public long NativeFrameIndex => m_frame;
        public ReadOnlySpan<uint> Framebuffer => default;

        // Advance one frame so successive keyframes carry distinct cycle/native-frame stamps.
        public void Advance() {
            m_cycle += 100L;
            ++m_frame;
        }

        public int CaptureState(ref byte[] buffer) {
            if (buffer.Length < stateBytes) {
                buffer = new byte[stateBytes];
            }

            return stateBytes;
        }

        public void RestoreState(byte[] buffer, int length) { }
        public void ApplyInput(in byte input) { }
        public void RunCycles(long cycles) { }
        public ITimeTravelLookahead<byte> CreateLookahead() => throw new NotSupportedException();
    }

    private sealed class BlockingSurfaceUpload(bool blockFirstCall = true) : IGpuSurfaceUpload {
        private readonly ManualResetEventSlim m_entered = new(initialState: false);
        private readonly ManualResetEventSlim m_release = new(initialState: false);
        private int m_activeCalls;
        private int m_callCount;
        private int m_disposed;
        private int m_disposedDuringCall;
        private int m_maximumConcurrentCalls;
        private int m_sourceChanged;

        public int CallCount => Volatile.Read(location: ref m_callCount);
        public bool DisposedDuringCall => (0 != Volatile.Read(location: ref m_disposedDuringCall));
        public bool IsDisposed => (0 != Volatile.Read(location: ref m_disposed));
        public int MaximumConcurrentCalls => Volatile.Read(location: ref m_maximumConcurrentCalls);
        public bool SourceChanged => (0 != Volatile.Read(location: ref m_sourceChanged));

        public nint Upload(
            IGpuDeviceContext deviceContext,
            ReadOnlyMemory<byte> pixels,
            GpuPixelFormat format,
            uint width,
            uint height
        ) {
            var call = Interlocked.Increment(location: ref m_callCount);
            var active = Interlocked.Increment(location: ref m_activeCalls);

            SetMaximumConcurrentCalls(value: active);

            try {
                if (blockFirstCall && (call == 1)) {
                    var baseline = pixels.ToArray();

                    m_entered.Set();

                    while (!m_release.Wait(millisecondsTimeout: 1)) {
                        if (!pixels.Span.SequenceEqual(other: baseline)) {
                            _ = Interlocked.Exchange(location1: ref m_sourceChanged, value: 1);
                        }
                    }

                    if (!pixels.Span.SequenceEqual(other: baseline)) {
                        _ = Interlocked.Exchange(location1: ref m_sourceChanged, value: 1);
                    }
                }

                return call;
            } finally {
                _ = Interlocked.Decrement(location: ref m_activeCalls);
            }
        }

        public bool WaitUntilEntered(TimeSpan timeout) => m_entered.Wait(timeout: timeout);
        public void Release() => m_release.Set();

        public void Dispose() {
            if (0 != Interlocked.Exchange(location1: ref m_disposed, value: 1)) {
                return;
            }

            if (0 != Volatile.Read(location: ref m_activeCalls)) {
                _ = Interlocked.Exchange(location1: ref m_disposedDuringCall, value: 1);
            }

            m_release.Set();
            m_entered.Dispose();
            m_release.Dispose();
        }

        private void SetMaximumConcurrentCalls(int value) {
            var observed = Volatile.Read(location: ref m_maximumConcurrentCalls);

            while ((observed < value) &&
                   (observed != Interlocked.CompareExchange(location1: ref m_maximumConcurrentCalls, value: value, comparand: observed))) {
                observed = Volatile.Read(location: ref m_maximumConcurrentCalls);
            }
        }
    }

    private sealed class TestGpuComputeServices(IGpuSurfaceTransferFactory factory) : IGpuComputeServices {
        public IGpuComputeCommandPoolFactory CommandPoolFactory => null!;
        public IGpuComputePipelineFactory ComputePipelineFactory => null!;
        public IGpuComputeRecorder ComputeRecorder => null!;
        public IGpuDescriptorAllocator DescriptorAllocator => null!;
        public IGpuQueueSubmitter QueueSubmitter => null!;
        public IGpuShaderModuleFactory ShaderModuleFactory => null!;
        public IGpuStorageBufferFactory StorageBufferFactory => null!;
        public IGpuStorageImageFactory StorageImageFactory => null!;
        public IGpuSurfaceTransferFactory SurfaceTransferFactory { get; } = factory;
    }

    private sealed class TestGpuDeviceContext : IGpuDeviceContext {
        public nint DeviceHandle => 1;
        public void WaitIdle() { }
    }

    private sealed class TestSurfaceTransferFactory(params IGpuSurfaceUpload[] uploads) : IGpuSurfaceTransferFactory {
        private readonly Queue<IGpuSurfaceUpload> m_uploads = new(uploads);

        public IGpuSurfaceImport CreateImport(IGpuDeviceContext deviceContext) =>
            throw new NotSupportedException();

        public IGpuSurfaceReadback CreateReadback(IGpuDeviceContext deviceContext) =>
            throw new NotSupportedException();

        public IGpuSurfaceUpload CreateUpload(IGpuDeviceContext deviceContext) {
            lock (m_uploads) {
                return m_uploads.Dequeue();
            }
        }
    }
}
