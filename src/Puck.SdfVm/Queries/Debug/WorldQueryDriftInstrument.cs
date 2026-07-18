using Puck.Maths;

namespace Puck.SdfVm.Queries.Debug;

/// <summary>
/// One point the drift instrument sampled and could not classify in agreement — kept for diagnostics (a failing
/// Post stage prints the first handful so a regression is debuggable from the console line alone, not just a rate).
/// </summary>
/// <param name="Position">The sampled world position.</param>
/// <param name="EvaluatorDistance">The <see cref="SdfFieldEvaluator"/>'s signed distance at <see cref="Position"/>.</param>
/// <param name="Channel">Which comparison disagreed (<c>"gpu"</c> or <c>"baked"</c>) — a sample can appear in both lists.</param>
public readonly record struct WorldQueryDriftSample(WorldCoord3 Position, FixedQ4816 EvaluatorDistance, string Channel);

/// <summary>
/// The measured outcome of one <see cref="WorldQueryDriftInstrument.Evaluate"/> run: how many of the sampled points
/// agreed between the evaluator and each independent channel it was checked against. Both rates are 1.0 when the
/// corresponding channel was not supplied (nothing to disagree with is vacuously an agreement, so a caller that
/// passes no GPU delegate never trips a threshold guarding <see cref="GpuSignAgreementRate"/>).
/// </summary>
/// <param name="SampleCount">The total number of points the instrument was given.</param>
/// <param name="EpsilonShell">The exclusion half-width applied around the zero set: a point whose
/// <c>|EvaluatorDistance|</c> is at or under this is excluded from BOTH comparisons (see the type remarks on why a
/// near-surface point cannot be a fair sign test for any coarser representation).</param>
/// <param name="ExcludedByEpsilonShell">How many of <see cref="SampleCount"/> fell inside <see cref="EpsilonShell"/>
/// and were excluded.</param>
/// <param name="GpuComparisons">How many points were actually compared against the GPU delegate (<see cref="SampleCount"/>
/// minus the excluded points, or 0 when no delegate was supplied).</param>
/// <param name="GpuAgreements">How many of <see cref="GpuComparisons"/> agreed.</param>
/// <param name="BakedComparisons">How many points were compared against the baked cross-check.</param>
/// <param name="BakedAgreements">How many of <see cref="BakedComparisons"/> agreed.</param>
/// <param name="GpuDisagreements">The points that disagreed with the GPU delegate (diagnostics; not exhaustive — see
/// <see cref="WorldQueryDriftInstrument.MaxRecordedDisagreements"/>).</param>
/// <param name="BakedDisagreements">The points that disagreed with the baked cross-check (diagnostics).</param>
public sealed record WorldQueryDriftHistogram(
    int SampleCount,
    FixedQ4816 EpsilonShell,
    int ExcludedByEpsilonShell,
    int GpuComparisons,
    int GpuAgreements,
    int BakedComparisons,
    int BakedAgreements,
    IReadOnlyList<WorldQueryDriftSample> GpuDisagreements,
    IReadOnlyList<WorldQueryDriftSample> BakedDisagreements
) {
    /// <summary>The fraction of <see cref="GpuComparisons"/> that agreed, in <c>[0, 1]</c>; <c>1.0</c> when no
    /// comparison was made (nothing to disagree with).</summary>
    public double GpuSignAgreementRate => ((GpuComparisons == 0) ? 1.0 : ((double)GpuAgreements / GpuComparisons));

    /// <summary>The fraction of <see cref="BakedComparisons"/> that agreed, in <c>[0, 1]</c>; <c>1.0</c> when no
    /// comparison was made.</summary>
    public double BakedAgreementRate => ((BakedComparisons == 0) ? 1.0 : ((double)BakedAgreements / BakedComparisons));
}

