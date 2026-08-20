using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Puck.SignedDistance.Queries.Debug;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// THE LAW: the drift instrument's baked channel measures <see cref="WorldQueryBaker"/>'s own cell indexing rather
/// than a copy of it. Its artifact is produced by <see cref="WorldQueryBaker.Bake(float, float, float, float, IEnumerable{WorldQueryTerrainInput}, IEnumerable{WorldQueryBlockerInput}, int)"/>
/// from per-cell terrain rectangles, so a height the bake writes one cell off sits where
/// <see cref="BakedWorldQuery"/> does not read it and the agreement rate leaves 1.0. An instrument that derived the
/// write index itself would move both halves of the comparison together and report agreement whatever the baker did.
/// <para>The denial is the same evaluator cross-checked against an artifact whose heights carry a one-cell fencepost;
/// the control is the instrument's own bake over the same region. The fixture's ground is a step, so a one-cell shift
/// is observable rather than absorbed by a flat field.</para>
/// </summary>
public sealed class WorldQueryDriftPlumbingLawTests {
    // A quarter-unit grid 8 cells wide and 4 deep. The step in the fixture's ground falls on the cell boundary at
    // x = 0, so every sample inside a cell shares that cell's authored height and the comparison needs no slack for
    // where inside a cell a point sits.
    private const float MaxX = 1f;
    private const float MaxZ = 0.5f;
    private const float MinX = -1f;
    private const float MinZ = -0.5f;
    private const float ProbeDown = 4f;
    private const float ProbeUp = 1f;

    private static readonly FixedQ4816 s_epsilonShell = FixedQ4816.FromDouble(value: 0.1);
    private static readonly FixedQ4816 s_probeDown = FixedQ4816.FromDouble(value: ProbeDown);
    private static readonly FixedQ4816 s_probeUp = FixedQ4816.FromDouble(value: ProbeUp);
    private static readonly FixedQ4816 s_tolerance = FixedQ4816.FromDouble(value: 0.01);

    // A ground plane at y = 0 with a slab standing proud of it over x in [0, 1], so the baked heightfield carries two
    // distinct authored heights and the boundary between them is a cell edge.
    private static SdfFieldEvaluator BuildSteppedGroundEvaluator() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder
            .Plane(
                material: material,
                normal: Vector3.UnitY,
                offset: 0f
            )
            .ResetPoint()
            .Translate(offset: new Vector3(
                x: 0.5f,
                y: 0f,
                z: 0f
            ))
            .Box(
                halfExtents: new Vector3(
                    x: 0.5f,
                    y: 0.25f,
                    z: 4f
                ),
                material: material,
                round: 0f
            );

        return new SdfFieldEvaluator(program: builder.Build());
    }
    private static WorldQueryDriftHistogram Measure(SdfFieldEvaluator evaluator, BakedWorldQuery baked) =>
        WorldQueryDriftInstrument.Evaluate(
            baked: baked,
            bakedTolerance: s_tolerance,
            epsilonShell: s_epsilonShell,
            evaluator: evaluator,
            gpuInsideOrNear: null,
            groundProbeDown: s_probeDown,
            groundProbeUp: s_probeUp,
            points: SamplePoints()
        );
    private static WorldQueryArtifact BakeArtifact(SdfFieldEvaluator evaluator) =>
        WorldQueryDriftInstrument.BakeGroundHeightArtifact(
            evaluator: evaluator,
            maxX: MaxX,
            maxZ: MaxZ,
            minX: MinX,
            minZ: MinZ,
            probeDown: ProbeDown,
            probeUp: ProbeUp
        );
    // One point per cell column at two depths, each well inside its own cell and a clear unit above the field so the
    // epsilon shell excludes none of them.
    private static IReadOnlyList<FixedPosition> SamplePoints() {
        var points = new List<FixedPosition>();

        foreach (var z in (ReadOnlySpan<double>)[-0.4, 0.1,]) {
            foreach (var x in (ReadOnlySpan<double>)[-0.9, -0.6, -0.4, -0.1, 0.1, 0.4, 0.6, 0.9,]) {
                points.Add(item: FixedPosition.FromLocal(local: new FixedVector3(
                    X: FixedQ4816.FromDouble(value: x),
                    Y: FixedQ4816.One,
                    Z: FixedQ4816.FromDouble(value: z)
                )));
            }
        }

        return points;
    }

    [Fact]
    public void TheBakedChannelAgreesWithTheEvaluatorItWasSampledFrom() {
        var evaluator = BuildSteppedGroundEvaluator();
        var artifact = BakeArtifact(evaluator: evaluator);
        var histogram = Measure(
            baked: new BakedWorldQuery(artifact: artifact),
            evaluator: evaluator
        );

        Assert.Equal(
            actual: artifact.Width,
            expected: 8
        );
        Assert.Equal(
            actual: artifact.Height,
            expected: 4
        );
        // A flat fixture would make the denial below vacuous: a shifted copy of one repeated height still agrees.
        Assert.True(
            condition: (artifact.HeightRaw.ToArray().Distinct().Count() >= 2),
            userMessage: "the fixture's baked ground is uniform, so a one-cell shift would be unobservable"
        );
        Assert.Equal(
            actual: histogram.BakedComparisons,
            expected: 16
        );
        Assert.Empty(collection: histogram.BakedDisagreements);
        Assert.Equal(
            actual: histogram.BakedAgreementRate,
            expected: 1.0
        );
    }

    [Fact]
    public void AOneCellFencepostInTheBakedHeightsShowsUpAsDisagreement() {
        var evaluator = BuildSteppedGroundEvaluator();
        var artifact = BakeArtifact(evaluator: evaluator);
        var authored = artifact.HeightRaw.ToArray();
        var shifted = new long[authored.Length];

        Array.Fill(
            array: shifted,
            value: WorldQueryArtifact.NoHeightSentinel
        );
        Array.Copy(
            destinationArray: shifted,
            destinationIndex: 1,
            length: (authored.Length - 1),
            sourceArray: authored,
            sourceIndex: 0
        );

        var histogram = Measure(
            baked: new BakedWorldQuery(artifact: new WorldQueryArtifact(
                blocked: [],
                cellSizeRaw: artifact.CellSizeRaw,
                height: artifact.Height,
                heightRaw: shifted,
                originXRaw: artifact.OriginXRaw,
                originZRaw: artifact.OriginZRaw,
                width: artifact.Width
            )),
            evaluator: evaluator
        );

        Assert.Equal(
            actual: histogram.BakedComparisons,
            expected: 16
        );
        Assert.NotEmpty(collection: histogram.BakedDisagreements);
        Assert.True(
            condition: (histogram.BakedAgreementRate < 1.0),
            userMessage: "a one-cell fencepost in the baked heights went unnoticed, so the comparison cannot fail"
        );
    }
}
