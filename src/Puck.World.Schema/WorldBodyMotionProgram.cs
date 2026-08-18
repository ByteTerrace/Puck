using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Maths;

namespace Puck.World;

/// <summary>Identifies a body motion program instruction from the closed domain-operation vocabulary.</summary>
[JsonConverter(typeof(StrictEnumConverter<BodyMotionOp>))]
public enum BodyMotionOp : byte {
    SenseNearestInCone,
    ProduceWanderIntent,
    ProduceAttendIntent,
    FaceSensorTarget,
    ResolveYawAttitudeAndPlanarFrame,
    IntegrateLocalAttitude,
    ComputePlanarTargetVelocity,
    ComputeLocalTargetVelocity,
    ComputeSwimTargetVelocity,
    ShapePlanarVelocity,
    SnapYawToPlanarIntent,
    ResolveVehicleFrame,
    ShapeVehicleVelocity,
    RunActionTriggers,
    ApplyVerticalGravity,
    ApplyVerticalDecay,
    ApplyBuoyancyAndSurface,
    /// <summary>While MoveUp is non-zero, drives vertical velocity directly at MoveSpeed and suspends the ballistic
    /// channel. Releasing MoveUp returns vertical ownership to gravity, so authored jump actions and ordinary ground
    /// contact remain coherent in the same program.</summary>
    ApplyVerticalDrive,
    IntegratePlanarAndVerticalVelocity,
    IntegrateScratchVelocity,
    CommitPose,
    SetVerticalVelocity,
    ScaleVerticalVelocity,
    PlanarImpulse,
    SetState,
    AddState,
    StartTimer,
    Designate,
    Generate,
    Judge,
}
/// <summary>The storage kind of a named persistent action-state slot.</summary>
[JsonConverter(typeof(StrictEnumConverter<ActionStateKind>))]
public enum ActionStateKind : byte {
    Counter,
    Timer,
}
/// <summary>Declares where a compiled action-state slot survives. Authored documents select this through the
/// <c>state.body</c> or <c>state.identity</c> lane; the runtime keeps the closed enum so its fixed register metadata
/// remains compact.</summary>
[JsonConverter(typeof(StrictEnumConverter<ActionStateLifetime>))]
public enum ActionStateLifetime : byte {
    /// <summary>The slot belongs to one body and resets from its authored facts.</summary>
    Ephemeral,