/// <summary>
/// Measures how much a <see cref="SdfFieldEvaluator"/>'s answer drifts from two INDEPENDENT representations of the
/// same program: a GPU render of the live scene (the ground-truth channel — a different codebase, a different
/// numeric domain, a different algorithm entirely) and a <see cref="BakedWorldQuery"/> artifact baked from the
/// evaluator's own samples (the consistency channel — proves the query PLUMBING, not the field math, since the
/// artifact is sourced from the evaluator itself; see <see cref="BakeGroundHeightArtifact"/>'s remarks).
/// <para>
/// THE EPSILON-SHELL EXCLUSION (why every comparison here is a SIGN test, not a magnitude test): a point sitting
/// within a thin band of the true surface is not a fair test for ANY coarser representation. The evaluator resolves
/// distance to Q48.16's raw floor; a GPU sphere-trace resolves a "how close" question to whatever its footprint-
/// adaptive hit threshold and 8-bit dithered depth channel can distinguish (world units per LSB, not raw ticks); a
/// baked artifact resolves position to whole grid cells (<see cref="WorldQueryBaker.CellSize"/> world units). Asking
/// three representations at three different resolutions to agree on which side of a razor-thin band a point falls
/// would fail even for CORRECT code — the failure mode this instrument exists to catch (a wrong sign, a swapped
/// inside/outside, a missing region) is gross and shows up far outside that band. <see cref="Evaluate"/> therefore
/// excludes any point with <c>|evaluator distance| &lt;= epsilonShell</c> from both comparisons. The caller chooses the
/// shell width appropriate to the representations being compared.
/// </para>
/// </summary>
public static class WorldQueryDriftInstrument {
    /// <summary>The maximum disagreeing samples <see cref="Evaluate"/> records per channel — enough to debug a
    /// regression from the console line without the histogram growing unboundedly on a badly broken run.</summary>
    public const int MaxRecordedDisagreements = 16;

    /// <summary>Generates <paramref name="count"/> pseudo-random points inside the sphere of <paramref name="radius"/>
    /// centered at <paramref name="center"/>. Rejection sampling produces a uniform distribution over the ball's
    /// volume. The same seed produces the same diagnostic sample sequence within a process. This method is intended
    /// for debug and calibration work, not simulation state.</summary>
    /// <param name="center">The sampling sphere's center.</param>
    /// <param name="radius">The sampling sphere's radius (world units).</param>
    /// <param name="count">How many points to generate.</param>
    /// <param name="seed">The PRNG seed.</param>
    /// <returns>The generated points, at cell (0,0,0) (<see cref="WorldCoord3.FromLocal"/>).</returns>
    public static IReadOnlyList<WorldCoord3> GenerateSeededPoints(FixedVector3 center, FixedQ4816 radius, int count, int seed) {
        var random = new Random(Seed: seed);
        var points = new List<WorldCoord3>(capacity: count);
        var radiusDouble = (double)radius;

        while (points.Count < count) {
            var x = ((random.NextDouble() * 2d) - 1d);
            var y = ((random.NextDouble() * 2d) - 1d);
            var z = ((random.NextDouble() * 2d) - 1d);

            if ((((x * x) + (y * y)) + (z * z)) > 1d) {
                continue; // outside the unit ball — reject (keeps the distribution uniform over the ball's volume)
            }

            var offset = new FixedVector3(
                X: FixedQ4816.FromDouble(value: (x * radiusDouble)),
                Y: FixedQ4816.FromDouble(value: (y * radiusDouble)),
                Z: FixedQ4816.FromDouble(value: (z * radiusDouble))
            );

            points.Add(item: WorldCoord3.FromLocal(local: (center + offset)));
        }

        return points;
    }

