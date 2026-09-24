using System.Diagnostics;

namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// Tier-A stage: the whole-machine savestate round-trips. Over each generated micro-ROM, the machine is booted,
/// snapshotted at a frame boundary and again mid-frame, restored, and re-run — asserting the framebuffer + register
/// recordings are bit-identical, plus the double-restore idempotence invariant (a thin wrapper over
/// <see cref="StateRoundTripProbe"/>, the same three checks the <c>--state-roundtrip</c> diagnostic runs). It is the
/// machine snapshot-round-trip stage for <c>Snapshot</c>/<c>Restore</c>;
/// any divergence is a genuine hole in the state coverage, since the core is fully deterministic. The micro-ROMs are
/// direct-boot and BIOS-independent, so this runs on the zeroed stub as well as a real BIOS.
/// </summary>
internal sealed class StateRoundTripStage : IPostStage<PostContext> {
    /// <inheritdoc/>
    public bool IsConcurrent =>
        true;
    /// <inheritdoc/>
    public string Name =>
        "state-round-trip";
    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);

        var rows = new List<PostCaseResult>(capacity: MicroRoms.Kinds.Length);

        foreach (var kind in MicroRoms.Kinds) {
            var start = Stopwatch.GetTimestamp();
            var result = StateRoundTripProbe.Run(
                bios: context.BiosImage,
                rom: MicroRoms.GenerateBytes(kind: kind)
            );

            rows.Add(item: new PostCaseResult(
                Detail: result.Detail,
                Duration: Stopwatch.GetElapsedTime(startingTimestamp: start),
                Name: $"micro:{kind}",
                Verdict: (result.Pass
                    ? PostCaseVerdict.Pass
                    : PostCaseVerdict.Mismatch)
            ));
        }

        var failures = rows.Count(predicate: static row => (row.Verdict != PostCaseVerdict.Pass));

        return ((failures == 0)
            ? PostStageOutcome.Pass(
                cases: rows,
                detail: $"all {rows.Count} micro-ROMs snapshot/restore byte-identical (frame-boundary, mid-frame, double-restore)"
            )
            : PostStageOutcome.Fail(
                cases: rows,
                detail: $"{failures}/{rows.Count} micro-ROM(s) diverged after a savestate round-trip"
            )
        );
    }
}
