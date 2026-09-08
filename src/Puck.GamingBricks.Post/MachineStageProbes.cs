using System.Diagnostics;

namespace Puck.GamingBricks.Post;

/// <summary>The machine-neutral bodies of the Tier-A stages every machine family's battery runs — zero-allocation,
/// throughput, two-machine determinism, and the fork lifecycle. A battery's stage supplies only its own machine
/// construction, frame stepping, snapshot capture, and per-family budgets; the checks and their reported details are
/// identical across families.</summary>
public static class MachineStageProbes {
    /// <summary>Measures raw throughput: warm-up, then a stopwatch over a fixed frame span, reported as frames per
    /// second, the multiple of real time, and millions of cycles per second. Always passes — a measurement, not a
    /// gate.</summary>
    /// <typeparam name="TMachine">The battery's machine handle type.</typeparam>
    /// <param name="build">Builds the machine under measurement.</param>
    /// <param name="runFrames">Advances a machine by a whole number of frames.</param>
    /// <param name="warmFrames">The warm-up frame count.</param>
    /// <param name="benchFrames">The measured frame count.</param>
    /// <param name="hardwareFps">The family's hardware refresh rate, for the realtime multiple.</param>
    /// <param name="cyclesPerFrame">The family's cycles per frame, for the cycle rate.</param>
    /// <param name="cycleUnit">The family's cycle-rate unit label (e.g. <c>"MT/s"</c>).</param>
    /// <returns>The passing outcome carrying the measurement.</returns>
    public static PostStageOutcome MeasureThroughput<TMachine>(
        Func<TMachine> build,
        Action<TMachine, int> runFrames,
        int warmFrames,
        int benchFrames,
        double hardwareFps,
        int cyclesPerFrame,
        string cycleUnit
    ) where TMachine : IDisposable {
        using var machine = build();

        runFrames(
            arg1: machine,
            arg2: warmFrames
        );

        var stopwatch = Stopwatch.StartNew();

        runFrames(
            arg1: machine,
            arg2: benchFrames
        );
        stopwatch.Stop();

        var fps = (benchFrames / stopwatch.Elapsed.TotalSeconds);
        var realtimeMultiple = (fps / hardwareFps);
        var megaCyclesPerSecond = ((fps * cyclesPerFrame) / 1e6);

        return PostStageOutcome.Pass(detail: $"{fps:F0} fps ({realtimeMultiple:F1}x realtime, {megaCyclesPerSecond:F1} {cycleUnit}) over {benchFrames} frames");
    }
    /// <summary>Verifies the machine is deterministic: two independently-built machines advanced the same number of
    /// frames must reach byte-identical whole-machine state, with a mismatch localized via
    /// <paramref name="describeDivergence"/>.</summary>
    /// <typeparam name="TMachine">The battery's machine handle type.</typeparam>
    /// <typeparam name="TSnapshot">The family's snapshot type.</typeparam>
    /// <typeparam name="TIdentity">The family's machine identity type.</typeparam>
    /// <typeparam name="TClock">The family's captured clock type.</typeparam>
    /// <param name="build">Builds one machine; called twice, and must yield identically-configured machines.</param>
    /// <param name="runFrames">Advances a machine by a whole number of frames.</param>
    /// <param name="snapshot">Captures a machine's whole-machine snapshot.</param>
    /// <param name="frames">The frame count both machines run.</param>
    /// <param name="describeDivergence">Renders the family's one-line component/offset localization.</param>
    /// <returns>The outcome.</returns>
    public static PostStageOutcome VerifyDeterminism<TMachine, TSnapshot, TIdentity, TClock>(
        Func<TMachine> build,
        Action<TMachine, int> runFrames,
        Func<TMachine, TSnapshot> snapshot,
        int frames,
        Func<TSnapshot, TSnapshot, string> describeDivergence
    )
        where TMachine : IDisposable
        where TSnapshot : MachineSnapshot<TSnapshot, TIdentity, TClock>
        where TIdentity : IEquatable<TIdentity>
        where TClock : IEquatable<TClock> {
        using var first = build();
        using var second = build();

        runFrames(
            arg1: first,
            arg2: frames
        );
        runFrames(
            arg1: second,
            arg2: frames
        );

        var firstSnapshot = snapshot(arg: first);
        var secondSnapshot = snapshot(arg: second);

        return (firstSnapshot.ContentEquals(other: secondSnapshot)
            ? PostStageOutcome.Pass(detail: $"two independent machines byte-identical after {frames} frames ({firstSnapshot.Size} state bytes)")
            : PostStageOutcome.Fail(detail: $"two independent machines diverged after {frames} frames — {describeDivergence(
            arg1: firstSnapshot,
            arg2: secondSnapshot
        )}"));
    }
    /// <summary>Verifies the fork seam: a fork advanced in lock-step with its parent stays byte-identical, and no stale
    /// fork handle — an immediate double dispose or the delayed stale dispose of a re-rented sibling — can alias two
    /// later forks onto one machine.</summary>
    /// <typeparam name="TMachine">The whole-machine driver type.</typeparam>
    /// <typeparam name="TConfiguration">The per-machine configuration type.</typeparam>
    /// <typeparam name="TSnapshot">The family's snapshot type.</typeparam>
    /// <typeparam name="TIdentity">The family's machine identity type.</typeparam>
    /// <typeparam name="TClock">The family's captured clock type.</typeparam>
    /// <param name="parent">The freshly-built (unwarmed) root machine instance. The caller owns and disposes it.</param>
    /// <param name="runFrames">Advances a machine driver by a whole number of frames.</param>
    /// <param name="snapshot">Captures a machine driver's whole-machine snapshot.</param>
    /// <param name="warmFrames">The frames the parent runs before the fork is taken.</param>
    /// <param name="tailFrames">The frames the parent and each fork run from the common point.</param>
    /// <param name="describeDivergence">Renders the family's one-line component/offset localization.</param>
    /// <returns>The outcome.</returns>
    public static PostStageOutcome VerifyForkLifecycle<TMachine, TConfiguration, TSnapshot, TIdentity, TClock>(
        MachineInstance<TMachine, TConfiguration> parent,
        Action<TMachine, int> runFrames,
        Func<TMachine, TSnapshot> snapshot,
        int warmFrames,
        int tailFrames,
        Func<TSnapshot, TSnapshot, string> describeDivergence
    )
        where TMachine : class, ISnapshotableMachine
        where TConfiguration : notnull
        where TSnapshot : MachineSnapshot<TSnapshot, TIdentity, TClock>
        where TIdentity : IEquatable<TIdentity>
        where TClock : IEquatable<TClock> {
        runFrames(
            arg1: parent.Machine,
            arg2: warmFrames
        );

        using var fork = parent.Fork();

        runFrames(
            arg1: parent.Machine,
            arg2: tailFrames
        );
        runFrames(
            arg1: fork.Machine,
            arg2: tailFrames
        );

        var parentState = snapshot(arg: parent.Machine);
        var forkState = snapshot(arg: fork.Machine);

        if (!parentState.ContentEquals(other: forkState)) {
            return PostStageOutcome.Fail(detail: $"fork diverged from the parent after {tailFrames} frames — {describeDivergence(
                arg1: parentState,
                arg2: forkState
            )}");
        }

