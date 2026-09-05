using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.Physics.Motion;

/// <summary>Declares the register and operation profile a compiled program uses.</summary>
[JsonConverter(typeof(StrictEnumConverter<BodyProgramKind>))]
public enum BodyProgramKind : byte {
    /// <summary>A body-motion program reading intent and writing body pose, velocity, and action state.</summary>
    Motion,

    /// <summary>A producer program reading sensors and writing channel values.</summary>
    Producer,
}
/// <summary>The register families a program kind admits.</summary>
[Flags]
public enum BodyProgramAdmission : byte {
    None = 0,
    Channels = 1,
    Pose = 2,
    Velocity = 4,
    ActionState = 8,
    Sensors = 16,
}
/// <summary>Names why a body motion program was refused during construction.</summary>
public enum BodyMotionProgramRefusal : byte {
    VersionUnsupported,
    NameMissing,
    InstructionCountOutOfRange,
    OpcodeUnknown,
    OpcodeDuplicate,
    ProgramKindUnknown,
    OpcodeInadmissible,
    ParameterMissing,
    ParameterUnknown,
}
/// <summary>Reports a construction-time body motion program refusal.</summary>
public sealed class BodyMotionProgramException : ArgumentException {
    /// <summary>Initializes a body motion program refusal.</summary>
    public BodyMotionProgramException(BodyMotionProgramRefusal refusal, string programName, string detail)
        : base(message: $"Body motion program '{programName}' refused {refusal}: {detail}") {
        Refusal = refusal;
    }

    /// <summary>Gets the refusal category.</summary>
    public BodyMotionProgramRefusal Refusal { get; }
}
/// <summary>The construction-time typed form of a body motion program.</summary>
public sealed class CompiledBodyMotionProgram {
    private const int MaxOperations = 32;

    private readonly HashSet<BodyMotionOp> m_operations;

    private CompiledBodyMotionProgram(string name, BodyProgramKind kind, BodyProgramAdmission admissionMask, BodyMotionOp[][] phases, HashSet<BodyMotionOp> operations) {
        Name = name;
        Kind = kind;
        AdmissionMask = admissionMask;
        Phases = phases;
        m_operations = operations;
    }

    /// <summary>Gets the register admission mask for <see cref="Kind"/>.</summary>
    public BodyProgramAdmission AdmissionMask { get; }
    /// <summary>Gets the declared program profile.</summary>
    public BodyProgramKind Kind { get; }
    /// <summary>Gets the program name.</summary>
    public string Name { get; }
    /// <summary>Gets a value indicating whether this program's selected operations cede the vertical channel to a
    /// host's contact resolution — <see cref="BodyMotionOp.ApplyHold"/>, whose gravity and lift laws integrate it —
    /// UNLESS the program also computes its velocity in local/body frame
    /// (<see cref="BodyMotionOp.ComputeLocalTargetVelocity"/>, folded through
    /// <see cref="BodyMotionOp.IntegrateScratchVelocity"/>): such a program owns its whole velocity channel
    /// directly, and a hold row there (a Lift row's decay bleeding a carried residual back to rest) shapes a term
    /// INSIDE that channel rather than ceding it, so folding contact's resolved velocity back in every tick would
    /// feed that residual into itself, an unbounded loop rather than a correction.</summary>
    public bool OwnsVerticalContactState => (Contains(operation: BodyMotionOp.ApplyHold) && !Contains(operation: BodyMotionOp.ComputeLocalTargetVelocity));
    /// <summary>Gets the operations grouped by their intrinsic host phase.</summary>
    public BodyMotionOp[][] Phases { get; }

    /// <summary>Gets the instruction-set version this compiler accepts.</summary>
    public const string SupportedVersion = "puck.body-motion.v1";

