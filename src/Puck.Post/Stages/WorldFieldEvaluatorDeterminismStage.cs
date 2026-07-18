using Puck.Commands;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SdfVm.Queries;
using Puck.SdfVm.Queries.Debug;

namespace Puck.Post;

/// <summary>
/// Tier-A stage that verifies <see cref="SdfFieldEvaluator"/> determinism. Three independently constructed evaluators
/// process the same fixed program and seeded point set. Their distance, material, and gradient results must have
/// identical <see cref="FixedQ4816.Value"/> bits at every point.
/// <para>
/// The comparison uses <see cref="Puck.Commands.HashTrace.FirstDivergence"/> directly. The recording-oriented
/// <see cref="DeterminismHarness"/> is not applicable because a pure field query has no command stream to replay.
/// </para>
/// </summary>
internal sealed class WorldFieldEvaluatorDeterminismStage : IPostStage {
    private const int PointCount = 500;
    private const uint Seed = 0x6E9A11EDu; // "field" — arbitrary, fixed so the point set never varies run to run.

    private static readonly FixedVector3 SampleCenter = new(X: FixedQ4816.Zero, Y: FixedQ4816.FromDouble(value: 1.0), Z: FixedQ4816.Zero);
    private static readonly FixedQ4816 SampleRadius = FixedQ4816.FromDouble(value: 6.0);

    /// <inheritdoc/>
    public string Name => "world-field-evaluator-determinism";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var program = WorldStage.BuildHeroScene();
        var points = WorldQueryDriftInstrument.GenerateSeededPoints(center: SampleCenter, radius: SampleRadius, count: PointCount, seed: (int)Seed);

        var first = EvaluateTrace(program: program, points: points);
        var second = EvaluateTrace(program: program, points: points);
        var third = EvaluateTrace(program: program, points: points);

        var firstSecondDivergence = HashTrace.FirstDivergence(left: first, right: second);

        if (firstSecondDivergence >= 0) {
            return PostStageOutcome.Fail(detail: $"a fresh SdfFieldEvaluator over the same program and points diverged from another fresh instance at sample {firstSecondDivergence}/{points.Count} — the evaluator is not deterministic");
        }

        var firstThirdDivergence = HashTrace.FirstDivergence(left: first, right: third);

        if (firstThirdDivergence >= 0) {
            return PostStageOutcome.Fail(detail: $"a THIRD fresh SdfFieldEvaluator diverged from the first two at sample {firstThirdDivergence}/{points.Count} — an intermittent non-determinism the two-run check alone would have missed");
        }

        return PostStageOutcome.Pass(detail: $"{points.Count} seeded points x 3 independently constructed SdfFieldEvaluator instances over the fixed hero scene: byte-identical distance/material/gradient bits on every run (final hash 0x{first[^1]:X16})");
    }

    // One full pass: a FRESH evaluator (never reused across passes — reuse would only prove "the cache is stable,"
    // not "construction is deterministic"), folding each point's distance/material/gradient bits into a running
    // per-point hash trace via the shared Fnv1aHash accumulator (the same primitive NeutralSim.StateHash uses).
    private static ulong[] EvaluateTrace(SdfProgram program, IReadOnlyList<WorldCoord3> points) {
        var evaluator = new SdfFieldEvaluator(program: program);
        var hashes = new ulong[points.Count];

        for (var index = 0; (index < points.Count); index++) {
            var point = points[index];
            var hash = Fnv1aHash.Create();
            var hasDistance = evaluator.TryDistance(position: point, distance: out var distance, material: out var material);

            hash.Add(value: (byte)(hasDistance ? 1 : 0));
            hash.Add(value: distance.Value);
            hash.Add(value: (uint)material);

            var hasGradient = evaluator.TryFieldGradient(position: point, gradient: out var gradient);

            hash.Add(value: (byte)(hasGradient ? 1 : 0));
            hash.Add(value: gradient.X.Value);
            hash.Add(value: gradient.Y.Value);
            hash.Add(value: gradient.Z.Value);

            hashes[index] = hash.Value;
        }

        return hashes;
    }
}
