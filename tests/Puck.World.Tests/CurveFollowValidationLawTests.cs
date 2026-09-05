using Xunit;

using Puck.Assets.Documents;
using Puck.Physics.Motion;

namespace Puck.World.Tests;

/// <summary>Pins the <see cref="BodyTargetSource.CurveFollow"/> target-source validator rules — a dangling curve
/// name, an out-of-range rate, a rate-0 (resident) world, and a knot <c>tangentYaw</c> outside its canonical
/// interval — each refuse while an otherwise-identical admitting document passes — the same discriminating-pair
/// discipline <see cref="MotionShapingValidationLawTests"/> already establishes for the kit dynamics arm.</summary>
public sealed class CurveFollowValidationLawTests {
    private static WorldCurveRow StraightPath => new(
        Name: "path",
        Knots: [
            new WorldCurveKnot(Position: new DocumentVector3(x: 0f, y: 0f, z: 0f), TangentYaw: 0f, Curvature: 0f),
            new WorldCurveKnot(Position: new DocumentVector3(x: 20f, y: 0f, z: 0f), TangentYaw: 0f, Curvature: 0f),
        ],
        Closed: false
    );

    private static bool TryValidate(WorldDefinition definition, out string reason) => WorldDefinitionValidator.TryValidate(
        definition: definition,
        neighbours: null,
        reason: out reason
    );
    // Splices a "follow" Producer-kind program naming curve/rate, plus the matching kit producer parameters, onto
    // Fixtures.BuildDocument() — the shared shape every case in this file mutates exactly one field of.
    private static WorldDefinition WithFollowTarget(string curve, float rate) {
        var document = Fixtures.BuildDocument() with { CurvesRaw = [StraightPath] };
        var kit = document.Kits[0];
        var followProgram = new BodyMotionProgram(
            Name: "follow",
            Version: "puck.body-motion.v1",
            Kind: BodyProgramKind.Producer,
            Operations: [BodyMotionOp.SenseNearestInCone, BodyMotionOp.FaceSensorTarget, BodyMotionOp.ProduceSteeringIntent],
            Target: new BodyTargetSource.CurveFollow(Curve: curve, Rate: rate)
        );

        return document with {
            BodyMotionProgramsRaw = [.. document.BodyMotionPrograms, followProgram],
            KitRowsRaw = [kit with {
                ProducersRaw = new Dictionary<string, BodyProgramParameters>(collection: kit.Producers) {
                    ["follow"] = new BodyProgramParameters(
                        Scalars: new Dictionary<string, float> {
                            ["standoffRadius"] = 0.1f,
                            ["approach"] = 1f,
                            ["orbit"] = 0f,
                            ["altitudeGain"] = 0f,
                            ["approachAltitudeGain"] = 0f,
                            ["inwardGain"] = 3f,
                            ["turnScale"] = 3f,
                            ["forward"] = 0f,
                            ["softRadius"] = 1f,
                            ["weaveAmplitude"] = 0f,
                            ["weaveFrequencyBase"] = 0f,
                            ["weaveFrequencyRange"] = 0f,
                            ["activityRateBase"] = 0f,
                            ["activityRateRange"] = 0f,
                            ["strafeWave"] = 0f,
                            ["turnWave"] = 0f,
                            ["upWave"] = 0f,
                            ["pitchWave"] = 0f,
                            ["rollTurn"] = 0f,
                            ["pressThreshold"] = 0f,
                            ["altitudeBase"] = 0f,
                            ["altitudeRange"] = 0f,
                        },
                        Channels: new Dictionary<string, string>()
                    ),
                },
            }],
        };
    }

    [Fact]
    public void DanglingCurveNameRefusesWhileADeclaredRowPasses() {
        var denied = WithFollowTarget(curve: "missing", rate: 2f);
        var admitted = WithFollowTarget(curve: "path", rate: 2f);

        Assert.False(condition: TryValidate(definition: denied, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "'missing' names no curves row.");
        Assert.True(condition: TryValidate(definition: admitted, reason: out var admittedReason), userMessage: admittedReason);
    }
    [Fact]
    public void RateAboveTheCeilingRefusesWhileTheCeilingItselfPasses() {
        var denied = WithFollowTarget(curve: "path", rate: (WorldCurves.MaxFollowRate + 1f));
        var admitted = WithFollowTarget(curve: "path", rate: WorldCurves.MaxFollowRate);

        Assert.False(condition: TryValidate(definition: denied, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "target.rate");
        Assert.True(condition: TryValidate(definition: admitted, reason: out var admittedReason), userMessage: admittedReason);
    }
    [Fact]
    public void RateBelowTheNegativeCeilingRefusesWhileTheCeilingItselfPasses() {
        var denied = WithFollowTarget(curve: "path", rate: -(WorldCurves.MaxFollowRate + 1f));
        var admitted = WithFollowTarget(curve: "path", rate: -WorldCurves.MaxFollowRate);

        Assert.False(condition: TryValidate(definition: denied, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "target.rate");
        Assert.True(condition: TryValidate(definition: admitted, reason: out var admittedReason), userMessage: admittedReason);
    }
    [Fact]
    public void RateAtResidentSimulationRateRefusesWhileASteppingRatePasses() {
        var withTarget = WithFollowTarget(curve: "path", rate: 2f);

        var denied = (withTarget with { Simulation = null }); // rate-0, resident, non-stepping
        var admitted = withTarget;

        Assert.False(condition: TryValidate(definition: denied, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "cannot compile — the world authors no simulation rate (simulation.rateHz)");
        Assert.True(condition: TryValidate(definition: admitted, reason: out var admittedReason), userMessage: admittedReason);
    }
    [Fact]
    public void TangentYawOutsideTheCanonicalIntervalRefusesWhileAnInRangeValuePasses() {
        var outOfRangeRow = StraightPath with {
            Knots = [
                StraightPath.Knots[0] with { TangentYaw = (MathF.PI + 1f) },
                StraightPath.Knots[1],
            ],
        };
        var denied = (WithFollowTarget(curve: "path", rate: 2f) with { CurvesRaw = [outOfRangeRow] });
        var admitted = WithFollowTarget(curve: "path", rate: 2f);

        Assert.False(condition: TryValidate(definition: denied, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "tangentYaw");
        Assert.True(condition: TryValidate(definition: admitted, reason: out var admittedReason), userMessage: admittedReason);
    }
}