    private static BodyProgramAdmission AdmissionFor(BodyProgramKind kind) => kind switch {
        BodyProgramKind.Motion => (BodyProgramAdmission.Channels | BodyProgramAdmission.Pose | BodyProgramAdmission.Velocity | BodyProgramAdmission.ActionState),
        BodyProgramKind.Producer => (BodyProgramAdmission.Sensors | BodyProgramAdmission.Channels | BodyProgramAdmission.ActionState),
        _ => BodyProgramAdmission.None,
    };
    private static int Phase(BodyMotionOp op) => op switch {
        BodyMotionOp.SenseNearestInCone => 0,
        BodyMotionOp.ProduceSteeringIntent => 2,
        BodyMotionOp.FaceSensorTarget => 2,
        BodyMotionOp.ProduceFlockIntent => 2,
        BodyMotionOp.ResolveYawAttitudeAndPlanarFrame or BodyMotionOp.IntegrateLocalAttitude or BodyMotionOp.ResolveDriveFrame
            or BodyMotionOp.ResolveHold => 0,
        BodyMotionOp.ComputePlanarTargetVelocity or BodyMotionOp.ComputeLocalTargetVelocity => 1,
        BodyMotionOp.ShapeVelocity or BodyMotionOp.SnapYawToPlanarIntent => 2,
        BodyMotionOp.RunActionTriggers => 3,
        BodyMotionOp.ApplyHold => 4,
        BodyMotionOp.IntegratePlanarAndVerticalVelocity or BodyMotionOp.IntegrateScratchVelocity => 5,
        BodyMotionOp.CommitPose => 7,
        _ => throw Refuse(
        detail: $"opcode value {((int)op)} is not declared",
        name: "<unnamed>",
        refusal: BodyMotionProgramRefusal.OpcodeUnknown
    ),
    };
    private static bool ProgramSelectable(BodyMotionOp operation) => (operation < BodyMotionOp.SetVerticalVelocity);
    private static BodyMotionProgramException Refuse(BodyMotionProgramRefusal refusal, string? name, string detail) => new(
        detail: detail,
        programName: (name ?? "<null>"),
        refusal: refusal
    );
    private static BodyProgramAdmission RequiredAdmission(BodyMotionOp operation) => operation switch {
        BodyMotionOp.SenseNearestInCone => BodyProgramAdmission.Sensors,
        BodyMotionOp.ProduceSteeringIntent or BodyMotionOp.FaceSensorTarget or BodyMotionOp.ProduceFlockIntent => (BodyProgramAdmission.Sensors | BodyProgramAdmission.Channels | BodyProgramAdmission.ActionState),
        BodyMotionOp.ResolveYawAttitudeAndPlanarFrame or BodyMotionOp.IntegrateLocalAttitude or BodyMotionOp.ComputePlanarTargetVelocity
            or BodyMotionOp.ComputeLocalTargetVelocity or BodyMotionOp.ShapeVelocity
            or BodyMotionOp.SnapYawToPlanarIntent or BodyMotionOp.ResolveDriveFrame
            => (BodyProgramAdmission.Channels | BodyProgramAdmission.Pose | BodyProgramAdmission.Velocity),
        // A hold reads intent, writes the body frame, and spends a body-lane state slot, so it claims every register
        // family a motion program owns.
        BodyMotionOp.ResolveHold => (BodyProgramAdmission.Channels | BodyProgramAdmission.Pose | BodyProgramAdmission.Velocity | BodyProgramAdmission.ActionState),
        BodyMotionOp.RunActionTriggers => (BodyProgramAdmission.Channels | BodyProgramAdmission.Velocity | BodyProgramAdmission.ActionState),
        BodyMotionOp.ApplyHold
            or BodyMotionOp.IntegratePlanarAndVerticalVelocity
            or BodyMotionOp.IntegrateScratchVelocity => (BodyProgramAdmission.Pose | BodyProgramAdmission.Velocity),
        BodyMotionOp.CommitPose => BodyProgramAdmission.Pose,
        BodyMotionOp.SetVerticalVelocity or BodyMotionOp.ScaleVerticalVelocity or BodyMotionOp.PlanarImpulse => BodyProgramAdmission.Velocity,
        BodyMotionOp.SetState or BodyMotionOp.AddState or BodyMotionOp.StartTimer or BodyMotionOp.Designate or BodyMotionOp.Generate or BodyMotionOp.Judge => BodyProgramAdmission.ActionState,
        _ => (BodyProgramAdmission.Channels | BodyProgramAdmission.Pose | BodyProgramAdmission.Velocity | BodyProgramAdmission.ActionState | BodyProgramAdmission.Sensors),
    };

