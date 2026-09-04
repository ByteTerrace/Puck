using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Maths;
using Puck.Physics.Motion;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>One locomotion kit — a world-definition row naming a way of moving: the body motion program it runs under,
/// the motion tuning its
/// bodies compile, its producer arguments, and its action-lane bindings. Every game-flavored movement noun is a
/// row of this data, never an engine enum; the census echo prints these names. <see cref="Name"/> is the kit's
/// kebab-case name (the census echo token); <see cref="BodyMotionProgram"/> names the body motion program the
/// kit's bodies execute; <see cref="Motion"/> is the locomotion tuning the kit's bodies compile (a seat's profile
/// speeds still override its speed fields) — see <see cref="WorldMotion"/>.</summary>
/// <remarks><see cref="Collider"/> is the kit's body volume solved against the world contact field, or
/// <see langword="null"/> for a kit with no volume (never solved against the field), omitted from the wire when
/// null. <see cref="BodyContact"/> is whether bodies wearing this kit overlap one another or participate in
/// physical depenetration — world geometry still uses <see cref="Collider"/> in either mode. <see cref="Mass"/> is the
/// body's gravitational mass in the same units a <c>gravity.attractors</c> row uses; zero (the default) makes a body a
/// target that is pulled but pulls nothing. <see cref="Rigid"/> is a distinct facet: presence hands the kit's bodies
/// to the rigid solver instead of a locomotion motion program, requires <see cref="Collider"/> (sphere/capsule/box)
/// and <see cref="BodyContact"/> <see cref="WorldBodyContactMode.Solid"/>, and derives its own inertial mass from that
/// collider's shape — it never reads <see cref="Mass"/>.</remarks>
public sealed record WorldKit(
    string Name,
    string BodyMotionProgram,
    WorldMotion Motion,
    [property: JsonPropertyName("producers"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, BodyProgramParameters>? ProducersRaw = null,
    [property: JsonPropertyName("actions"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, ActionSpec>? ActionsRaw = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldCollider? Collider = null,
    WorldBodyContactMode BodyContact = WorldBodyContactMode.Overlap,
    float Mass = 0f,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldRigid? Rigid = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldCarry? Carry = null,
    [property: JsonPropertyName("pad"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, WorldPadElement>? PadRaw = null,
    [property: JsonPropertyName("autonomy"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldAutonomyCadence? AutonomyRaw = null
) {
    /// <summary>Gets the kit's composition bindings, keyed by declared channel name (validated against the world's
    /// channel table — a kit naming an undeclared channel is a dead name; a declared composition channel with no
    /// entry here stays legal and inert per body). Compositions key off channel name, never a lane ordinal. ABSENT
    /// resolves to none.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, ActionSpec> Actions => (ActionsRaw ?? EmptyActions);
    /// <summary>Gets the kit's machine-pad bindings, keyed by the same declared channel names <see cref="Actions"/>
    /// keys off — what a channel MEANS when this kit is worn by a control application whose target is a screen's
    /// booted machine, rather than by a body. One vocabulary, two destinations: a kit binding <c>jump</c> to a body
    /// action and to <see cref="WorldPadElement.South"/> answers both. A kit carrying no pad map cannot be named by
    /// a <see cref="WorldScreenRoute.Kit"/>; ABSENT resolves to none.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, WorldPadElement> Pad => (PadRaw ?? EmptyPad);
    /// <summary>Gets the producer parameter maps keyed by authored producer-program name — ABSENT resolves to
    /// none.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, BodyProgramParameters> Producers => (ProducersRaw ?? EmptyProducers);
    /// <summary>Gets the cadence policy for locally simulated, non-human bodies wearing this kit. ABSENT preserves
    /// full-rate motion and producer steering.</summary>
    [JsonIgnore]
    public WorldAutonomyCadence Autonomy => (AutonomyRaw ?? WorldAutonomyCadence.FullRate);

    private static readonly IReadOnlyDictionary<string, BodyProgramParameters> EmptyProducers = new Dictionary<string, BodyProgramParameters>(comparer: StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, ActionSpec> EmptyActions = new Dictionary<string, ActionSpec>(comparer: StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, WorldPadElement> EmptyPad = new Dictionary<string, WorldPadElement>(comparer: StringComparer.Ordinal);
}
/// <summary>Independent deterministic update cadences for non-human bodies. Human-occupied peers and local seats
/// always run at the authority's full rate. A zero value means every authority tick; a positive interval batches
/// elapsed engine time and phases bodies across that interval, preserving rates while trading response granularity
/// for crowd scale. Submitted input and command-side channel presses immediately promote a body to full-rate steps,
/// and a timed press keeps it there through release. Motion batching is only valid for overlap bodies; solid-body
/// kits must advance every authority tick so dynamic contact remains exact.</summary>
/// <param name="MotionSeconds">How often the body's physics/motion program advances.</param>
/// <param name="SteeringSeconds">How often its selected producer refreshes steering. The most recent image is reused
/// between refreshes.</param>
public sealed record WorldAutonomyCadence(float MotionSeconds = 0f, float SteeringSeconds = 0f) {
    /// <summary>The greatest supported autonomous update interval.</summary>
    public const float MaximumSeconds = 1f;
    /// <summary>Full authority-rate motion and steering.</summary>
    public static WorldAutonomyCadence FullRate { get; } = new();
}
/// <summary>Declares how a kit responds to other dynamic bodies. Interactions and targeting remain available in
/// both modes; only <see cref="Solid"/> authorizes physical depenetration.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldBodyContactMode>))]
public enum WorldBodyContactMode : byte {
    /// <summary>Bodies may overlap. This is the default; the engine never introduces crowd shoving implicitly.</summary>
    Overlap,

    /// <summary>Two bodies physically depenetrate only when both of their kits select this mode.</summary>
    Solid,
}
/// <summary>A <see cref="WorldKit"/>'s compiled motion program, producer bindings, and action bindings.</summary>
/// <param name="BodyMotionProgram">The compiled body motion program the kit's bodies execute.</param>
/// <param name="Producers">The kit's producer bindings keyed by authored program name.</param>
/// <param name="Actions">The kit's compiled composition bindings, indexed by channel ordinal
/// (<see cref="ChannelLimits.MaxChannels"/> slots; unbound ordinals are <see langword="null"/>) — the channel-name map
/// resolved once against the world's <see cref="WorldChannelTable"/>.</param>
/// <param name="ActionThresholds">The binary crossing threshold for each ordinal in <paramref name="Actions"/>
/// (meaningful only where a binding exists).</param>
/// <param name="ActionShapes">The world's declared channel shape for every ordinal (not just where a binding
/// exists) — the held-image composition (<c>Puck.World.Server.WorldBody.NextIntent</c>) needs a composition
/// ordinal's shape whether or not this kit binds an action to it.</param>
/// <param name="Collider">The kit's compiled body volumes, or <see langword="null"/> for a volumeless kit.</param>
/// <param name="BodyContact">The authored dynamic-body contact mode.</param>
/// <param name="Mass">The compiled gravitational mass.</param>
/// <param name="RoleOrdinals">The authored ordinals resolved for engine motion roles.</param>
/// <param name="RoleMask">The compiled per-ordinal role predicate.</param>
/// <param name="ActionState">The kit's compiled named action-state register file.</param>
/// <param name="Holds">The kit's compiled ordered hold list (<see cref="WorldMotion.Holds"/>), empty
/// for a kit authoring none.</param>
/// <param name="Tuning">The kit's compiled locomotion tuning — speed, turn, and the shaping table — resolved
/// against the world's channel table and <c>dynamics</c> rows here, once, rather than per body.</param>
/// <param name="AutonomousMotionTicks">The non-human motion cadence in engine ticks; zero means every authority tick.</param>
/// <param name="AutonomousSteeringTicks">The non-human producer cadence in engine ticks; zero means every authority tick.</param>
/// <param name="Rigid">The kit's compiled rigid-dynamics facet, or <see langword="null"/> for a locomotion kit.</param>
/// <param name="Carry">The kit's compiled carry facet, or <see langword="null"/> for a kit that cannot pick up a
/// rigid body.</param>
public readonly record struct FixedWorldKit(
    CompiledBodyMotionProgram BodyMotionProgram,
    IReadOnlyDictionary<string, CompiledBodyProducer> Producers,
    CompiledActionSpec?[] Actions,
    FixedQ4816[] ActionThresholds,
    ChannelShape[] ActionShapes,
    FixedWorldCollider? Collider,
    WorldBodyContactMode BodyContact,
    FixedQ4816 Mass,
    RoleChannelOrdinals RoleOrdinals,
    bool[] RoleMask,
    CompiledActionStateSlot[] ActionState,
    FixedBodyHold[] Holds,
    FixedMotionTuning Tuning,
    ulong AutonomousMotionTicks,
    ulong AutonomousSteeringTicks,
    FixedWorldRigid? Rigid = null,
    FixedWorldCarry? Carry = null
) {
    private static (CompiledActionStateSlot[] Slots, Dictionary<string, int> ByName) CompileActionState(IReadOnlyList<ActionStateSlot> bodyState, IReadOnlyList<ActionStateSlot> identityState) {
        var slots = new List<CompiledActionStateSlot>();
        var byName = new Dictionary<string, int>(comparer: StringComparer.Ordinal);

        void Add(IReadOnlyList<ActionStateSlot> declarations, ActionStateLifetime lifetime) {
            foreach (var state in declarations) {
                byName.Add(
                    key: state.Name,
                    value: slots.Count
                );
                slots.Add(item: new CompiledActionStateSlot(
                    Name: state.Name,
                    Kind: state.Kind,
                    InitialValue: ((state.Kind == ActionStateKind.Counter)
                    ? FixedQ4816.FromDouble(value: state.Initial)
                    : FixedQ4816.Zero),
                    InitialTicks: ((state.Kind == ActionStateKind.Timer)
                    ? FixedTickConversion.DurationEngineTicks(seconds: FixedQ4816.FromDouble(value: state.Initial))
                    : 0UL),
                    ResetFact: state.ResetFact,
                    Lifetime: lifetime,
                    PlayerWritable: state.PlayerWritable,
                    Envelope: CompileEnvelope(state: state)
                ));
            }
        }

        Add(
            declarations: bodyState,
            lifetime: ActionStateLifetime.Ephemeral
        );
        Add(
            declarations: identityState,
            lifetime: ActionStateLifetime.Durable
        );

        return (Slots: slots.ToArray(), ByName: byName);
    }
    private static CompiledActionStateEnvelope? CompileEnvelope(ActionStateSlot state) {
        long Compile(float value) => ((state.Kind == ActionStateKind.Counter)
            ? FixedQ4816.FromDouble(value: value).Value
            : checked((long)FixedTickConversion.DurationEngineTicks(seconds: FixedQ4816.FromDouble(value: value)))
        );

        return state.Envelope switch {
            null => null,
            ActionStateEnvelope.Range range => new CompiledActionStateEnvelope(
            Minimum: Compile(value: range.Minimum),
            Maximum: Compile(value: range.Maximum),
            Values: null
        ),
            ActionStateEnvelope.Set set => new CompiledActionStateEnvelope(
            Minimum: 0L,
            Maximum: 0L,
            Values: set.Values.Select(selector: Compile).ToArray()
        ),
            _ => throw new InvalidOperationException(message: $"Unknown action-state envelope '{state.Envelope.GetType().Name}'."),
        };
    }
    private static void RequireProgramRoles(string kitName, CompiledBodyMotionProgram program, RoleChannelOrdinals ordinals) {
        foreach (var role in Enum.GetValues<ChannelRole>()) {
            if (
                program.RequiresRole(role: role) &&
                (ordinals[role] < 0)
            ) {
                throw new InvalidOperationException(message: $"Kit '{kitName}' body motion program '{program.Name}' requires channel role '{role}', but no declared channel claims it.");
            }
        }
    }

    /// <summary>Compiles a kit row's authored floats to fixed point (the once-at-the-boundary rule), resolving its
    /// channel-name-keyed <see cref="WorldKit.Actions"/> and producer maps against the
    /// world's compiled channel table. Validation (<see cref="WorldDefinitionValidator"/>) has already rejected a dead
    /// channel name by the time this runs.</summary>
    /// <param name="kit">The authored kit row.</param>
    /// <param name="channels">The world's compiled channel table.</param>
    /// <param name="targets">The world's compiled target-register table.</param>
    /// <param name="curves">The world's compiled curves-row table — a producer's curve-follow target resolves
    /// against it the same way <paramref name="targets"/> resolves a designated register.</param>
    /// <param name="navigation">The world's compiled navigation-domain table.</param>
    /// <param name="programs">The world's compiled body motion programs keyed by stable name.</param>
    /// <param name="programRows">The world's authored body motion program rows keyed by the same names — the target
    /// source a producer senses is authored vocabulary, so it is read here rather than carried on the compiled
    /// instruction form.</param>
    /// <param name="creations">The creation rows a <see cref="WorldCollider.FromCreation"/> may reference.</param>
    /// <param name="bodyState">The world's body-owned ephemeral state declarations.</param>
    /// <param name="identityState">The world's identity-owned durable state declarations.</param>
    /// <param name="dynamics">The world's declared <c>dynamics</c> rows, resolved against <paramref name="kit"/>'s
    /// motion row's own declared dynamics row name (validation has already refused a dangling name).</param>
    /// <param name="simulationRateHz">The world's own simulation rate — the step width a resolved dynamics row's
    /// propagator compiles against (validation has already refused a resolved name at rate 0), and a curve-follow
    /// producer's per-tick arc step divisor.</param>
    public static FixedWorldKit Compile(WorldKit kit, WorldChannelTable channels, WorldTargetRegisterTable targets, WorldCurveTable curves, WorldNavigationDomainTable navigation, IReadOnlyDictionary<string, CompiledBodyMotionProgram> programs, IReadOnlyDictionary<string, BodyMotionProgram> programRows, IReadOnlyList<WorldPrototype> creations, IReadOnlyList<ActionStateSlot> bodyState, IReadOnlyList<ActionStateSlot> identityState, IReadOnlyList<WorldDynamicsRow> dynamics, int simulationRateHz) {
        var actions = new CompiledActionSpec?[ChannelLimits.MaxChannels];
        var thresholds = new FixedQ4816[ChannelLimits.MaxChannels];
        // Every ordinal, not just bound ones — a composition channel's shape is a WORLD property, not a per-kit one,
        // and the held-image overlay composes it whether or not this kit binds an action there.
        var shapes = new ChannelShape[ChannelLimits.MaxChannels];
        var roleMask = new bool[ChannelLimits.MaxChannels];
        var program = programs[kit.BodyMotionProgram];

        var (actionState, stateSlots) = CompileActionState(
            bodyState: bodyState,
            identityState: identityState
        );

        for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
            shapes[ordinal] = channels.Shape(ordinal: ordinal);
            roleMask[ordinal] = channels.IsRole(ordinal: ordinal);
        }

        foreach (var (name, spec) in kit.Actions) {
            if (!channels.TryGetOrdinal(
                name: name,
                ordinal: out var ordinal
            )) {
                continue;
            }

            actions[ordinal] = BodyActionSpecFactory.Compile(
                spec: spec,
                stateSlots: stateSlots,
                program: program,
                actionName: $"{kit.Name}.{name}"
            );
            thresholds[ordinal] = channels.Threshold(ordinal: ordinal);
        }

        var roleOrdinals = channels.RoleOrdinals;
        var producers = new Dictionary<string, CompiledBodyProducer>(
            capacity: kit.Producers.Count,
            comparer: StringComparer.Ordinal
        );

        foreach (var (name, parameters) in kit.Producers) {
            producers.Add(
                key: name,
                value: CompiledBodyProducer.Compile(
                    program: programs[name],
                    source: programRows[name].Target,
                    parameters: parameters,
                    channels: channels,
                    targets: targets,
                    curves: curves,
                    navigation: navigation,
                    simulationRateHz: simulationRateHz
                )
            );
        }

        RequireProgramRoles(
            kitName: kit.Name,
            program: program,
            ordinals: roleOrdinals
        );

        var collider = FixedWorldCollider.Compile(
            collider: kit.Collider,
            creations: creations
        );
        var rigid = ((kit.Rigid is { } rigidRow)
            ? FixedWorldRigid.Compile(
                rigid: rigidRow,
                collider: collider!.Value
            )
            : (FixedWorldRigid?)null
        );
        var carry = ((kit.Carry is { } carryRow)
            ? FixedWorldCarry.Compile(carry: carryRow)
            : (FixedWorldCarry?)null
        );

        var tuning = WorldMotionTuningFactory.Compile(
            channels: channels,
            dynamics: dynamics,
            simulationRateHz: simulationRateHz,
            tuning: kit.Motion
        );

        // The speed-held channel is a HELD read, not an Actions binding — it needs its threshold in ActionThresholds
        // regardless of whether kit.Actions also binds a press/release effect there (the loop above only writes a
        // threshold where an ActionSpec exists), so WorldBody's held-channel test compares against the channel's
        // OWN declared threshold rather than the array's zero default.
        if (tuning.Speed.HeldOrdinal >= 0) {
            thresholds[tuning.Speed.HeldOrdinal] = channels.Threshold(ordinal: tuning.Speed.HeldOrdinal);
        }

        // Every shaping row's flattened gate may test a `held` channel the kit binds no action to (a drift row).
        // MotionGateOpen compares the SAME channelThresholds array every other held read uses, so each one needs
        // its own declared threshold here too — never the array's zero default, which a channel at rest (raw 0)
        // would then read as held.
        foreach (var row in tuning.Shaping) {
            foreach (var predicate in row.When) {
                if (
                    (predicate.Kind == CompiledPredicateKind.Held) &&
                    (predicate.ChannelOrdinal >= 0)
                ) {
                    thresholds[predicate.ChannelOrdinal] = channels.Threshold(ordinal: predicate.ChannelOrdinal);
                }
            }
        }

        return new FixedWorldKit(
            BodyMotionProgram: program,
            Producers: producers,
            Actions: actions,
            ActionThresholds: thresholds,
            ActionShapes: shapes,
            Collider: collider,
            BodyContact: kit.BodyContact,
            Mass: FixedQ4816.FromDouble(value: kit.Mass),
            RoleOrdinals: roleOrdinals,
            RoleMask: roleMask,
            ActionState: actionState,
            Holds: WorldHoldFactory.Compile(
                channels: channels,
                holds: kit.Motion.Holds
            ),
            Tuning: tuning,
            AutonomousMotionTicks: ((kit.Autonomy.MotionSeconds > 0f)
                ? FixedTickConversion.DurationEngineTicks(seconds: FixedQ4816.FromDouble(value: kit.Autonomy.MotionSeconds))
                : 0UL),
            AutonomousSteeringTicks: ((kit.Autonomy.SteeringSeconds > 0f)
                ? FixedTickConversion.DurationEngineTicks(seconds: FixedQ4816.FromDouble(value: kit.Autonomy.SteeringSeconds))
                : 0UL),
            Rigid: rigid,
            Carry: carry
        );
    }
}
