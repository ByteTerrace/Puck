using Puck.Physics.Motion;

namespace Puck.Physics.Tests;

/// <summary>
/// The body motion program compiler's laws, driven only through its public surface: an authored operation list is a
/// SET whose execution order the compiler owns, admission is closed per program kind, and every malformed shape is
/// refused by a named category rather than compiled into something that misbehaves at tick time.
/// </summary>
public sealed class BodyMotionProgramCompilerLawTests {
    // The two admissible partitions, declared rather than derived: a new opcode has to be assigned a side here, and
    // an opcode that is neither (a trigger-effect operation) has to be absent from both. Compiling the whole
    // vocabulary below turns any omission into a failure instead of a silently uncovered opcode.
    private static readonly BodyMotionOp[] MotionSelectable = [
        BodyMotionOp.ResolveYawAttitudeAndPlanarFrame,
        BodyMotionOp.IntegrateLocalAttitude,
        BodyMotionOp.ComputePlanarTargetVelocity,
        BodyMotionOp.ComputeLocalTargetVelocity,
        BodyMotionOp.ComputeSwimTargetVelocity,
        BodyMotionOp.ShapePlanarVelocity,
        BodyMotionOp.SnapYawToPlanarIntent,
        BodyMotionOp.ResolveVehicleFrame,
        BodyMotionOp.ShapeVehicleVelocity,
        BodyMotionOp.RunActionTriggers,
        BodyMotionOp.ApplyVerticalGravity,
        BodyMotionOp.ApplyVerticalDecay,
        BodyMotionOp.ApplyBuoyancyAndSurface,
        BodyMotionOp.ApplyVerticalDrive,
        BodyMotionOp.IntegratePlanarAndVerticalVelocity,
        BodyMotionOp.IntegrateScratchVelocity,
        BodyMotionOp.CommitPose,
    ];
    private static readonly BodyMotionOp[] ProducerSelectable = [
        BodyMotionOp.SenseNearestInCone,
        BodyMotionOp.ProduceWanderIntent,
        BodyMotionOp.ProduceAttendIntent,
        BodyMotionOp.FaceSensorTarget,
    ];

    private static CompiledBodyMotionProgram Compile(BodyProgramKind kind, params BodyMotionOp[] operations) => CompiledBodyMotionProgram.Compile(
        name: "law",
        version: CompiledBodyMotionProgram.SupportedVersion,
        kind: kind,
        operations: operations
    );
    private static BodyMotionProgramRefusal? Refusal(BodyProgramKind kind, params BodyMotionOp[] operations) {
        try {
            _ = Compile(
                kind: kind,
                operations: operations
            );

            return null;
        } catch (BodyMotionProgramException exception) {
            return exception.Refusal;
        }
    }

