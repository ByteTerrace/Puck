using Puck.Physics.Tests.Fixtures;
using Puck.Physics.Tests.Geometry;

namespace Puck.Physics.Tests.Measurements;

/// <summary>
/// A dump of what each fixture actually does, written to the measurement file. It asserts nothing beyond the solver
/// declining nothing; the laws that DO assert read their thresholds from what this records.
/// </summary>
[Collection(MeasurementCollection.Name)]
public sealed class ProbeTests {
    [Fact]
    public void EveryFixtureRunsAndRecordsItsTrajectory() {
        MeasurementReport.Section(title: "Probe — fixture trajectories");

        ProbeCorner();
        ProbeCapsule(mode: CapsuleWitnessMode.SegmentScan);
        ProbeCapsule(mode: CapsuleWitnessMode.EndpointsOnly);
        ProbeRotatingBox();
        ProbeSpeculative(activation: FixedSpeculativeActivation.Conservative);
        ProbeSpeculative(activation: FixedSpeculativeActivation.CurrentOnly);
        ProbeDeepOverlap(recovery: true);
        ProbeDeepOverlap(recovery: false);
        ProbeBoxInCorner();
    }

    private static void ProbeCorner() {
        var world = SpikeFixtures.Corner(options: SpikeFixtures.CornerOptions(rateHz: 60, substepCount: 4));

        for (var step = 0; (step < 240); ++step) {
            world.Advance();

            if ((step < 4) || (((step + 1) % 60) == 0)) {
                MeasurementReport.Write(line: $"corner step={(step + 1)} x={MeasurementReport.Format(value: world.Pose.Center.X)} y={MeasurementReport.Format(value: world.Pose.Center.Y)} candidates={world.LastStepCandidateCount} active={world.Slots.ActiveCount} iterations={world.Solver.LastStepIterationsToConverge} refusals={world.Solver.RefusalCount}");
            }
        }

        for (var index = 0; (index < FixedManifoldSlotTable.Capacity); ++index) {
            ref readonly var slot = ref world.Slots[index];

            if (slot.Occupied) {
                MeasurementReport.Write(line: $"corner slot={index} source={slot.SourceId} feature={slot.FeatureId} normal=({MeasurementReport.Format(value: slot.Normal.X)},{MeasurementReport.Format(value: slot.Normal.Y)},{MeasurementReport.Format(value: slot.Normal.Z)}) separation={MeasurementReport.Format(value: slot.Separation)} impulse={slot.NormalImpulseRaw}");
            }
        }
    }
    private static void ProbeCapsule(CapsuleWitnessMode mode) {
        var world = SpikeFixtures.CapsuleWaist(
            options: new() { RateHz = 60, SubstepCount = 4, },
            mode: mode,
            surface: out var surface
        );

        for (var step = 0; (step < 120); ++step) {
            world.Advance();

            if ((step < 3) || (((step + 1) % 30) == 0)) {
                MeasurementReport.Write(line: $"capsule[{mode}] step={(step + 1)} y={MeasurementReport.Format(value: world.Pose.Center.Y)} candidates={world.LastStepCandidateCount} samples={world.LastStepSampleCount} endpointSeparation={MeasurementReport.Format(value: surface.LastEndpointSeparation)} witnessSeparation={MeasurementReport.Format(value: surface.LastWitnessSeparation)} refusals={world.Solver.RefusalCount}");
            }
        }
    }
    private static void ProbeRotatingBox() {
        var world = SpikeFixtures.RotatingBox(options: new() { RateHz = 60, SubstepCount = 4, });

        for (var step = 0; (step < 300); ++step) {
            world.Advance();

            if ((step < 4) || (((step + 1) % 60) == 0)) {
                MeasurementReport.Write(line: $"box step={(step + 1)} y={MeasurementReport.Format(value: world.Pose.Center.Y)} spinZ={MeasurementReport.Format(value: world.Body.AngularVelocity.Z)} vy={MeasurementReport.Format(value: world.Body.LinearVelocity.Y)} candidates={world.LastStepCandidateCount} iterations={world.Solver.LastStepIterationsToConverge} refusals={world.Solver.RefusalCount}");
            }
        }
    }
    private static void ProbeSpeculative(FixedSpeculativeActivation activation) {
        var world = SpikeFixtures.HighSpeedApproach(
            options: new() { Activation = activation, RateHz = 60, SubstepCount = 1, },
            height: 1d,
            downwardSpeed: 400d
        );

        for (var step = 0; (step < 4); ++step) {
            world.Advance();
            MeasurementReport.Write(line: $"speculative[{activation}] step={(step + 1)} y={MeasurementReport.Format(value: world.Pose.Center.Y)} vy={MeasurementReport.Format(value: world.Body.LinearVelocity.Y)} bound={MeasurementReport.Format(value: world.LastStepActivationBound)} candidates={world.LastStepCandidateCount} refusals={world.Solver.RefusalCount}");
        }
    }
    private static void ProbeDeepOverlap(bool recovery) {
        var world = SpikeFixtures.DeepOverlap(options: new() { DeepRecovery = recovery, RateHz = 60, SubstepCount = 4, });

        for (var step = 0; (step < 120); ++step) {
            world.Advance();

            if ((step < 3) || (((step + 1) % 20) == 0)) {
                MeasurementReport.Write(line: $"deep[recovery={recovery}] step={(step + 1)} y={MeasurementReport.Format(value: world.Pose.Center.Y)} vy={MeasurementReport.Format(value: world.Body.LinearVelocity.Y)} candidates={world.LastStepCandidateCount} maxImpulse={world.Solver.LastStepMaximumImpulseRaw} refusals={world.Solver.RefusalCount}");
            }
        }
    }
    private static void ProbeBoxInCorner() {
        var world = SpikeFixtures.BoxInCorner(options: SpikeFixtures.BoxInCornerOptions(rateHz: 60, substepCount: 4));

        for (var step = 0; (step < 240); ++step) {
            world.Advance();

            if ((step < 3) || (((step + 1) % 60) == 0)) {
                MeasurementReport.Write(line: $"boxCorner step={(step + 1)} x={MeasurementReport.Format(value: world.Pose.Center.X)} y={MeasurementReport.Format(value: world.Pose.Center.Y)} candidates={world.LastStepCandidateCount} active={world.Slots.ActiveCount} digest={world.Digest:X16} refusals={world.Solver.RefusalCount}");
            }
        }
    }
}