    /// <summary>The slot belongs to a player identity and crosses sessions through the durable input/output seam.</summary>
    Durable,
}
/// <summary>The authored values a player-writable durable slot admits in this world.</summary>
[JsonDerivedType(typeof(ActionStateEnvelope.Range), typeDiscriminator: "range")]
[JsonDerivedType(typeof(ActionStateEnvelope.Set), typeDiscriminator: "set")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record ActionStateEnvelope {
    private ActionStateEnvelope() {
    }

    /// <summary>An inclusive numeric interval.</summary>
    /// <param name="Minimum">The least admitted value.</param>
    /// <param name="Maximum">The greatest admitted value.</param>
    public sealed record Range(float Minimum, float Maximum) : ActionStateEnvelope;
    /// <summary>A closed numeric set. Values are authored labels encoded in the slot's deterministic numeric domain.</summary>
    /// <param name="Values">The admitted values.</param>
    public sealed record Set(IReadOnlyList<float> Values) : ActionStateEnvelope;
}
/// <summary>A fixed comparison admitted by <see cref="ActionPredicate.CompareState"/>.</summary>
[JsonConverter(typeof(StrictEnumConverter<ActionStateComparison>))]
public enum ActionStateComparison : byte {
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
}
/// <summary>The one evaluation of an <see cref="ActionStateComparison"/> — a kit action's own state predicate and a
/// world rule's <c>compareState</c> operand ask exactly the same question of the same fixed-point pair, so the
/// vocabulary is decided in one place and neither can grow an arm the other lacks.</summary>
public static class ActionStateComparisons {
    /// <summary>Evaluates the comparison against a value/expectation pair.</summary>
    /// <param name="comparison">The comparison to evaluate.</param>
    /// <param name="value">The observed value.</param>
    /// <param name="expected">The value compared against.</param>
    /// <returns><see langword="true"/> when the comparison holds.</returns>
    public static bool Holds(this ActionStateComparison comparison, FixedQ4816 value, FixedQ4816 expected) => comparison switch {
        ActionStateComparison.Equal => (value == expected),
        ActionStateComparison.NotEqual => (value != expected),
        ActionStateComparison.Less => (value < expected),
        ActionStateComparison.LessOrEqual => (value <= expected),
        ActionStateComparison.Greater => (value > expected),
        _ => (value >= expected),
    };
    /// <summary>Evaluates the comparison when either side may be positive infinity — a fact whose magnitude exceeds
    /// every representable number (today only the <c>$parked:</c> channel's forever case). Infinity compares as
    /// strictly greater than every finite value and equal to itself, so <c>&gt; finite</c> holds, <c>&lt;= finite</c>
    /// does not, and <c>== finite</c> never does. A sentinel numeric encoding was deliberately rejected: any finite
    /// stand-in is a value an authored comparand could legitimately equal, and a comparison that cannot distinguish
    /// "forever" from one particular number is lying about one of them.</summary>
    /// <param name="comparison">The comparison to evaluate.</param>
    /// <param name="value">The observed value; ignored when <paramref name="valueIsForever"/>.</param>
    /// <param name="valueIsForever">Whether the observed side is positive infinity.</param>
    /// <param name="expected">The value compared against; ignored when <paramref name="expectedIsForever"/>.</param>
    /// <param name="expectedIsForever">Whether the expected side is positive infinity.</param>
    /// <returns><see langword="true"/> when the comparison holds.</returns>
    public static bool Holds(this ActionStateComparison comparison, FixedQ4816 value, bool valueIsForever, FixedQ4816 expected, bool expectedIsForever) {
        if (
            !valueIsForever &&
            !expectedIsForever
        ) {
            return comparison.Holds(
                expected: expected,
                value: value
            );
        }

        // Exactly one or both sides are infinite; the finite magnitudes no longer matter, only the ordering sign.
        var sign = ((valueIsForever, expectedIsForever)) switch {
            (true, true) => 0,
            (true, false) => 1,
            _ => -1,
        };

        return comparison switch {
            ActionStateComparison.Equal => (sign == 0),
            ActionStateComparison.NotEqual => (sign != 0),
            ActionStateComparison.Less => (sign < 0),
            ActionStateComparison.LessOrEqual => (sign <= 0),
            ActionStateComparison.Greater => (sign > 0),
            _ => (sign >= 0),
        };
    }
}
/// <summary>Declares one named body-state slot shared by every kit action in the world. The carrying
/// <see cref="WorldStateSection"/> lane selects whether it belongs to the body or its identity.</summary>
/// <param name="Name">The stable slot name predicates and effects reference.</param>
/// <param name="Kind">Whether the slot stores a counter or a remaining timer.</param>
/// <param name="Initial">The initial counter value or timer duration in seconds.</param>
/// <param name="ResetFact">An optional body fact that resets the slot to <paramref name="Initial"/> while it holds.</param>
/// <param name="PlayerWritable">Whether the identity driving the body may submit a value for the slot.</param>
/// <param name="Envelope">The visited world's admitted effective values. Required for a player-writable slot.</param>
public sealed record ActionStateSlot(
    string Name,
    ActionStateKind Kind,
    float Initial = 0f,
    ActionFact? ResetFact = null,
    bool PlayerWritable = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ActionStateEnvelope? Envelope = null
);
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
/// <summary>An authored fixed-phase body motion program.</summary>
/// <param name="Name">The stable name kits use to select the program.</param>
/// <param name="Version">The instruction-set version.</param>
/// <param name="Kind">The declared program profile that gates operations and registers.</param>
/// <param name="Operations">The selected domain operations; their phases are intrinsic and cannot be reordered.</param>
/// <param name="Target">The single source supplying the program's target, when it uses target-aware operations.</param>
public sealed record BodyMotionProgram(
    string Name,
    string Version,
    BodyProgramKind? Kind,
    IReadOnlyList<BodyMotionOp> Operations,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BodyTargetSource? Target = null
) {
    /// <summary>The supported body-motion instruction-set version.</summary>
    public const string CurrentVersion = "puck.body-motion.v1";
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

    private CompiledBodyMotionProgram(string name, BodyProgramKind kind, BodyProgramAdmission admissionMask, BodyMotionOp[][] phases, HashSet<BodyMotionOp> operations, BodyTargetSource? target) {
        Name = name;
        Kind = kind;
        AdmissionMask = admissionMask;
        Phases = phases;
        m_operations = operations;
        Target = target;
    }

    /// <summary>Gets the register admission mask for <see cref="Kind"/>.</summary>
    public BodyProgramAdmission AdmissionMask { get; }
    /// <summary>Gets the declared program profile.</summary>
    public BodyProgramKind Kind { get; }
    /// <summary>Gets the program name.</summary>
    public string Name { get; }
    /// <summary>Gets a value indicating whether this program's selected operations integrate gravity into vertical velocity
    /// (<see cref="BodyMotionOp.ApplyVerticalGravity"/> — the same op <c>WorldDefinitionValidator</c>'s
    /// <c>GravityArc</c> tuning facet maps from). This is the vertical-contact-authority signal
    /// <c>WorldBody.ResolveProgramContacts</c> gates its vertical write-back on: a program that owns this
    /// integrates its own vertical channel (e.g. <see cref="BodyMotionOp.ApplyVerticalDecay"/>'s bleed) and must
    /// not have contact resolution overwrite it — feeding a decay channel's own prior value back into itself
    /// every tick is an unbounded loop, not a correction.</summary>
    public bool OwnsVerticalContactState => Contains(operation: BodyMotionOp.ApplyVerticalGravity);
    /// <summary>Gets the operations grouped by their intrinsic host phase.</summary>
    public BodyMotionOp[][] Phases { get; }
    /// <summary>Gets the program's declared target source.</summary>
    public BodyTargetSource? Target { get; }

    private static BodyProgramAdmission AdmissionFor(BodyProgramKind kind) => kind switch {
        BodyProgramKind.Motion => (BodyProgramAdmission.Channels | BodyProgramAdmission.Pose | BodyProgramAdmission.Velocity | BodyProgramAdmission.ActionState),
        BodyProgramKind.Producer => (BodyProgramAdmission.Sensors | BodyProgramAdmission.Channels | BodyProgramAdmission.ActionState),
        _ => BodyProgramAdmission.None,
    };
    private static int Phase(BodyMotionOp op) => op switch {
        BodyMotionOp.SenseNearestInCone => 0,
        BodyMotionOp.ProduceWanderIntent => 1,
        BodyMotionOp.ProduceAttendIntent => 2,
        BodyMotionOp.FaceSensorTarget => 2,
        BodyMotionOp.ResolveYawAttitudeAndPlanarFrame or BodyMotionOp.IntegrateLocalAttitude or BodyMotionOp.ResolveVehicleFrame => 0,
        BodyMotionOp.ComputePlanarTargetVelocity or BodyMotionOp.ComputeLocalTargetVelocity or BodyMotionOp.ComputeSwimTargetVelocity => 1,
        BodyMotionOp.ShapePlanarVelocity or BodyMotionOp.SnapYawToPlanarIntent or BodyMotionOp.ShapeVehicleVelocity => 2,
        BodyMotionOp.RunActionTriggers => 3,
        BodyMotionOp.ApplyVerticalGravity or BodyMotionOp.ApplyVerticalDecay or BodyMotionOp.ApplyBuoyancyAndSurface
            or BodyMotionOp.ApplyVerticalDrive => 4,
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
        BodyMotionOp.ProduceWanderIntent or BodyMotionOp.ProduceAttendIntent or BodyMotionOp.FaceSensorTarget => (BodyProgramAdmission.Sensors | BodyProgramAdmission.Channels | BodyProgramAdmission.ActionState),
        BodyMotionOp.ResolveYawAttitudeAndPlanarFrame or BodyMotionOp.IntegrateLocalAttitude or BodyMotionOp.ComputePlanarTargetVelocity
            or BodyMotionOp.ComputeLocalTargetVelocity or BodyMotionOp.ComputeSwimTargetVelocity or BodyMotionOp.ShapePlanarVelocity
            or BodyMotionOp.SnapYawToPlanarIntent or BodyMotionOp.ResolveVehicleFrame or BodyMotionOp.ShapeVehicleVelocity
            => (BodyProgramAdmission.Channels | BodyProgramAdmission.Pose | BodyProgramAdmission.Velocity),
        BodyMotionOp.RunActionTriggers => (BodyProgramAdmission.Channels | BodyProgramAdmission.Velocity | BodyProgramAdmission.ActionState),
        BodyMotionOp.ApplyVerticalGravity or BodyMotionOp.ApplyVerticalDecay or BodyMotionOp.ApplyBuoyancyAndSurface
            or BodyMotionOp.ApplyVerticalDrive
            or BodyMotionOp.IntegratePlanarAndVerticalVelocity
            or BodyMotionOp.IntegrateScratchVelocity => (BodyProgramAdmission.Pose | BodyProgramAdmission.Velocity),
        BodyMotionOp.CommitPose => BodyProgramAdmission.Pose,
        BodyMotionOp.SetVerticalVelocity or BodyMotionOp.ScaleVerticalVelocity or BodyMotionOp.PlanarImpulse => BodyProgramAdmission.Velocity,
        BodyMotionOp.SetState or BodyMotionOp.AddState or BodyMotionOp.StartTimer or BodyMotionOp.Designate or BodyMotionOp.Generate or BodyMotionOp.Judge => BodyProgramAdmission.ActionState,
        _ => (BodyProgramAdmission.Channels | BodyProgramAdmission.Pose | BodyProgramAdmission.Velocity | BodyProgramAdmission.ActionState | BodyProgramAdmission.Sensors),
    };

    /// <summary>Reports whether this program profile admits an instruction's required registers.</summary>
    public bool Admits(BodyMotionOp operation) => ((RequiredAdmission(operation: operation) & ~AdmissionMask) == BodyProgramAdmission.None);
    /// <summary>Compiles and validates an authored program in one construction-time walk.</summary>
    public static CompiledBodyMotionProgram Compile(BodyMotionProgram program) {
        ArgumentNullException.ThrowIfNull(argument: program);

        if (string.IsNullOrWhiteSpace(value: program.Name)) {
            throw Refuse(
                BodyMotionProgramRefusal.NameMissing,
                program.Name,
                "name is required"
            );
        }
        if (!string.Equals(
            a: program.Version,
            b: BodyMotionProgram.CurrentVersion,
            comparisonType: StringComparison.Ordinal
        )) {
            throw Refuse(
                BodyMotionProgramRefusal.VersionUnsupported,
                program.Name,
                $"version '{program.Version}' is not '{BodyMotionProgram.CurrentVersion}'"
            );
        }
        if (
            (program.Operations is null) ||
            (program.Operations.Count == 0) ||
            (program.Operations.Count > MaxOperations)
        ) {
            throw Refuse(
                BodyMotionProgramRefusal.InstructionCountOutOfRange,
                program.Name,
                $"operation count must be in [1, {MaxOperations}]"
            );
        }
        if (
            (program.Kind is not { } kind) ||
            !Enum.IsDefined(value: kind)
        ) {
            throw Refuse(
                BodyMotionProgramRefusal.ProgramKindUnknown,
                program.Name,
                $"program kind '{(program.Kind?.ToString() ?? "<missing>")}' is not declared"
            );
        }

        var admissionMask = AdmissionFor(kind: kind);

        var seen = new HashSet<BodyMotionOp>();

        foreach (var op in program.Operations) {
            if (!Enum.IsDefined(value: op)) {
                throw Refuse(
                    BodyMotionProgramRefusal.OpcodeUnknown,
                    program.Name,
                    $"opcode value {((int)op)} is not declared"
                );
            }
            if (
                !ProgramSelectable(operation: op) ||
                ((RequiredAdmission(operation: op) & ~admissionMask) != BodyProgramAdmission.None)
            ) {
                throw Refuse(
                    BodyMotionProgramRefusal.OpcodeInadmissible,
                    program.Name,
                    $"opcode '{op}' is inadmissible for program kind '{kind}'"
                );
            }
            if (!seen.Add(item: op)) {
                throw Refuse(
                    BodyMotionProgramRefusal.OpcodeDuplicate,
                    program.Name,
                    $"opcode '{op}' occurs more than once"
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
            name: program.Name,
            kind: kind,
            admissionMask: admissionMask,
            phases: phases,
            operations: seen,
            target: program.Target
        );
    }
    /// <summary>Reports whether this program selects an operation.</summary>
    public bool Contains(BodyMotionOp operation) => m_operations.Contains(item: operation);
    /// <summary>Reports whether the selected instructions read <paramref name="role"/>.</summary>
    public bool RequiresRole(ChannelRole role) => role switch {
        ChannelRole.MoveForward or ChannelRole.MoveStrafe => (Contains(operation: BodyMotionOp.ComputePlanarTargetVelocity)
            || Contains(operation: BodyMotionOp.SnapYawToPlanarIntent)
            || Contains(operation: BodyMotionOp.ComputeLocalTargetVelocity)
            || Contains(operation: BodyMotionOp.ComputeSwimTargetVelocity)
            || (Contains(operation: BodyMotionOp.ShapeVehicleVelocity) && (role == ChannelRole.MoveForward))),
        ChannelRole.Turn => (Contains(operation: BodyMotionOp.ResolveYawAttitudeAndPlanarFrame)
            || Contains(operation: BodyMotionOp.IntegrateLocalAttitude)
            || Contains(operation: BodyMotionOp.ResolveVehicleFrame)),
        ChannelRole.MoveUp => (Contains(operation: BodyMotionOp.ComputeLocalTargetVelocity)
            || Contains(operation: BodyMotionOp.ComputeSwimTargetVelocity)
            || Contains(operation: BodyMotionOp.ApplyVerticalDrive)),
        // ResolveVehicleFrame reads Pitch only under a positive PitchRate, so Pitch is not REQUIRED for it — a
        // pitchless world's flying-vehicle pitch reads zero rather than refusing the kit.
        ChannelRole.Pitch or ChannelRole.Roll => Contains(operation: BodyMotionOp.IntegrateLocalAttitude),
        // SnapYawToPlanarIntent reads FaceX/FaceZ only when a world declares them (FaceY rides along for
        // attitude-bearing arms) — a faceless world's snap stays movement-facing rather than refusing the kit.
        _ => false,
    };
}
/// <summary>The flattened, fixed-point form of one predicate.</summary>
public readonly record struct CompiledPredicate(ActionFact Fact, int RecencySlot, int StateSlot, FixedQ4816 Value, ActionStateComparison Comparison, CompiledPredicateKind Kind);
/// <summary>The compiled predicate dispatch tag.</summary>
public enum CompiledPredicateKind : byte {
    Now,
    Recently,
    CompareState,
    TimerElapsed,
}
/// <summary>One compiled instruction shared by program phases and action triggers.</summary>
/// <remarks><c>StateName</c> carries <see cref="BodyMotionOp.Generate"/>'s draw site — the one row a generate names,
/// since a site's source and cursor are its own — and is <see langword="null"/> for every other operation except
/// <see cref="BodyMotionOp.Judge"/>, where it carries the declared judge row name. Nothing is bound at kit-compile
/// time here for either: the generate site is a world-global <c>state</c> row and the judge row lives in the
/// declared <c>judges</c> table, neither part of this kit's per-body slot table.</remarks>
public readonly record struct CompiledBodyInstruction(BodyMotionOp Operation, FixedQ4816 Value, FixedVector3 Direction, ulong DurationTicks, int StateSlot, ActionTarget Target = ActionTarget.Self, string? StateName = null);
/// <summary>One compiled named action-state slot.</summary>
public readonly record struct CompiledActionStateSlot(
    string Name,
    ActionStateKind Kind,
    FixedQ4816 InitialValue,
    ulong InitialTicks,
    ActionFact? ResetFact,
    ActionStateLifetime Lifetime,
    bool PlayerWritable,
    CompiledActionStateEnvelope? Envelope
);
/// <summary>A slot envelope compiled into the slot's fixed counter or engine-tick domain.</summary>
/// <param name="Minimum">The inclusive range minimum, or zero for a set.</param>
/// <param name="Maximum">The inclusive range maximum, or zero for a set.</param>
/// <param name="Values">The closed set, or <see langword="null"/> for a range.</param>
public sealed record CompiledActionStateEnvelope(long Minimum, long Maximum, long[]? Values) {
    /// <summary>Clamps a raw value to the range, or substitutes the authored initial value for a closed-set miss.</summary>
    public long Clamp(long value, long initial) => ((Values is null)
        ? Math.Clamp(
            value: value,
            min: Minimum,
            max: Maximum
        )
        : (Contains(value: value)
            ? value
            : initial
    ));
    /// <summary>Returns whether a raw slot-domain value is admitted.</summary>
    public bool Contains(long value) => ((Values is { } values)
        ? (Array.IndexOf(
            array: values,
            value: value
        ) >= 0)
        : ((value >= Minimum) && (value <= Maximum))
    );
}
/// <summary>One compiled trigger channel: the flattened conjunction gate, the press latch in engine ticks, and the
/// fixed-point effects in authored order.</summary>
public sealed record CompiledTrigger(CompiledPredicate[] Gate, ulong LatchTicks, CompiledBodyInstruction[] Effects);
/// <summary>A lane binding compiled once before simulation: both trigger channels plus the recency-clock table (one
/// slot per <see cref="ActionPredicate.Recently"/> instance across both gates — the per-tick clock updater walks it).</summary>
public sealed record CompiledActionSpec(CompiledTrigger? OnPress, CompiledTrigger? OnRelease, CompiledFactTrigger[] OnFact, ActionFact[] RecencyFacts, ulong[] RecencyWindows) {
    // Flattens a predicate ADT into a fixed-point conjunction gate, allocating one shared recency slot per Recently
    // instance. Promoted to internal so the motion-response compiler (a non-lane caller) reuses the same slotting.
    internal static void FlattenPredicate(ActionPredicate? predicate, List<CompiledPredicate> gate, List<ActionFact> recencyFacts, List<ulong> recencyWindows, IReadOnlyDictionary<string, int>? stateSlots = null) {
        switch (predicate) {
            case null:
                break;
            case ActionPredicate.All all:
                foreach (var inner in all.Predicates) {
                    FlattenPredicate(
                        gate: gate,
                        predicate: inner,
                        recencyFacts: recencyFacts,
                        recencyWindows: recencyWindows,
                        stateSlots: stateSlots
                    );
                }

                break;
            case ActionPredicate.Now now:
                gate.Add(item: new CompiledPredicate(
                    Fact: now.Fact,
                    RecencySlot: 0,
                    StateSlot: -1,
                    Value: default,
                    Comparison: default,
                    Kind: CompiledPredicateKind.Now
                ));

                break;
            case ActionPredicate.Recently recently:
                gate.Add(item: new CompiledPredicate(
                    Fact: recently.Fact,
                    RecencySlot: recencyFacts.Count,
                    StateSlot: -1,
                    Value: default,
                    Comparison: default,
                    Kind: CompiledPredicateKind.Recently
                ));
                recencyFacts.Add(item: recently.Fact);
                recencyWindows.Add(item: DurationTicks(seconds: recently.WindowSeconds));

                break;
            case ActionPredicate.CompareState compare:
                // A per-body action-state slot is not keyed — a `key` here would be parsed and discarded, which is
                // exactly the shape this campaign refuses. It is legitimate at WORLD scope alone (WorldRuleCompiler).
                if (compare.Key is not null) {
                    throw new InvalidOperationException(message: $"Predicate 'compareState' on action state '{compare.State}' carries a 'key' — a per-body action-state slot is not keyed; 'key' addresses a world state row's cell and is legitimate only in a world rule.");
                }
                // A comparand ROW reference addresses a world state row (or a reserved channel a world evaluates
                // per tick) — a per-body action-state slot has neither, so the second spelling is legitimate only in
                // a world rule (WorldRuleCompiler), never here.
                if (
                    (compare.ComparandState is not null) ||
                    (compare.ComparandKey is not null)
                ) {
                    throw new InvalidOperationException(message: $"Predicate 'compareState' on action state '{compare.State}' carries a 'comparandState'/'comparandKey' — a per-body action-state slot has no world state row to reference; a comparand row is legitimate only in a world rule.");
                }

                if (compare.Value is not { } constant) {
                    throw new InvalidOperationException(message: $"Predicate 'compareState' on action state '{compare.State}' carries no 'value' — a per-body predicate names the authored constant to compare against.");
                }

                gate.Add(item: new CompiledPredicate(
                    Fact: default,
                    RecencySlot: 0,
                    StateSlot: ResolveState(
                        name: compare.State,
                        stateSlots: stateSlots
                    ),
                    Value: FixedQ4816.FromDouble(value: constant),
                    Comparison: compare.Comparison,
                    Kind: CompiledPredicateKind.CompareState
                ));
                break;
            case ActionPredicate.TimerElapsed elapsed:
                gate.Add(item: new CompiledPredicate(
                    Fact: default,
                    RecencySlot: 0,
                    StateSlot: ResolveState(
                        name: elapsed.State,
                        stateSlots: stateSlots
                    ),
                    Value: default,
                    Comparison: default,
                    Kind: CompiledPredicateKind.TimerElapsed
                ));
                break;
        }
    }

    private static CompiledBodyInstruction CompileEffect(ActionEffect effect, IReadOnlyDictionary<string, int> stateSlots, CompiledBodyMotionProgram program, string actionName) {
        var instruction = effect switch {
            ActionEffect.SetVerticalVelocity set => new CompiledBodyInstruction(
            Operation: BodyMotionOp.SetVerticalVelocity,
            Value: FixedQ4816.FromDouble(value: set.Velocity),
            Direction: default,
            DurationTicks: 0UL,
            StateSlot: -1,
            Target: set.Target
        ),
            ActionEffect.ScaleVerticalVelocity scale => new CompiledBodyInstruction(
            Operation: BodyMotionOp.ScaleVerticalVelocity,
            Value: FixedQ4816.FromDouble(value: scale.Factor),
            Direction: default,
            DurationTicks: 0UL,
            StateSlot: -1,
            Target: scale.Target
        ),
            ActionEffect.PlanarImpulse impulse => new CompiledBodyInstruction(
            Operation: BodyMotionOp.PlanarImpulse,
            Value: FixedQ4816.FromDouble(value: impulse.Speed),
            Direction: new FixedVector3(
                X: FixedQ4816.FromDouble(value: impulse.BodyDirection.X),
                Y: FixedQ4816.FromDouble(value: impulse.BodyDirection.Y),
                Z: FixedQ4816.FromDouble(value: impulse.BodyDirection.Z)
            ),
            DurationTicks: DurationTicks(seconds: impulse.DurationSeconds),
            StateSlot: -1,
            Target: impulse.Target
        ),
            ActionEffect.SetState set => new CompiledBodyInstruction(
            Operation: BodyMotionOp.SetState,
            Value: FixedQ4816.FromDouble(value: RequireBodyEffectValue(
                value: set.Value,
                fromState: set.FromState,
                fromKey: set.FromKey,
                valueSeconds: set.ValueSeconds,
                actionName: actionName,
                effectName: "setState",
                state: set.State
            )),
            Direction: default,
            DurationTicks: 0UL,
            StateSlot: ResolveState(
                name: set.State,
                stateSlots: stateSlots,
                key: set.Key,
                effect: "setState"
            ),
            Target: set.Target,
            StateName: set.State
        ),
            ActionEffect.AddState add => new CompiledBodyInstruction(
            Operation: BodyMotionOp.AddState,
            Value: FixedQ4816.FromDouble(value: RequireBodyEffectValue(
                value: add.Value,
                fromState: add.FromState,
                fromKey: add.FromKey,
                valueSeconds: add.ValueSeconds,
                actionName: actionName,
                effectName: "addState",
                state: add.State
            )),
            Direction: default,
            DurationTicks: 0UL,
            StateSlot: ResolveState(
                name: add.State,
                stateSlots: stateSlots,
                key: add.Key,
                effect: "addState"
            ),
            Target: add.Target,
            StateName: add.State
        ),
            ActionEffect.StartTimer timer => new CompiledBodyInstruction(
            Operation: BodyMotionOp.StartTimer,
            Value: default,
            Direction: default,
            DurationTicks: DurationTicks(seconds: timer.Seconds),
            StateSlot: ResolveState(
                name: timer.State,
                stateSlots: stateSlots
            ),
            Target: timer.Target,
            StateName: timer.State
        ),
            ActionEffect.Designate designate => new CompiledBodyInstruction(
            Operation: BodyMotionOp.Designate,
            Value: default,
            Direction: default,
            DurationTicks: 0UL,
            StateSlot: -1,
            Target: designate.Target,
            StateName: designate.Register
        ),
            // Nothing is resolved at kit-compile time: the generator row and the destination row are world-global
            // `state` rows, not this kit's per-body slot table, so both names ride through to the mutation compose
            // boundary that owns their existence checks.
            ActionEffect.Generate generate => new CompiledBodyInstruction(
            Operation: BodyMotionOp.Generate,
            Value: default,
            Direction: default,
            DurationTicks: 0UL,
            StateSlot: -1,
            Target: ActionTarget.Self,
            StateName: generate.Row
        ),
            // The judge row is resolved against the declared judges[] table at validation time (ValidateEffect), so
            // by the time this compiles the name is already known to name a real row — nothing further to bind here.
            ActionEffect.Judge judge => new CompiledBodyInstruction(
            Operation: BodyMotionOp.Judge,
            Value: default,
            Direction: default,
            DurationTicks: 0UL,
            StateSlot: -1,
            Target: ActionTarget.Self,
            StateName: judge.JudgeRef
        ),
            // countdownState/upsertHudPanel/removeHudPanel/upsertPlacement/removePlacement author WORLD state/document
            // rows — a per-body
            // action has none of its own, so these are refused BY NAME here rather than parsed and discarded
            // (legitimate only inside a WorldRule; see WorldRuleCompiler.CompileEffect).
            ActionEffect.CountdownState or ActionEffect.UpsertHudPanel or ActionEffect.RemoveHudPanel or ActionEffect.UpsertPlacement or ActionEffect.RemovePlacement =>
                throw new InvalidOperationException(message: $"Action '{actionName}' uses effect '{effect.GetType().Name}', which has no body-scope meaning — it authors a WORLD document row and is admissible only inside a world rule's own effects."),
            // save writes the WORLD's own file — a per-body action has no world file of its own to save, so this is
            // refused BY NAME here too (legitimate only inside a WorldRule; see WorldRuleCompiler.CompileEffect and
            // ActionEffect.Save's own remarks).
            ActionEffect.Save =>
                throw new InvalidOperationException(message: $"Action '{actionName}' uses effect 'Save', which has no body-scope meaning — a per-body action has no world file of its own to save, and is admissible only inside a world rule's own effects."),
            _ => throw new InvalidOperationException(message: $"Action '{actionName}' contains an unknown effect kind."),
        };

        if (!program.Admits(operation: instruction.Operation)) {
            throw new BodyMotionProgramException(
                refusal: BodyMotionProgramRefusal.OpcodeInadmissible,
                programName: program.Name,
                detail: $"action '{actionName}' opcode '{instruction.Operation}' is inadmissible for program kind '{program.Kind}'"
            );
        }

        return instruction;
    }
    private static CompiledTrigger? CompileTrigger(ActionTrigger? trigger, List<ActionFact> recencyFacts, List<ulong> recencyWindows, IReadOnlyDictionary<string, int> stateSlots, CompiledBodyMotionProgram program, string actionName) {
        if (trigger is null) {
            return null;
        }

        var gate = new List<CompiledPredicate>();

        FlattenPredicate(
            predicate: trigger.Gate,
            gate: gate,
            recencyFacts: recencyFacts,
            recencyWindows: recencyWindows,
            stateSlots: stateSlots
        );

        var effects = new CompiledBodyInstruction[trigger.Effects.Count];

        for (var index = 0; (index < effects.Length); index++) {
            effects[index] = CompileEffect(
                effect: trigger.Effects[index],
                stateSlots: stateSlots,
                program: program,
                actionName: actionName
            );
        }

        return new CompiledTrigger(
            Gate: gate.ToArray(),
            LatchTicks: DurationTicks(seconds: trigger.LatchSeconds),
            Effects: effects
        );
    }
    // Seconds → engine ticks through the same FromDouble + round-up path the runtime tuning conversions ride.
    // Puck.Maths.FixedTickConversion is the single-sourced conversion Puck.World.Server's WorldBody calls too — this
    // project cannot reference WorldBody directly (Puck.World.Schema must not depend on Puck.World.Server).
    private static ulong DurationTicks(float seconds) {
        return FixedTickConversion.DurationEngineTicks(seconds: FixedQ4816.FromDouble(value: seconds));
    }
    // A per-body action-state slot has no world state row to copy from — setState/addState's live 'fromState'/
    // 'fromKey' spelling is legitimate only in a world rule (WorldRuleCompiler); a body-scope effect always writes an
    // authored constant, so 'value' is required here on the same terms compareState's own body-scope 'value' is.
    private static float RequireBodyEffectValue(float? value, string? fromState, string? fromKey, decimal? valueSeconds, string actionName, string effectName, string state) {
        if (
            (fromState is not null) ||
            (fromKey is not null)
        ) {
            throw new InvalidOperationException(message: $"Action '{actionName}' effect '{effectName}' on action state '{state}' carries a 'fromState'/'fromKey' — a per-body action-state slot has no world state row to copy from; a live copy source is legitimate only in a world rule.");
        }

        if (valueSeconds is not null) {
            throw new InvalidOperationException(message: $"Action '{actionName}' effect '{effectName}' on action state '{state}' carries a 'valueSeconds' — that spelling is WORLD SCOPE ONLY (a state row a world rule decrements once per simulation tick); a per-body effect writes an authored constant via 'value', or starts a proper timer via 'startTimer'.");
        }

        return (value ?? throw new InvalidOperationException(message: $"Action '{actionName}' effect '{effectName}' on action state '{state}' carries no 'value' — a per-body effect writes an authored constant; a live copy source is legitimate only in a world rule."));
    }
    private static int ResolveState(string name, IReadOnlyDictionary<string, int>? stateSlots) => (((stateSlots is not null) && stateSlots.TryGetValue(
        key: name,
        value: out var slot
    ))
        ? slot
        : throw new InvalidOperationException(message: $"Action state '{name}' was not declared.")
    );
    // The keyed overload: a per-body action-state slot is not keyed, so an authored `key` here is refused rather than
    // discarded (it addresses a world state row's cell and is legitimate only in a world rule).
    private static int ResolveState(string name, IReadOnlyDictionary<string, int>? stateSlots, string? key, string effect) => ((key is null)
        ? ResolveState(
            name: name,
            stateSlots: stateSlots
        )
        : throw new InvalidOperationException(message: $"Effect '{effect}' on action state '{name}' carries a 'key' — a per-body action-state slot is not keyed; 'key' addresses a world state row's cell and is legitimate only in a world rule.")
    );

    /// <summary>Compiles an authored binding: predicates flatten (nested <see cref="ActionPredicate.All"/>
    /// conjunctions concatenate), seconds become engine ticks, floats become fixed point — once, at the boundary.</summary>
    /// <param name="spec">The authored binding, or <see langword="null"/> for an unbound lane.</param>
    /// <param name="stateSlots">The kit-wide named action-state lookup.</param>
    /// <param name="program">The compiled program profile admitting trigger instructions.</param>
    /// <param name="actionName">The refusing action's qualified name.</param>
    public static CompiledActionSpec? Compile(ActionSpec? spec, IReadOnlyDictionary<string, int> stateSlots, CompiledBodyMotionProgram program, string actionName) {
        if (spec is null) {
            return null;
        }

        var recencyFacts = new List<ActionFact>();
        var recencyWindows = new List<ulong>();
        var onPress = CompileTrigger(
            trigger: spec.OnPress,
            recencyFacts: recencyFacts,
            recencyWindows: recencyWindows,
            stateSlots: stateSlots,
            program: program,
            actionName: actionName
        );
        var onRelease = CompileTrigger(
            trigger: spec.OnRelease,
            recencyFacts: recencyFacts,
            recencyWindows: recencyWindows,
            stateSlots: stateSlots,
            program: program,
            actionName: actionName
        );
        // A fact trigger's own gate allocates recency slots from the SAME two lists both channel triggers use — one
        // recency clock table per lane binding, never a third parallel table for the fact channel.
        var onFact = (spec.OnFact ?? []).Select(selector: rule => {
            var factGate = new List<CompiledPredicate>();

            FlattenPredicate(
                predicate: rule.Gate,
                gate: factGate,
                recencyFacts: recencyFacts,
                recencyWindows: recencyWindows,
                stateSlots: stateSlots
            );

            return new CompiledFactTrigger(
                Fact: rule.Fact,
                Gate: factGate.ToArray(),
                Mode: rule.Mode,
                Effects: rule.Effects.Select(selector: effect => CompileEffect(
                    actionName: actionName,
                    effect: effect,
                    program: program,
                    stateSlots: stateSlots
                )).ToArray()
            );
        }).ToArray();

        return new CompiledActionSpec(
            OnPress: onPress,
            OnRelease: onRelease,
            OnFact: onFact,
            RecencyFacts: recencyFacts.ToArray(),
            RecencyWindows: recencyWindows.ToArray()
        );
    }
}
/// <summary>One producer program and a kit's fixed-point arguments for it.</summary>
public sealed class CompiledBodyProducer {
    private readonly IReadOnlyDictionary<string, int> m_channels;
    private readonly IReadOnlyDictionary<string, FixedQ4816> m_scalars;

    private CompiledBodyProducer(CompiledBodyMotionProgram program, IReadOnlyDictionary<string, FixedQ4816> scalars, IReadOnlyDictionary<string, int> channels, FixedBodyTargetSource? target) {
        Program = program;
        m_scalars = scalars;
        m_channels = channels;
        Target = target;
    }

    /// <summary>Gets the compiled producer program.</summary>
    public CompiledBodyMotionProgram Program { get; }
    /// <summary>Gets the compiled target source, when this producer senses a target.</summary>
    public FixedBodyTargetSource? Target { get; }

    /// <summary>Reads one validated channel ordinal by name, or <c>-1</c> when omitted.</summary>
    public int Channel(string name) => (m_channels.TryGetValue(
        key: name,
        value: out var ordinal
    )
        ? ordinal
        : -1
    );
    /// <summary>Compiles a kit's producer parameters.</summary>
    public static CompiledBodyProducer Compile(CompiledBodyMotionProgram program, BodyProgramParameters parameters, WorldChannelTable channels, WorldTargetRegisterTable targets) {
        var scalars = new Dictionary<string, FixedQ4816>(
            capacity: parameters.Scalars.Count,
            comparer: StringComparer.Ordinal
        );

        foreach (var (name, value) in parameters.Scalars) {
            scalars.Add(
                key: name,
                value: FixedQ4816.FromDouble(value: value)
            );
        }

        var channelOrdinals = new Dictionary<string, int>(
            capacity: parameters.Channels.Count,
            comparer: StringComparer.Ordinal
        );

        foreach (var (name, channel) in parameters.Channels) {
            channelOrdinals.Add(
                key: name,
                value: (channels.TryGetOrdinal(
                    name: channel,
                    ordinal: out var ordinal
                )
                ? ordinal
                : -1)
            );
        }

        return new CompiledBodyProducer(
            program: program,
            scalars: scalars,
            channels: channelOrdinals,
            target: ((program.Target is { } target)
            ? FixedBodyTargetSource.Compile(
                    registers: targets,
                    source: target
                )
            : null)
        );
    }
    /// <summary>Reads one validated fixed-point scalar by name.</summary>
    public FixedQ4816 Scalar(string name) => m_scalars[name];
}
/// <summary>One compiled fact-triggered effect list.</summary>
/// <summary>One compiled fact trigger: the engine fact, the flattened additional gate, the edge/level mode, and the
/// effects a fire applies in order.</summary>
/// <param name="Fact">The engine fact.</param>
/// <param name="Gate">The flattened additional conjunction, empty when none is authored.</param>
/// <param name="Mode">Whether the trigger is level- or edge-fired (see <see cref="ActionTriggerMode"/>).</param>
/// <param name="Effects">The compiled effects, in authored order.</param>
public readonly record struct CompiledFactTrigger(ActionFact Fact, CompiledPredicate[] Gate, ActionTriggerMode Mode, CompiledBodyInstruction[] Effects);
