namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// Tier-A stage: a fork diverges identically. Fork a running machine (a sibling loaded from the parent's current
/// whole-machine state), then advance both the parent and the fork the same number of frames; they must reach
/// byte-identical state. This exercises the same fork seam a two-machine link co-simulation and rollback rely on — an
/// independent machine from a common point that stays in lock-step under identical input. Like <c>determinism</c>,
/// it compares the entire snapshot image, and a mismatch is localized to the diverging component and byte offset
/// via <see cref="HashDivergenceProbe.DescribeDivergence"/>.
/// </summary>
internal sealed class ForkDeterminismStage : IPostStage {
    private const int TailFrames = 200;
    private const int WarmFrames = 200;

    /// <inheritdoc/>
    public string Name =>
        "fork-determinism";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);

        using var parent = PostMachine.Build(bios: context.BiosImage, rom: SyntheticRom.Create());

        parent.RunFrames(frames: WarmFrames);

        using var fork = parent.Fork();

        parent.RunFrames(frames: TailFrames);
        fork.RunFrames(frames: TailFrames);

        var parentState = parent.Machine.Snapshot();
        var forkState = fork.Machine.Snapshot();

        if (!parentState.ContentEquals(other: forkState)) {
            return PostStageOutcome.Fail(detail: $"fork diverged from the parent after {TailFrames} frames — {HashDivergenceProbe.DescribeDivergence(a: parentState, b: forkState)}");
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

        if (ReferenceEquals(objA: forkB.Machine, objB: forkC.Machine)) {
            return PostStageOutcome.Fail(detail: "a stale fork handle parked a re-rented sibling — two later forks alias one machine");
        }

        var forkBBefore = forkB.Machine.Snapshot();

        forkC.RunFrames(frames: TailFrames);

        var forkCAfter = forkC.Machine.Snapshot();
        var forkBAfter = forkB.Machine.Snapshot();

        if (!forkBBefore.ContentEquals(other: forkBAfter)) {
            return PostStageOutcome.Fail(detail: "advancing one rented fork changed another — a stale handle aliased the sibling into two live forks");
        }

        if (forkCAfter.ContentEquals(other: forkBAfter)) {
            return PostStageOutcome.Fail(detail: "a rented fork did not diverge after advancing — suspected shared machine state");
        }

        forkB.Dispose();

        return PostStageOutcome.Pass(detail: $"parent and fork byte-identical after +{TailFrames}f from a common point ({parentState.Size} state bytes); neither an immediate nor a delayed stale double-dispose aliased two later forks");
    }
}
