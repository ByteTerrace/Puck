using Puck.Dynamics.Spike.Tests.Core;
using Puck.Dynamics.Spike.Tests.Fixtures;
using Puck.Dynamics.Spike.Tests.Geometry;

using Xunit;

namespace Puck.Dynamics.Spike.Tests;

/// <summary>
/// A dump of what each fixture actually does, written to the measurement file. It asserts nothing beyond the solver
/// declining nothing; the laws that DO assert read their thresholds from what this records.
/// </summary>
public sealed class ProbeTests {
    [Fact]
    public void EveryFixtureRunsAndRecordsItsTrajectory() {
        SpikeReport.Section(title: "Probe — fixture trajectories");

        ProbeCorner();
        ProbeCapsule(mode: CapsuleWitnessMode.SegmentScan);
        ProbeCapsule(mode: CapsuleWitnessMode.EndpointsOnly);
        ProbeRotatingBox();
        ProbeSpeculative(activation: SpeculativeActivation.Conservative);
        ProbeSpeculative(activation: SpeculativeActivation.CurrentOnly);
        ProbeDeepOverlap(recovery: true);
        ProbeDeepOverlap(recovery: false);
        ProbeBoxInCorner();
    }

    private static void ProbeCorner() {
        var world = SpikeFixtures.Corner(options: SpikeFixtures.CornerOptions(rateHz: 60, substepCount: 4));

        for (var step = 0; (step < 240); ++step) {
            world.Advance();

            if ((step < 4) || (((step + 1) % 60) == 0)) {
                SpikeReport.Write(line: $"corner step={(step + 1)} x={SpikeReport.Format(value: world.Pose.Center.X)} y={SpikeReport.Format(value: world.Pose.Center.Y)} candidates={world.LastStepCandidateCount} active={world.Slots.ActiveCount} iterations={world.Solver.LastStepIterationsToConverge} refusals={world.Solver.RefusalCount}");
            }
        }

        for (var index = 0; (index < ManifoldSlotTable.Capacity); ++index) {
            ref readonly var slot = ref world.Slots[index];

            if (slot.Occupied) {
                SpikeReport.Write(line: $"corner slot={index} source={slot.SourceId} feature={slot.FeatureId} normal=({SpikeReport.Format(value: slot.Normal.X)},{SpikeReport.Format(value: slot.Normal.Y)},{SpikeReport.Format(value: slot.Normal.Z)}) separation={SpikeReport.Format(value: slot.Separation)} impulse={slot.NormalImpulseRaw}");
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
                SpikeReport.Write(line: $"capsule[{mode}] step={(step + 1)} y={SpikeReport.Format(value: world.Pose.Center.Y)} candidates={world.LastStepCandidateCount} samples={world.LastStepSampleCount} endpointSeparation={SpikeReport.Format(value: surface.LastEndpointSeparation)} witnessSeparation={SpikeReport.Format(value: surface.LastWitnessSeparation)} refusals={world.Solver.RefusalCount}");
            }
        }
    }

    private static void ProbeRotatingBox() {
        var world = SpikeFixtures.RotatingBox(options: new() { RateHz = 60, SubstepCount = 4, });

        for (var step = 0; (step < 300); ++step) {
            world.Advance();

            if ((step < 4) || (((step + 1) % 60) == 0)) {
                SpikeReport.Write(line: $"box step={(step + 1)} y={SpikeReport.Format(value: world.Pose.Center.Y)} spinZ={SpikeReport.Format(value: world.Body.AngularVelocity.Z)} vy={SpikeReport.Format(value: world.Body.LinearVelocity.Y)} candidates={world.LastStepCandidateCount} iterations={world.Solver.LastStepIterationsToConverge} refusals={world.Solver.RefusalCount}");
            }
        }
    }

    private static void ProbeSpeculative(SpeculativeActivation activation) {
        var world = SpikeFixtures.HighSpeedApproach(
            options: new() { RateHz = 60, SubstepCount = 1, Activation = activation, },
            height: 1d,
            downwardSpeed: 400d
        );

        for (var step = 0; (step < 4); ++step) {
            world.Advance();
            SpikeReport.Write(line: $"speculative[{activation}] step={(step + 1)} y={SpikeReport.Format(value: world.Pose.Center.Y)} vy={SpikeReport.Format(value: world.Body.LinearVelocity.Y)} bound={SpikeReport.Format(value: world.LastStepActivationBound)} candidates={world.LastStepCandidateCount} refusals={world.Solver.RefusalCount}");
        }
    }

    private static void ProbeDeepOverlap(bool recovery) {
        var world = SpikeFixtures.DeepOverlap(options: new() { RateHz = 60, SubstepCount = 4, DeepRecovery = recovery, });

        for (var step = 0; (step < 120); ++step) {
            world.Advance();

            if ((step < 3) || (((step + 1) % 20) == 0)) {
                SpikeReport.Write(line: $"deep[recovery={recovery}] step={(step + 1)} y={SpikeReport.Format(value: world.Pose.Center.Y)} vy={SpikeReport.Format(value: world.Body.LinearVelocity.Y)} candidates={world.LastStepCandidateCount} maxImpulse={world.Solver.LastStepMaximumImpulseRaw} refusals={world.Solver.RefusalCount}");
            }
        }
    }

    private static void ProbeBoxInCorner() {
        var world = SpikeFixtures.BoxInCorner(options: SpikeFixtures.BoxInCornerOptions(rateHz: 60, substepCount: 4));

        for (var step = 0; (step < 240); ++step) {
            world.Advance();

            if ((step < 3) || (((step + 1) % 60) == 0)) {
                SpikeReport.Write(line: $"boxCorner step={(step + 1)} x={SpikeReport.Format(value: world.Pose.Center.X)} y={SpikeReport.Format(value: world.Pose.Center.Y)} candidates={world.LastStepCandidateCount} active={world.Slots.ActiveCount} digest={world.Digest:X16} refusals={world.Solver.RefusalCount}");
            }
        }
    }
}
