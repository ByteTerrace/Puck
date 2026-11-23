using Puck.Abstractions.Documents;
using Puck.Abstractions.Collections;
using System.Text.Json.Serialization;
using Puck.Maths;
using Puck.Physics.Motion;

namespace Puck.World;

/// <summary>The authored values a slot admits in this world: a player-writable durable slot's input, a rule's or a
/// kit's write, and a value carried in from another world all settle through it.</summary>
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
    public sealed record Set(IReadOnlyList<float> Values) : ActionStateEnvelope {
        /// <summary>The admitted values.</summary>
        public IReadOnlyList<float> Values { get => field; init => field = OwnedLists.Freeze(items: value)!; } = OwnedLists.Freeze(items: Values)!;
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
    [property: DocumentText("a per-body slot name, the body-state lane's own namespace")] string Name,
    ActionStateKind Kind,
    float Initial = 0f,
    ActionFact? ResetFact = null,
    bool PlayerWritable = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ActionStateEnvelope? Envelope = null
) : IStateSlot {
    StateParticipantRole IStateSlot.Role => ((Kind == ActionStateKind.Timer)
        ? StateParticipantRole.Timer
        : StateParticipantRole.Counter
    );
}
/// <summary>An authored fixed-phase body motion program.</summary>
/// <param name="Name">The stable name kits use to select the program.</param>
/// <param name="Version">The instruction-set version.</param>
/// <param name="Kind">The declared program profile that gates operations and registers.</param>
/// <param name="Operations">The selected domain operations; their phases are intrinsic and cannot be reordered.</param>
/// <param name="Target">The single source supplying the program's target, when it uses target-aware operations.</param>
/// <param name="PlanarTarget">The parameters of <see cref="BodyMotionOp.ComputePlanarTargetVelocity"/>, or
/// <see langword="null"/> for a target at the full move speed in every direction.</param>
public sealed record BodyMotionProgram(
    [property: DocumentText("a body motion program name")] string Name,
    [property: DocumentText("a schema or program version")] string Version,
    BodyProgramKind? Kind,
    IReadOnlyList<BodyMotionOp> Operations,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BodyTargetSource? Target = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BodyPlanarTarget? PlanarTarget = null
) {
    /// <summary>The supported body-motion instruction-set version.</summary>
    public const string CurrentVersion = CompiledBodyMotionProgram.SupportedVersion;
}
/// <summary>How fast <see cref="BodyMotionOp.ComputePlanarTargetVelocity"/> lets a body travel across and against its
/// heading, as fractions of its move speed. The commanded target decomposes against the heading the program's
/// <see cref="BodyMotionOp.ResolveYawAttitudeAndPlanarFrame"/> resolves: the lateral part scales by
/// <paramref name="Strafe"/> and a part opposing the heading by <paramref name="Backward"/>, whatever frame the seat
/// composed the stick in. A camera-framed strafe seat, whose heading follows the camera, therefore strafes and
/// backpedals slower than it runs forward.</summary>
/// <param name="Strafe">The fraction of the move speed travel across the heading reaches, in <c>[0, 1]</c>.</param>
/// <param name="Backward">The fraction of the move speed travel against the heading reaches, in <c>[0, 1]</c>.</param>
public sealed record BodyPlanarTarget(float Strafe = 1f, float Backward = 1f);
/// <summary>The document intake for <see cref="CompiledBodyMotionProgram"/> — the one place an authored
/// <see cref="BodyMotionProgram"/> row becomes the engine's compiled instruction form.</summary>
public static class BodyMotionProgramFactory {
    /// <summary>Compiles and validates an authored program in one construction-time walk.</summary>
    /// <param name="program">The authored program row.</param>
    /// <returns>The compiled program.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="program"/> is <see langword="null"/>.</exception>
    /// <exception cref="BodyMotionProgramException">The authored shape is refused.</exception>
    public static CompiledBodyMotionProgram Compile(BodyMotionProgram program) {
        ArgumentNullException.ThrowIfNull(argument: program);

        return CompiledBodyMotionProgram.Compile(
            name: program.Name,
            version: program.Version,
            kind: program.Kind,
            operations: program.Operations,
            planarTarget: ((program.PlanarTarget is { } planarTarget)
                ? new FixedPlanarTarget(
                    Backward: FixedQ4816.FromDouble(value: planarTarget.Backward),
                    Strafe: FixedQ4816.FromDouble(value: planarTarget.Strafe)
                )
                : null)
        );
    }
}
/// <summary>The world channel-role questions a compiled program answers. The role vocabulary is the world's channel
/// table's, so the query lives beside it rather than inside the engine's instruction core.</summary>
public static class BodyMotionProgramRoles {
    /// <summary>Reports whether the program's selected instructions read <paramref name="role"/>.</summary>
    /// <param name="program">The compiled program.</param>
    /// <param name="role">The engine motion role.</param>
    /// <returns><see langword="true"/> when some selected instruction reads the role.</returns>
    public static bool RequiresRole(this CompiledBodyMotionProgram program, ChannelRole role) => role switch {
        ChannelRole.MoveAdvance or ChannelRole.MoveStrafe => (program.Contains(operation: BodyMotionOp.ComputePlanarTargetVelocity)
            || program.Contains(operation: BodyMotionOp.SnapYawToPlanarIntent)
            || program.Contains(operation: BodyMotionOp.ComputeLocalTargetVelocity)),
        ChannelRole.Turn => (program.Contains(operation: BodyMotionOp.ResolveYawAttitudeAndPlanarFrame)
            || program.Contains(operation: BodyMotionOp.IntegrateLocalAttitude)
            || program.Contains(operation: BodyMotionOp.ResolveDriveFrame)),
        // A hold row's own thrust reads MoveUp too, but that need is per-row data (WorldHold.Thrust), not a shape
        // this program-only query can see; WorldDefinitionValidator.ValidateHolds refuses a positive thrust by name
        // against a world declaring no MoveUp channel, so this query answers only for the program's OWN ops.
        ChannelRole.MoveUp => program.Contains(operation: BodyMotionOp.ComputeLocalTargetVelocity),
        // ResolveDriveFrame reads Pitch only under a positive pitchRate, so Pitch is not required for it — a
        // pitchless world's flying drive pitch reads zero rather than refusing the kit.
        ChannelRole.Pitch or ChannelRole.Roll => program.Contains(operation: BodyMotionOp.IntegrateLocalAttitude),
        // SnapYawToPlanarIntent reads FaceX/FaceZ only when a world declares them (FaceY rides along for
        // attitude-bearing arms) — a faceless world's snap stays movement-facing rather than refusing the kit.
        _ => false,
    };
}
/// <summary>The document intake for <see cref="CompiledActionSpec"/> — the one place an authored
/// <see cref="ActionSpec"/> becomes the engine's compiled trigger form.</summary>
public static partial class BodyActionSpecFactory {
    private static CompiledBodyInstruction CompileEffect(ActionEffect effect, IReadOnlyDictionary<string, int> stateSlots, CompiledBodyMotionProgram program, string actionName) {
        var instruction = effect switch {
            _ when WorldBodyEffects.IsBodyMotion(effect) => CompileBodyMotion(
                actionName: actionName,
                effect: effect
            ),
            ActionEffect.SetState set => new CompiledBodyInstruction(
            Operation: BodyMotionOp.SetState,
            Value: NumericLiteral.ToFixed(value: RequireBodyEffectValue(
                value: set.Value,
                fromState: set.FromState?.Spelling,
                fromKey: set.FromKey?.Spelling,
                valueSeconds: set.ValueSeconds,
                expression: set.Expression,
                actionName: actionName,
                effectName: "setState",
                state: set.State.Spelling
            )),
            Direction: default,
            DurationTicks: 0UL,
            StateSlot: ResolveState(
                name: set.State.Spelling,
                stateSlots: stateSlots,
                key: set.Key?.Spelling,
                effect: "setState"
            ),
            Target: set.Target,
            StateName: set.State.Spelling
        ),
            ActionEffect.AddState add => new CompiledBodyInstruction(
            Operation: BodyMotionOp.AddState,
            Value: NumericLiteral.ToFixed(value: RequireBodyEffectValue(
                value: add.Value,
                fromState: add.FromState?.Spelling,
                fromKey: add.FromKey?.Spelling,
                valueSeconds: add.ValueSeconds,
                expression: add.Expression,
                actionName: actionName,
                effectName: "addState",
                state: add.State.Spelling
            )),
            Direction: default,
            DurationTicks: 0UL,
            StateSlot: ResolveState(
                name: add.State.Spelling,
                stateSlots: stateSlots,
                key: add.Key?.Spelling,
                effect: "addState"
            ),
            Target: add.Target,
            StateName: add.State.Spelling
        ),
            WorldEffect.StartTimer timer => new CompiledBodyInstruction(
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
            StateName: generate.Row.Spelling
        ),
            // upsertHudPanel/removeHudPanel/upsertPlacement/removePlacement author WORLD document rows — a per-body
            // action has none of its own, so these are refused BY NAME here rather than parsed and discarded
            // (legitimate only inside a WorldRule; see WorldFactsCompiler.CompileEffect).
            WorldEffect.UpsertHudPanel or WorldEffect.RemoveHudPanel or WorldEffect.UpsertPlacement or WorldEffect.RemovePlacement =>
                throw new InvalidOperationException(message: $"Action '{actionName}' uses effect '{effect.GetType().Name}', which has no body-scope meaning — it authors a WORLD document row and is admissible only inside a world rule's own effects."),
            // A per-body action compiles to a flat CompiledBodyInstruction stream with no branch of its own.
            ActionEffect.If =>
                throw new InvalidOperationException(message: $"Action '{actionName}' uses effect 'if', which has no body-scope meaning — it is admissible only inside a world rule's own effects."),
            // save writes the WORLD's own file — a per-body action has no world file of its own to save, so this is
            // refused BY NAME here too (legitimate only inside a WorldRule; see WorldFactsCompiler.CompileEffect and
            // WorldEffect.Save's own remarks).
            WorldEffect.Save =>
                throw new InvalidOperationException(message: $"Action '{actionName}' uses effect 'Save', which has no body-scope meaning — a per-body action has no world file of its own to save, and is admissible only inside a world rule's own effects."),
            WorldEffect.Pose =>
                throw new InvalidOperationException(message: $"Action '{actionName}' uses effect 'Pose', which has no body-scope meaning — it teleports a body the world names, and is admissible only inside a world rule's own effects."),
            _ => throw new InvalidOperationException(message: $"Action '{actionName}' contains an unknown effect kind."),
        };

        if (!program.Admits(operation: instruction.Operation)) {
            throw new BodyMotionProgramException(
                refusal: BodyMotionProgramRefusal.OpcodeInadmissible,
                programName: program.Name,
                detail: ((instruction.Operation == BodyMotionOp.TimedMove)
                    ? $"action '{actionName}' timedMove requires a yaw-frame program with CommitPose"
                    : $"action '{actionName}' opcode '{instruction.Operation}' is inadmissible for program kind '{program.Kind}'")
            );
        }

        return instruction;
    }
    // A body-motion effect lowers through the check a world rule's lowers through; the document validator has already
    // refused the effect by the same check with the world's register table in hand.
    private static CompiledBodyInstruction CompileBodyMotion(ActionEffect effect, string actionName) {
        if (!WorldBodyEffects.TryLower(
            effect: effect,
            form: out var form,
            reason: out var reason,
            refusal: out _,
            registerDeclared: null,
            scope: WorldBodyEffectScope.Kit
        )) {
            throw new InvalidOperationException(message: $"Action '{actionName}': {reason}.");
        }

        return new CompiledBodyInstruction(
            Curve: form.Curve,
            Direction: form.Direction,
            UseMoveDirection: form.UseMoveDirection,
            DurationTicks: form.DurationTicks,
            Operation: form.Operation,
            StateName: form.Register,
            StateSlot: -1,
            Target: form.Target,
            Value: form.Value
        );
    }
    private static CompiledTrigger? CompileTrigger(ActionTrigger? trigger, List<ActionFact> recencyFacts, List<ulong> recencyWindows, IReadOnlyDictionary<string, int> stateSlots, CompiledBodyMotionProgram program, string actionName) {
        if (trigger is null) {
            return null;
        }

        var gate = CompileGate(
            predicate: trigger.Gate,
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
            Gate: gate,
            LatchTicks: DurationTicks(seconds: trigger.LatchSeconds),
            Effects: effects
        );
    }
    // Authored seconds → engine ticks, rounded up from the decimal the float field was authored as.
    private static ulong DurationTicks(float seconds) => FixedTickConversion.DurationEngineTicks(seconds: ((decimal)seconds));
    // A per-body action-state slot has no world state row to copy from — setState/addState's live 'fromState'/
    // 'fromKey' spelling is legitimate only in a world rule (WorldFactsCompiler); a body-scope effect always writes an
    // authored constant, so 'value' is required here on the same terms compareState's own body-scope 'value' is.
    private static decimal RequireBodyEffectValue(decimal? value, string? fromState, string? fromKey, decimal? valueSeconds, ExpressionProgram? expression, string actionName, string effectName, string state) {
        if (
            (fromState is not null) ||
            (fromKey is not null)
        ) {
            throw new InvalidOperationException(message: $"Action '{actionName}' effect '{effectName}' on action state '{state}' carries a 'fromState'/'fromKey' — a per-body action-state slot has no world state row to copy from; a live copy source is legitimate only in a world rule.");
        }

        if (valueSeconds is not null) {
            throw new InvalidOperationException(message: $"Action '{actionName}' effect '{effectName}' on action state '{state}' carries a 'valueSeconds' — that spelling is WORLD SCOPE ONLY (a state row a world rule decrements once per simulation tick); a per-body effect writes an authored constant via 'value', or starts a proper timer via 'startTimer'.");
        }

        if (expression is not null) {
            throw new InvalidOperationException(message: $"Action '{actionName}' effect '{effectName}' on action state '{state}' carries an 'expression' — expressions read world state and are admitted only in a world rule.");
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
    /// <returns>The compiled binding, or <see langword="null"/> for an unbound lane.</returns>
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
            var factGate = CompileGate(
                predicate: rule.Gate,
                recencyFacts: recencyFacts,
                recencyWindows: recencyWindows,
                stateSlots: stateSlots
            );

            return new CompiledFactTrigger(
                Fact: rule.Fact,
                Gate: factGate,
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
/// <summary>One producer program and a kit's fixed-point arguments for it, resolved to
/// <see cref="BodyProducerParameter"/> ordinals once at kit-compile time — the tick path indexes
/// <see cref="Scalar"/>/<see cref="Channel"/> directly, reading no dictionary and no authored string.</summary>
public sealed class CompiledBodyProducer {
    private static readonly int ParameterCount = Enum.GetValues<BodyProducerParameter>().Length;

    private readonly int[] m_channelOrdinals;
    private readonly FixedQ4816[] m_scalars;

    private CompiledBodyProducer(CompiledBodyMotionProgram program, FixedQ4816[] scalars, int[] channelOrdinals, FixedBodyTargetSource? target, FixedWorldFlockProfile? flock, bool roamActive) {
        Program = program;
        m_scalars = scalars;
        m_channelOrdinals = channelOrdinals;
        Target = target;
        Flock = flock;
        RoamActive = roamActive;
    }

    /// <summary>Gets bounded local-perception and steering parameters, when authored.</summary>
    public FixedWorldFlockProfile? Flock { get; }
    /// <summary>Gets the compiled producer program.</summary>
    public CompiledBodyMotionProgram Program { get; }
    /// <summary>Gets a value indicating whether <see cref="BodyMotionOp.ProduceSteeringIntent"/>'s roam shape runs
    /// for this producer — always true for a non-sensing steering program, and derived at compile time from a
    /// roam-exclusive <see cref="BodyProducerParameterVocabulary.SteeringScalars"/> member for a sensing program;
    /// never authored as a separate flag.</summary>
    public bool RoamActive { get; }
    /// <summary>Gets the compiled target source, when this producer senses a target.</summary>
    public FixedBodyTargetSource? Target { get; }

    // The scalar ordinals CHANNEL args (Press) may legitimately name, alongside the op-declared scalar set — kept
    // separate from BodyProducerParameterVocabulary.RequiredScalars, which answers only for the SCALAR-valued
    // argument space.
    private static bool AdmitsChannelArgument(CompiledBodyMotionProgram program, BodyProducerParameter parameter) => ((parameter == BodyProducerParameter.Press) && program.Contains(operation: BodyMotionOp.ProduceSteeringIntent));

    /// <summary>Reads one validated channel ordinal, or <c>-1</c> when the kit binds none.</summary>
    public int Channel(BodyProducerParameter parameter) => m_channelOrdinals[((int)parameter)];
    /// <summary>Compiles a kit's producer parameters, refusing an authored <c>scalars</c>/<c>channels</c> key that
    /// names no parameter this program's selected operations read, or that omits one they require.</summary>
    /// <param name="program">The compiled producer program.</param>
    /// <param name="source">The program's authored target source, or <see langword="null"/> when it senses none.</param>
    /// <param name="parameters">The kit's authored arguments for the program.</param>
    /// <param name="channels">The world's compiled channel table.</param>
    /// <param name="targets">The world's compiled target-register table.</param>
    /// <param name="curves">The world's compiled curves-row table.</param>
    /// <param name="navigation">The world's compiled navigation-domain table.</param>
    /// <param name="simulationRateHz">The world's own simulation rate — a curve-follow target's per-tick arc step
    /// divisor.</param>
    /// <returns>The compiled producer binding.</returns>
    /// <exception cref="BodyMotionProgramException">An authored key names no parameter this program's operations
    /// read, or a required parameter is missing.</exception>
    public static CompiledBodyProducer Compile(CompiledBodyMotionProgram program, BodyTargetSource? source, BodyProgramParameters parameters, WorldChannelTable channels, WorldTargetRegisterTable targets, WorldCurveTable curves, WorldNavigationDomainTable navigation, int simulationRateHz) {
        var requiredScalars = ResolveRequiredScalars(
            parameters: parameters,
            program: program,
            target: source
        );
        var producesSteering = program.Contains(operation: BodyMotionOp.ProduceSteeringIntent);
        var senses = program.Contains(operation: BodyMotionOp.SenseNearestInCone);
        var roamActive = (producesSteering && (!senses || BodyProducerParameterVocabulary.IsRoamAuthored(scalars: parameters.Scalars)));

        var scalars = new FixedQ4816[ParameterCount];

        foreach (var (name, value) in parameters.Scalars) {
            if (
                !BodyProducerParameterVocabulary.TryParse(
                name: name,
                parameter: out var parameter
            ) ||
                !requiredScalars.Contains(item: parameter)
            ) {
                throw new BodyMotionProgramException(
                    refusal: BodyMotionProgramRefusal.ParameterUnknown,
                    programName: program.Name,
                    detail: $"scalar '{name}' names no parameter this program's selected operations read"
                );
            }

            scalars[((int)parameter)] = FixedQ4816.FromDouble(value: value);
        }
        foreach (var required in requiredScalars) {
            if (!parameters.Scalars.ContainsKey(key: BodyProducerParameterVocabulary.Name(parameter: required))) {
                throw new BodyMotionProgramException(
                    refusal: BodyMotionProgramRefusal.ParameterMissing,
                    programName: program.Name,
                    detail: $"scalar '{BodyProducerParameterVocabulary.Name(parameter: required)}' is required by this program's selected operations"
                );
            }
        }

        var channelOrdinals = new int[ParameterCount];

        Array.Fill(
            array: channelOrdinals,
            value: -1
        );

        foreach (var (name, channel) in parameters.Channels) {
            if (
                !BodyProducerParameterVocabulary.TryParse(
                name: name,
                parameter: out var parameter
            ) ||
                !AdmitsChannelArgument(
                parameter: parameter,
                program: program
            )
            ) {
                throw new BodyMotionProgramException(
                    refusal: BodyMotionProgramRefusal.ParameterUnknown,
                    programName: program.Name,
                    detail: $"channel argument '{name}' names no parameter this program's selected operations read"
                );
            }

            channelOrdinals[((int)parameter)] = (channels.TryGetOrdinal(
                name: channel,
                ordinal: out var ordinal
            )
                ? ordinal
                : -1
            );
        }

        return new CompiledBodyProducer(
            program: program,
            flock: ((parameters.Flock is { } flock)
            ? new FixedWorldFlockProfile(
                    navigation: navigation,
                    source: flock
                )
            : null),
            scalars: scalars,
            channelOrdinals: channelOrdinals,
            roamActive: roamActive,
            target: ((source is { } target)
            ? FixedBodyTargetSource.Compile(
                    curves: curves,
                    navigation: navigation,
                    registers: targets,
                    simulationRateHz: simulationRateHz,
                    source: target
                )
            : null)
        );
    }
    /// <summary>Computes one producer program's full required-scalar set from its selected operations and authored
    /// arguments — the ONE derivation both this type's <see cref="Compile"/> and the schema validator
    /// (<c>WorldDefinitionValidator.Motion.cs</c>'s <c>ValidateProducerParameters</c>) read, so a required-set rule
    /// can never drift between compile time and validation time.</summary>
    /// <param name="program">The compiled producer program.</param>
    /// <param name="target">The program's authored target source, or <see langword="null"/> when it senses none.</param>
    /// <param name="parameters">The kit's authored arguments for the program.</param>
    /// <returns>The set of parameters this program's selected operations and authored arguments require.</returns>
    public static IReadOnlySet<BodyProducerParameter> ResolveRequiredScalars(CompiledBodyMotionProgram program, BodyTargetSource? target, BodyProgramParameters parameters) {
        var required = new HashSet<BodyProducerParameter>();

        foreach (var op in Enum.GetValues<BodyMotionOp>()) {
            if (program.Contains(operation: op)) {
                required.UnionWith(other: BodyProducerParameterVocabulary.RequiredScalars(op: op));
            }
        }

        var senses = program.Contains(operation: BodyMotionOp.SenseNearestInCone);
        var producesSteering = program.Contains(operation: BodyMotionOp.ProduceSteeringIntent);

        // With sensing, roam is an optional fallback and its exclusive scalar presence activates it. Without
        // sensing, roam is the op's only reachable shape, so selecting ProduceSteeringIntent requires it outright;
        // accepting an empty bare producer would compile a permanently inert program.
        if (
            producesSteering &&
            (!senses || BodyProducerParameterVocabulary.IsRoamAuthored(scalars: parameters.Scalars))
        ) {
            required.UnionWith(other: BodyProducerParameterVocabulary.SteeringScalars);
        }
        // The approach shape ProduceSteeringIntent runs is reachable only on a tick this program's own sensing
        // found a target — never on a bare roam producer, which can only ever run the roam shape.
        if (
            senses &&
            producesSteering
        ) {
            required.UnionWith(other: BodyProducerParameterVocabulary.SteeringApproachScalars);
        }
        // SenseTarget's own release-radius hysteresis (WorldBody.Step.cs) reads this scalar only for a NON-flock
        // producer sensing a Sensed source — a flock's own bounded-perception cadence retains observations by a
        // different mechanism and never reads it.
        if (
            senses &&
            (target is BodyTargetSource.Sensed) &&
            (parameters.Flock is null)
        ) {
            required.Add(item: BodyProducerParameter.ReleaseRadius);
        }

        return required;
    }
    /// <summary>Reads one validated fixed-point scalar.</summary>
    public FixedQ4816 Scalar(BodyProducerParameter parameter) => m_scalars[((int)parameter)];
}