    [Fact]
    public void AdmissionPartitionsTheSelectableVocabularyByProgramKind() {
        foreach (var op in Enum.GetValues<BodyMotionOp>()) {
            var motion = Refusal(
                kind: BodyProgramKind.Motion,
                operations: op
            );
            var producer = Refusal(
                kind: BodyProgramKind.Producer,
                operations: op
            );

            Assert.Equal(
                expected: (MotionSelectable.Contains(value: op)
                    ? null
                    : BodyMotionProgramRefusal.OpcodeInadmissible
                ),
                actual: motion
            );
            Assert.Equal(
                expected: (ProducerSelectable.Contains(value: op)
                    ? null
                    : BodyMotionProgramRefusal.OpcodeInadmissible
                ),
                actual: producer
            );
            // No opcode may sit on both sides: a program's registers are decided by its kind, so an operation
            // reachable from either kind would mean the mask decides nothing.
            Assert.False(condition: (MotionSelectable.Contains(value: op) && ProducerSelectable.Contains(value: op)));
        }
    }
    [Fact]
    public void CompiledProgramAdmitsExactlyItsOwnSelectableOperations() {
        var motion = Compile(
            kind: BodyProgramKind.Motion,
            operations: MotionSelectable
        );
        var producer = Compile(
            kind: BodyProgramKind.Producer,
            operations: ProducerSelectable
        );

        foreach (var op in MotionSelectable) {
            Assert.True(condition: motion.Admits(operation: op));
            Assert.True(condition: motion.Contains(operation: op));
            Assert.False(condition: producer.Contains(operation: op));
        }
        foreach (var op in ProducerSelectable) {
            Assert.True(condition: producer.Admits(operation: op));
            Assert.True(condition: producer.Contains(operation: op));
            Assert.False(condition: motion.Contains(operation: op));
        }
    }
    [Fact]
    public void EverySelectedOperationLandsInExactlyOnePhase() {
        foreach (var (kind, selectable) in new[] { (BodyProgramKind.Motion, MotionSelectable), (BodyProgramKind.Producer, ProducerSelectable), }) {
            var program = Compile(
                kind: kind,
                operations: selectable
            );
            var placed = program.Phases.SelectMany(selector: phase => phase).ToArray();

            Assert.Equal(
                expected: selectable.Length,
                actual: placed.Length
            );
            Assert.Equal(
                expected: selectable.Order().ToArray(),
                actual: placed.Order().ToArray()
            );
        }
    }
    [Fact]
    public void MalformedShapesAreRefusedByCategory() {
        Assert.Equal(
            expected: BodyMotionProgramRefusal.NameMissing,
            actual: Assert.Throws<BodyMotionProgramException>(testCode: () => CompiledBodyMotionProgram.Compile(
                name: "  ",
                version: CompiledBodyMotionProgram.SupportedVersion,
                kind: BodyProgramKind.Motion,
                operations: [BodyMotionOp.CommitPose]
            )).Refusal
        );
        Assert.Equal(
            expected: BodyMotionProgramRefusal.VersionUnsupported,
            actual: Assert.Throws<BodyMotionProgramException>(testCode: () => CompiledBodyMotionProgram.Compile(
                name: "law",
                version: "puck.body-motion.v0",
                kind: BodyProgramKind.Motion,
                operations: [BodyMotionOp.CommitPose]
            )).Refusal
        );
        Assert.Equal(
            expected: BodyMotionProgramRefusal.ProgramKindUnknown,
            actual: Assert.Throws<BodyMotionProgramException>(testCode: () => CompiledBodyMotionProgram.Compile(
                name: "law",
                version: CompiledBodyMotionProgram.SupportedVersion,
                kind: null,
                operations: [BodyMotionOp.CommitPose]
            )).Refusal
        );
        Assert.Equal(
            expected: BodyMotionProgramRefusal.InstructionCountOutOfRange,
            actual: Refusal(kind: BodyProgramKind.Motion)
        );
        Assert.Equal(
            expected: BodyMotionProgramRefusal.OpcodeDuplicate,
            actual: Refusal(
                kind: BodyProgramKind.Motion,
                operations: [BodyMotionOp.CommitPose, BodyMotionOp.CommitPose]
            )
        );
        Assert.Equal(
            expected: BodyMotionProgramRefusal.OpcodeUnknown,
            actual: Refusal(
                kind: BodyProgramKind.Motion,
                operations: [((BodyMotionOp)200)]
            )
        );
    }
    [Fact]
    public void PhaseGroupingIsIndependentOfAuthoredOrder() {
        var forward = Compile(
            kind: BodyProgramKind.Motion,
            operations: MotionSelectable
        );
        var reversed = Compile(
            kind: BodyProgramKind.Motion,
            operations: MotionSelectable.Reverse().ToArray()
        );

        Assert.Equal(
            expected: forward.Phases.Length,
            actual: reversed.Phases.Length
        );

        for (var phase = 0; (phase < forward.Phases.Length); phase++) {
            Assert.Equal(
                expected: forward.Phases[phase],
                actual: reversed.Phases[phase]
            );
        }
    }
    [Fact]
    public void VerticalContactAuthorityFollowsTheGravityOperation() {
        Assert.True(condition: Compile(
            kind: BodyProgramKind.Motion,
            operations: [BodyMotionOp.ApplyVerticalGravity, BodyMotionOp.CommitPose]
        ).OwnsVerticalContactState);
        Assert.False(condition: Compile(
            kind: BodyProgramKind.Motion,
            operations: [BodyMotionOp.ApplyVerticalDecay, BodyMotionOp.CommitPose]
        ).OwnsVerticalContactState);
    }
}