    /// <summary>Bakes a <see cref="WorldQueryArtifact"/> heightfield FROM <paramref name="evaluator"/>'s own
    /// <see cref="SdfFieldEvaluator.TryGroundHeight"/> answers over a grid of cell centers covering
    /// <c>[minX,maxX] x [minZ,maxZ]</c> — the "evaluator-vs-baked" cross-check's baked half. Because the artifact is
    /// SOURCED from the evaluator, this cannot catch a systematic evaluator bug (that is the GPU channel's job); it
    /// catches a divergence in the QUERY PLUMBING — <see cref="WorldQueryBaker"/>'s grid indexing/quantization or
    /// <see cref="BakedWorldQuery"/>'s cell lookup disagreeing with the live evaluator for the SAME underlying
    /// geometry.</summary>
    /// <param name="evaluator">The evaluator to sample.</param>
    /// <param name="minX">The grid's minimum X bound.</param>
    /// <param name="minZ">The grid's minimum Z bound.</param>
    /// <param name="maxX">The grid's maximum X bound.</param>
    /// <param name="maxZ">The grid's maximum Z bound.</param>
    /// <param name="probeUp">How far above Y=0 the ground probe searches.</param>
    /// <param name="probeDown">How far below Y=0 the ground probe searches.</param>
    /// <returns>The baked artifact.</returns>
    public static WorldQueryArtifact BakeGroundHeightArtifact(SdfFieldEvaluator evaluator, float minX, float minZ, float maxX, float maxZ, float probeUp, float probeDown) {
        ArgumentNullException.ThrowIfNull(argument: evaluator);

        var terrain = new List<WorldQueryTerrainInput>();
        var up = FixedQ4816.FromDouble(value: probeUp);
        var down = FixedQ4816.FromDouble(value: probeDown);
        var cellSize = WorldQueryBaker.CellSize;

        for (var z = minZ; (z < maxZ); z += cellSize) {
            var cellMinZ = z;
            var cellMaxZ = (z + cellSize);
            var cellCenterZ = (z + (cellSize * 0.5f));

            for (var x = minX; (x < maxX); x += cellSize) {
                var cellMinX = x;
                var cellMaxX = (x + cellSize);
                var cellCenterX = (x + (cellSize * 0.5f));
                var probeOrigin = WorldCoord3.FromLocal(local: new FixedVector3(
                    X: FixedQ4816.FromDouble(value: cellCenterX),
                    Y: FixedQ4816.Zero,
                    Z: FixedQ4816.FromDouble(value: cellCenterZ)
                ));

                if (!evaluator.TryGroundHeight(position: probeOrigin, probeUp: up, probeDown: down, groundY: out var groundY)) {
                    continue; // no surface under this cell — leave it un-authored (WorldQueryArtifact.NoHeightSentinel)
                }

                terrain.Add(item: new WorldQueryTerrainInput(MinX: cellMinX, MinZ: cellMinZ, MaxX: cellMaxX, MaxZ: cellMaxZ, TopY: (float)(double)groundY));
            }
        }

        return WorldQueryBaker.Bake(minX: minX, minZ: minZ, maxX: maxX, maxZ: maxZ, terrain: terrain, blockers: []);
    }