    /// <summary>Reports whether this program profile admits an instruction's required registers.</summary>
    public bool Admits(BodyMotionOp operation) => ((RequiredAdmission(operation: operation) & ~AdmissionMask) == BodyProgramAdmission.None);
    /// <summary>Compiles and validates a program's declared shape in one construction-time walk.</summary>
    /// <param name="name">The stable program name.</param>
    /// <param name="version">The declared instruction-set version; must equal <see cref="SupportedVersion"/>.</param>
    /// <param name="kind">The declared program profile.</param>
    /// <param name="operations">The selected domain operations; their phases are intrinsic and cannot be reordered.</param>
    /// <returns>The compiled program.</returns>
    /// <exception cref="BodyMotionProgramException">The declared shape is refused.</exception>
    public static CompiledBodyMotionProgram Compile(string? name, string? version, BodyProgramKind? kind, IReadOnlyList<BodyMotionOp>? operations) {
        if (string.IsNullOrWhiteSpace(value: name)) {
            throw Refuse(
                detail: "name is required",
                name: name,
                refusal: BodyMotionProgramRefusal.NameMissing
            );
        }
        if (!string.Equals(
            a: version,
            b: SupportedVersion,
            comparisonType: StringComparison.Ordinal
        )) {
            throw Refuse(
                detail: $"version '{version}' is not '{SupportedVersion}'",
                name: name,
                refusal: BodyMotionProgramRefusal.VersionUnsupported
            );
        }
        if (
            (operations is null) ||
            (operations.Count == 0) ||
            (operations.Count > MaxOperations)
        ) {
            throw Refuse(
                detail: $"operation count must be in [1, {MaxOperations}]",
                name: name,
                refusal: BodyMotionProgramRefusal.InstructionCountOutOfRange
            );
        }
        if (
            (kind is not { } programKind) ||
            !Enum.IsDefined(value: programKind)
        ) {
            throw Refuse(
                BodyMotionProgramRefusal.ProgramKindUnknown,
                name,
                $"program kind '{(kind?.ToString() ?? "<missing>")}' is not declared"
            );
        }

        var admissionMask = AdmissionFor(kind: programKind);

        var seen = new HashSet<BodyMotionOp>();

        foreach (var op in operations) {
            if (!Enum.IsDefined(value: op)) {
                throw Refuse(
                    detail: $"opcode value {((int)op)} is not declared",
                    name: name,
                    refusal: BodyMotionProgramRefusal.OpcodeUnknown
                );
            }
            if (
                !ProgramSelectable(operation: op) ||
                ((RequiredAdmission(operation: op) & ~admissionMask) != BodyProgramAdmission.None)
            ) {
                throw Refuse(
                    detail: $"opcode '{op}' is inadmissible for program kind '{programKind}'",
                    name: name,
                    refusal: BodyMotionProgramRefusal.OpcodeInadmissible
                );
            }
            if (!seen.Add(item: op)) {
                throw Refuse(
                    detail: $"opcode '{op}' occurs more than once",
                    name: name,
                    refusal: BodyMotionProgramRefusal.OpcodeDuplicate
                );
            }

            _ = Phase(op: op);
        }
        var phaseLists = new List<BodyMotionOp>[8];

        for (var phase = 0; (phase < phaseLists.Length); phase++) {
            phaseLists[phase] = [];
        }
        foreach (var op in Enum.GetValues<BodyMotionOp>()) {
            if (seen.Contains(item: op)) {
                phaseLists[Phase(op: op)].Add(item: op);
            }
        }

        var phases = new BodyMotionOp[phaseLists.Length][];

        for (var phase = 0; (phase < phases.Length); phase++) {
            phases[phase] = phaseLists[phase].ToArray();
        }

        return new CompiledBodyMotionProgram(
            admissionMask: admissionMask,
            kind: programKind,
            name: name,
            operations: seen,
            phases: phases
        );
    }
    /// <summary>Reports whether this program selects an operation.</summary>
    public bool Contains(BodyMotionOp operation) => m_operations.Contains(item: operation);
}