        // H-06: no stale fork handle can return a later rental. Two sequences must both stay safe:
        //   (1) immediate double dispose — dispose one fork twice; the second is an idempotent no-op.
        //   (2) delayed stale dispose (the ABA hole) — rent A, dispose A (parks the pooled sibling), rent B (re-arms that
        //       SAME sibling under a fresh generation), then dispose the STALE A handle again, then rent C. A must not
        //       park the sibling B now owns, so C must not alias B.
        var recycled = parent.Fork();

        recycled.Dispose();
        recycled.Dispose();

        var staleA = parent.Fork();

        staleA.Dispose();

        var forkB = parent.Fork();

        staleA.Dispose(); // the delayed stale dispose — must be inert now that B re-rented the sibling

        using var forkC = parent.Fork();

        if (ReferenceEquals(
            objA: forkB.Machine,
            objB: forkC.Machine
        )) {
            return PostStageOutcome.Fail(detail: "a stale fork handle parked a re-rented sibling — two later forks alias one machine");
        }

        var forkBBefore = snapshot(arg: forkB.Machine);

        runFrames(
            arg1: forkC.Machine,
            arg2: tailFrames
        );

        var forkCAfter = snapshot(arg: forkC.Machine);
        var forkBAfter = snapshot(arg: forkB.Machine);

        if (!forkBBefore.ContentEquals(other: forkBAfter)) {
            return PostStageOutcome.Fail(detail: "advancing one rented fork changed another — a stale handle aliased the sibling into two live forks");
        }

        if (forkCAfter.ContentEquals(other: forkBAfter)) {
            return PostStageOutcome.Fail(detail: "a rented fork did not diverge after advancing — suspected shared machine state");
        }

        forkB.Dispose();

        return PostStageOutcome.Pass(detail: $"parent and fork byte-identical after +{tailFrames}f from a common point ({parentState.Size} state bytes); neither an immediate nor a delayed stale double-dispose aliased two later forks");
    }
    /// <summary>Verifies the per-frame hot loop is allocation-free: warm up, take a
    /// <see cref="GC.GetAllocatedBytesForCurrentThread()"/> baseline, advance a further span of frames, and assert the
    /// delta is exactly zero.</summary>
    /// <typeparam name="TMachine">The battery's machine handle type.</typeparam>
    /// <param name="build">Builds the machine under measurement.</param>
    /// <param name="runFrames">Advances a machine by a whole number of frames. Must not allocate per call.</param>
    /// <param name="warmFrames">The warm-up frame count.</param>
    /// <param name="measureFrames">The measured frame count.</param>
    /// <returns>The outcome.</returns>
    public static PostStageOutcome VerifyZeroAllocation<TMachine>(
        Func<TMachine> build,
        Action<TMachine, int> runFrames,
        int warmFrames,
        int measureFrames
    ) where TMachine : IDisposable {
        using var machine = build();

        runFrames(
            arg1: machine,
            arg2: warmFrames
        );

        var before = GC.GetAllocatedBytesForCurrentThread();

        runFrames(
            arg1: machine,
            arg2: measureFrames
        );

        var delta = (GC.GetAllocatedBytesForCurrentThread() - before);

        return ((delta == 0)
            ? PostStageOutcome.Pass(detail: $"0 B allocated over {measureFrames} frames after {warmFrames}-frame warm-up")
            : PostStageOutcome.Fail(detail: $"{delta:N0} B allocated over {measureFrames} frames after {warmFrames}-frame warm-up (expected 0)"));
    }
}
