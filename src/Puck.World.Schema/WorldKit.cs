using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Maths;
using Puck.Physics.Motion;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>One locomotion kit — a world-definition row naming a way of moving: the body motion program it runs under,
/// the motion model its
/// bodies compile, its producer arguments, and its action-lane bindings. Every game-flavored movement noun is a
/// row of this data, never an engine enum; the census echo prints these names. <see cref="Name"/> is the kit's
/// kebab-case name (the census echo token); <see cref="BodyMotionProgram"/> names the body motion program the
/// kit's bodies execute; <see cref="Motion"/> is the locomotion model the kit's bodies compile (a seat's profile
/// speeds still override its speed fields) — see <see cref="WorldMotionModel"/>.</summary>
/// <remarks><see cref="Collider"/> is the kit's body volume solved against the world contact field, or
/// <see langword="null"/> for a kit with no volume (never solved against the field), omitted from the wire when
/// null. <see cref="BodyContact"/> is whether bodies wearing this kit overlap one another or participate in
/// physical depenetration — world geometry still uses <see cref="Collider"/> in either mode. <see cref="Mass"/> is the
/// body's gravitational mass in the same units a <c>gravity.attractors</c> row uses; zero (the default) makes a body a
/// target that is pulled but pulls nothing.</remarks>
public sealed record WorldKit(
    string Name,
    string BodyMotionProgram,
    WorldMotionModel Motion,
    [property: JsonPropertyName("producers"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, BodyProgramParameters>? ProducersRaw = null,
    [property: JsonPropertyName("actions"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, ActionSpec>? ActionsRaw = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldCollider? Collider = null,
    WorldBodyContactMode BodyContact = WorldBodyContactMode.Overlap,
    float Mass = 0f,
    [property: JsonPropertyName("pad"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, WorldPadElement>? PadRaw = null
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

    private static readonly IReadOnlyDictionary<string, BodyProgramParameters> EmptyProducers = new Dictionary<string, BodyProgramParameters>(comparer: StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, ActionSpec> EmptyActions = new Dictionary<string, ActionSpec>(comparer: StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, WorldPadElement> EmptyPad = new Dictionary<string, WorldPadElement>(comparer: StringComparer.Ordinal);
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
/// <param name="SprintChannelOrdinal">The ordinal <see cref="WorldMotionModel.Grounded.SprintChannel"/> resolved
/// to, or <c>-1</c> for a kit with no sprint capability (including a kit whose declared model carries none).</param>
/// <param name="DriftChannelOrdinal">The ordinal <see cref="WorldDriveDrift.Channel"/> resolved to, or <c>-1</c>
/// for a kit that cannot drift (every kit authoring no drive row, and every drive row authoring no drift).</param>
/// <param name="RoleOrdinals">The authored ordinals resolved for engine motion roles.</param>
/// <param name="RoleMask">The compiled per-ordinal role predicate.</param>
/// <param name="ActionState">The kit's compiled named action-state register file.</param>
/// <param name="PlanarDynamics">The compiled second-order follower the kit's declared motion model's <c>dynamics</c>
/// row names, or <see langword="null"/> when it shapes planar velocity through its response table instead
/// (validation has already refused any other combination by the time this compiles).</param>
/// <param name="Holds">The kit's compiled ordered hold list (<see cref="WorldMotionModel.Grounded.Holds"/>), empty
/// for a kit authoring none.</param>
public readonly record struct FixedWorldKit(
    CompiledBodyMotionProgram BodyMotionProgram,
    IReadOnlyDictionary<string, CompiledBodyProducer> Producers,
    CompiledActionSpec?[] Actions,
    FixedQ4816[] ActionThresholds,
    ChannelShape[] ActionShapes,
    FixedWorldCollider? Collider,
    WorldBodyContactMode BodyContact,
    FixedQ4816 Mass,
    int SprintChannelOrdinal,
    int DriftChannelOrdinal,
    RoleChannelOrdinals RoleOrdinals,
    bool[] RoleMask,
    CompiledActionStateSlot[] ActionState,
    FixedBodyHold[] Holds,
    FixedMotionDynamics? PlanarDynamics = null
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
    /// <param name="programs">The world's compiled body motion programs keyed by stable name.</param>
    /// <param name="programRows">The world's authored body motion program rows keyed by the same names — the target
    /// source a producer senses is authored vocabulary, so it is read here rather than carried on the compiled
    /// instruction form.</param>
    /// <param name="creations">The creation rows a <see cref="WorldCollider.FromCreation"/> may reference.</param>
    /// <param name="bodyState">The world's body-owned ephemeral state declarations.</param>
    /// <param name="identityState">The world's identity-owned durable state declarations.</param>
    /// <param name="dynamics">The world's declared <c>dynamics</c> rows, resolved against <paramref name="kit"/>'s
    /// motion model's own declared row name (validation has already refused a dangling name).</param>
    /// <param name="simulationRateHz">The world's own simulation rate — the step width a resolved dynamics row's
    /// propagator compiles against (validation has already refused a resolved name at rate 0), and a curve-follow
    /// producer's per-tick arc step divisor.</param>
    public static FixedWorldKit Compile(WorldKit kit, WorldChannelTable channels, WorldTargetRegisterTable targets, WorldCurveTable curves, IReadOnlyDictionary<string, CompiledBodyMotionProgram> programs, IReadOnlyDictionary<string, BodyMotionProgram> programRows, IReadOnlyList<WorldPrototype> creations, IReadOnlyList<ActionStateSlot> bodyState, IReadOnlyList<ActionStateSlot> identityState, IReadOnlyList<WorldDynamicsRow> dynamics, int simulationRateHz) {
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

        // An arm without a held-multiplier channel resolves -1 here the same way a kit with the field unset does —
        // "no sprint" by construction, not a special case (DeclaredSprintChannel is the one arm-dispatch read). A
        // drive row's drift channel is its own held read, resolved the same way below.
        var sprintOrdinal = (((kit.Motion.DeclaredSprintChannel is { Length: > 0 } sprintChannel)
            && channels.TryGetOrdinal(
            name: sprintChannel,
            ordinal: out var sprintResolved
        ))
            ? sprintResolved
            : -1
        );
        var driftOrdinal = (((kit.Motion.DeclaredDrive?.Drift?.Channel is { Length: > 0 } driftChannel)
            && channels.TryGetOrdinal(
            name: driftChannel,
            ordinal: out var driftResolved
        ))
            ? driftResolved
            : -1
        );
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
                    simulationRateHz: simulationRateHz
                )
            );
        }

        RequireProgramRoles(
            kitName: kit.Name,
            program: program,
            ordinals: roleOrdinals
        );

        // The sprint/boost and drift ordinals are HELD reads, not Actions bindings — each needs its threshold in
        // ActionThresholds regardless of whether kit.Actions also binds a press/release effect there (the loop above
        // only writes a threshold where an ActionSpec exists), so WorldBody's held-channel test compares against the
        // channel's OWN declared threshold rather than the array's zero default.
        if (sprintOrdinal >= 0) {
            thresholds[sprintOrdinal] = channels.Threshold(ordinal: sprintOrdinal);
        }

        if (driftOrdinal >= 0) {
            thresholds[driftOrdinal] = channels.Threshold(ordinal: driftOrdinal);
        }

        FixedMotionDynamics? planarDynamics = null;

        if (
            (kit.Motion.DeclaredDynamics is { Length: > 0 } dynamicsName) &&
            (WorldDefinitionRows.FindDynamics(
            dynamics: dynamics,
            name: dynamicsName
        ) is { } row)
        ) {
            var compiled = SecondOrderDynamics.Create(
                dampingRatio: FixedQ4816.FromDouble(value: row.Damping),
                frequencyHz: FixedQ4816.FromDouble(value: row.Frequency),
                initialResponse: FixedQ4816.FromDouble(value: row.Response)
            );

            planarDynamics = new FixedMotionDynamics(Planar: compiled.Compile(
                stepTicks: (FixedTickConversion.TicksPerSecond / ((ulong)simulationRateHz)),
                ticksPerSecond: FixedTickConversion.TicksPerSecond
            ));
        }

        return new FixedWorldKit(
            BodyMotionProgram: program,
            Producers: producers,
            Actions: actions,
            ActionThresholds: thresholds,
            ActionShapes: shapes,
            Collider: FixedWorldCollider.Compile(
                collider: kit.Collider,
                creations: creations
            ),
            BodyContact: kit.BodyContact,
            Mass: FixedQ4816.FromDouble(value: kit.Mass),
            SprintChannelOrdinal: sprintOrdinal,
            DriftChannelOrdinal: driftOrdinal,
            RoleOrdinals: roleOrdinals,
            RoleMask: roleMask,
            ActionState: actionState,
            Holds: WorldHoldFactory.Compile(
                channels: channels,
                holds: kit.Motion.DeclaredHolds
            ),
            PlanarDynamics: planarDynamics
        );
    }
}