    /// <summary>Runs the drift comparison over <paramref name="points"/>: excludes anything inside
    /// <paramref name="epsilonShell"/> of the evaluator's own zero set, then compares the SIGN each remaining point
    /// resolves to against <paramref name="gpuInsideOrNear"/> (when supplied) and the ground-height agreement
    /// against <paramref name="baked"/> (when supplied). Both channels are independent and optional — a caller
    /// wanting only the GPU channel passes <see langword="null"/> for <paramref name="baked"/>, and vice versa.</summary>
    /// <param name="evaluator">The evaluator under test.</param>
    /// <param name="points">The sample points (see <see cref="GenerateSeededPoints"/>).</param>
    /// <param name="epsilonShell">The exclusion half-width (see the type remarks).</param>
    /// <param name="gpuInsideOrNear">Resolves whether the GPU channel classifies a point as "inside or within its own
    /// resolution floor of the surface" (<see langword="true"/>) or "outside" (<see langword="false"/>) — see the
    /// Post stage's <c>world-field-drift</c> doc comment for how this is derived from a render readback.
    /// <see langword="null"/> skips the GPU comparison entirely.</param>
    /// <param name="baked">A baked artifact covering the same program (see <see cref="BakeGroundHeightArtifact"/>);
    /// <see langword="null"/> skips the baked comparison.</param>
    /// <param name="groundProbeUp">How far above each point's Y the baked comparison's ground probe searches.</param>
    /// <param name="groundProbeDown">How far below each point's Y the baked comparison's ground probe searches.</param>
    /// <param name="bakedTolerance">How far apart the evaluator's and the baked artifact's ground heights may be and
    /// still count as agreement — must be at least <see cref="WorldQueryBaker.CellSize"/>'s quantization slack.</param>
    /// <returns>The measured histogram.</returns>
    public static WorldQueryDriftHistogram Evaluate(
        SdfFieldEvaluator evaluator,
        IReadOnlyList<WorldCoord3> points,
        FixedQ4816 epsilonShell,
        Func<WorldCoord3, bool>? gpuInsideOrNear,
        BakedWorldQuery? baked,
        FixedQ4816 groundProbeUp,
        FixedQ4816 groundProbeDown,
        FixedQ4816 bakedTolerance
    ) {
        ArgumentNullException.ThrowIfNull(argument: evaluator);
        ArgumentNullException.ThrowIfNull(argument: points);

        var excluded = 0;
        var gpuComparisons = 0;
        var gpuAgreements = 0;
        var bakedComparisons = 0;
        var bakedAgreements = 0;
        var gpuDisagreements = new List<WorldQueryDriftSample>();
        var bakedDisagreements = new List<WorldQueryDriftSample>();

        for (var index = 0; (index < points.Count); index++) {
            var point = points[index];

            if (!evaluator.TryDistance(position: point, distance: out var distance, material: out _)) {
                continue; // a shape-less program answers nothing to compare — not a sample this instrument can use
            }

            var withinShell = (FixedQ4816.Abs(value: distance) <= epsilonShell);

            if (withinShell) {
                excluded++;
            } else if (gpuInsideOrNear is not null) {
                var evaluatorInside = (distance < FixedQ4816.Zero);
                var gpuInside = gpuInsideOrNear(arg: point);

                gpuComparisons++;

                if (evaluatorInside == gpuInside) {
                    gpuAgreements++;
                } else if (gpuDisagreements.Count < MaxRecordedDisagreements) {
                    gpuDisagreements.Add(item: new WorldQueryDriftSample(Position: point, EvaluatorDistance: distance, Channel: "gpu"));
                }
            }

            if (baked is not null) {
                var evaluatorHasGround = evaluator.TryGroundHeight(position: point, probeUp: groundProbeUp, probeDown: groundProbeDown, groundY: out var evaluatorGround);
                var bakedHasGround = baked.TryGroundHeight(position: point, probeUp: groundProbeUp, probeDown: groundProbeDown, groundY: out var bakedGround);

                bakedComparisons++;

                var agree = ((evaluatorHasGround == bakedHasGround) &&
                    (!evaluatorHasGround || (FixedQ4816.Abs(value: (evaluatorGround - bakedGround)) <= bakedTolerance)));

                if (agree) {
                    bakedAgreements++;
                } else if (bakedDisagreements.Count < MaxRecordedDisagreements) {
                    bakedDisagreements.Add(item: new WorldQueryDriftSample(Position: point, EvaluatorDistance: distance, Channel: "baked"));
                }
            }
        }

        return new WorldQueryDriftHistogram(
            BakedAgreements: bakedAgreements,
            BakedComparisons: bakedComparisons,
            BakedDisagreements: bakedDisagreements,
            EpsilonShell: epsilonShell,
            ExcludedByEpsilonShell: excluded,
            GpuAgreements: gpuAgreements,
            GpuComparisons: gpuComparisons,
            GpuDisagreements: gpuDisagreements,
            SampleCount: points.Count
        );
    }
}
