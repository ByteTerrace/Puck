using Puck.Maths;
using Puck.Physics.Motion;
using CompiledCellRef = Puck.State.Rules.CompiledCellRef;
using CompiledExpressionToken = Puck.State.Rules.CompiledExpressionToken;
using CompiledValueSource = Puck.State.Rules.CompiledValueSource;
using EffectSource = Puck.State.Rules.EffectSource;
using IRuleEffect = Puck.State.Rules.IRuleEffect;

namespace Puck.World;

/// <summary>What one world document write installs.</summary>
public enum WorldDocumentWrite : byte {
    /// <summary>Upserts a HUD panel row.</summary>
    UpsertHudPanel,

    /// <summary>Removes a HUD panel row.</summary>
    RemoveHudPanel,

    /// <summary>Upserts a placement row.</summary>
    UpsertPlacement,

    /// <summary>Removes a placement row.</summary>
    RemovePlacement,

    /// <summary>Saves the world through the host's save tap.</summary>
    Save,
}
/// <summary>One compiled body action, addressed by ordinal.</summary>
/// <param name="Operation">The motion operation.</param>
/// <param name="Value">The scalar the operation takes.</param>
/// <param name="Direction">The unit direction a planar impulse takes.</param>
/// <param name="DurationTicks">The engine-tick duration a timed operation takes.</param>
/// <param name="Register">The target register a designation names, or <see langword="null"/>.</param>
/// <param name="TargetKey">The designated body's literal key, or the invalid default.</param>
/// <param name="TargetKeyFrom">The designated body's live indirection, or <see langword="null"/>.</param>
/// <param name="Designation">What a designation writes.</param>
/// <param name="UseMoveDirection">Whether a planar impulse prefers the normalized current movement command.</param>
public readonly record struct WorldBodyAction(BodyMotionOp Operation, FixedQ4816 Value, FixedVector3 Direction, ulong DurationTicks, string? Register = null, CellKey TargetKey = default, CompiledCellRef? TargetKeyFrom = null, WorldBodyDesignationKind Designation = WorldBodyDesignationKind.Body, bool UseMoveDirection = false);
/// <summary>The effect facts only a world fires. The facet is the type argument, so firing receives
/// <see cref="IWorldFacts"/> as an argument.</summary>
/// <remarks>Every world effect but the identity-fact write lands outside the arena, so it declares
/// <see cref="EffectNeeds.Irreversible"/> and the compiler defers it to after the firing's scope commits.</remarks>
public abstract class WorldFactEffect : EffectFact<IWorldFacts>, IRuleEffect {
    private protected WorldFactEffect(string describe) : base(describe: describe) { }

    /// <inheritdoc/>
    public IReadOnlyList<IRuleEffect[]> Arms => [];
    /// <inheritdoc/>
    public override EffectNeeds Needs => EffectNeeds.Irreversible;
    /// <inheritdoc/>
    public virtual EffectSource[] Sources => [];

    /// <inheritdoc/>
    public override bool TryFire(IEffectHost host, IWorldFacts facet, in EffectFiring firing, out EffectRefusal refusal) {
        ArgumentNullException.ThrowIfNull(argument: host);

        return host.Fire(
            effect: this,
            firing: in firing,
            refusal: out refusal
        );
    }

    // The facet is the host, resolved by a type test: a host that serves no world facet reaches its own arm door,
    // whose default refuses the arm by name rather than throwing.
    bool IRuleEffect.TryFire(IEffectHost host, in EffectFiring firing, out EffectRefusal refusal) {
        ArgumentNullException.ThrowIfNull(argument: host);

        return ((host is IWorldFacts facts)
            ? TryFire(
                facet: facts,
                firing: in firing,
                host: host,
                refusal: out refusal
            )
            : host.Fire(
                effect: this,
                firing: in firing,
                refusal: out refusal
            )
        );
    }
}
/// <summary>Writes one world document row, or saves the world.</summary>
public sealed class WorldDocumentEffect : WorldFactEffect {
    private readonly RuleWork m_cost;

    /// <summary>Initializes the effect.</summary>
    /// <param name="write">What the effect installs.</param>
    /// <param name="id">The row id the write addresses.</param>
    /// <param name="panel">The HUD panel the write installs, or <see langword="null"/>.</param>
    /// <param name="placement">The placement the write installs, or <see langword="null"/>.</param>
    /// <param name="cost">The conservative work units one firing costs.</param>
    /// <param name="describe">The authored spelling, for the rules read-back.</param>
    public WorldDocumentEffect(WorldDocumentWrite write, string id, WorldHudPanel? panel, WorldPlacement? placement, RuleWork cost, string describe) : base(describe: describe) {
        HudPanel = panel;
        Id = id;
        Placement = placement;
        Write = write;
        m_cost = cost;
    }

    /// <summary>Gets the HUD panel the write installs, or <see langword="null"/>.</summary>
    public WorldHudPanel? HudPanel { get; }
    /// <summary>Gets the row id the write addresses.</summary>
    public string Id { get; }
    /// <summary>Gets the placement the write installs, or <see langword="null"/>.</summary>
    public WorldPlacement? Placement { get; }
    /// <summary>Gets what firing needs: a document row is committed with the firing, as one unit with the firing's
    /// other rows; a save is delivered after it.</summary>
    public override EffectNeeds Needs => ((Write == WorldDocumentWrite.Save)
        ? EffectNeeds.Irreversible
        : EffectNeeds.Irreversible | EffectNeeds.Transactional
    );
    /// <inheritdoc/>
    public override bool SubmitsMutation => (Write != WorldDocumentWrite.Save);
    /// <summary>Gets what the effect installs.</summary>
    public WorldDocumentWrite Write { get; }

    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => m_cost;
}
/// <summary>Emits a gameplay cue.</summary>
public sealed class WorldCueEffect : WorldFactEffect {
    /// <summary>Initializes the effect.</summary>
    /// <param name="cue">The cue name.</param>
    /// <param name="payload">The cue payload, or <see langword="null"/>.</param>
    /// <param name="body">The addressed body — its literal key, or the live indirection that computes it — or
    /// <see langword="null"/> for a cue about no body.</param>
    /// <param name="describe">The authored spelling, for the rules read-back.</param>
    public WorldCueEffect(string cue, string? payload, (CellKey Key, CompiledCellRef? KeyFrom)? body, string describe) : base(describe: describe) {
        Body = body;
        Cue = cue;
        Payload = payload;
        m_sources = ((body is { } address)
            ? [EffectSource.Key(keyFrom: address.KeyFrom, literal: address.Key)]
            : []);
    }

    private readonly EffectSource[] m_sources;

    /// <summary>The position of the addressed body's index among the sources, present only when the cue names a
    /// body.</summary>
    public const int BodySource = 0;

    /// <inheritdoc/>
    public override EffectSource[] Sources => m_sources;
    /// <summary>Gets the cue name.</summary>
    public string Cue { get; }
    /// <summary>Gets the addressed body, or <see langword="null"/> for a cue about no body. One value carries both
    /// spellings, so a live key is resolved exactly when a literal one is.</summary>
    public (CellKey Key, CompiledCellRef? KeyFrom)? Body { get; }
    /// <summary>Gets the cue payload, or <see langword="null"/>.</summary>
    public string? Payload { get; }
    /// <inheritdoc/>
    public override bool SubmitsMutation => false;

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) => CompiledCellRef.CollectReference(
        into: into,
        reference: Body?.KeyFrom
    );
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => 1L;
}
/// <summary>Teleports a body to a spawn point or a literal pose.</summary>
public sealed class WorldPoseEffect : WorldFactEffect {
    /// <summary>Initializes the effect.</summary>
    /// <param name="spawnPoint">The spawn point's id, or empty for a literal pose.</param>
    /// <param name="index">The addressed body's literal index, or <c>-1</c>.</param>
    /// <param name="keyFrom">The addressed body's live indirection, or <see langword="null"/>.</param>
    /// <param name="pose">The literal pose, or <see langword="null"/>.</param>
    /// <param name="describe">The authored spelling, for the rules read-back.</param>
    public WorldPoseEffect(string spawnPoint, int index, CompiledCellRef? keyFrom, CompiledWorldPose? pose, string describe) : base(describe: describe) {
        Index = index;
        KeyFrom = keyFrom;
        Pose = pose;
        SpawnPoint = spawnPoint;
        m_sources = ((keyFrom is null)
            ? []
            : [EffectSource.Key(keyFrom: keyFrom, literal: default)]);
    }

    private readonly EffectSource[] m_sources;

    /// <summary>The position of the addressed body's index among the sources, present only for a live key.</summary>
    public const int BodySource = 0;

    /// <inheritdoc/>
    public override EffectSource[] Sources => m_sources;
    /// <summary>Gets the addressed body's literal index, or <c>-1</c>.</summary>
    public int Index { get; }
    /// <summary>Gets the addressed body's live indirection, or <see langword="null"/>.</summary>
    public CompiledCellRef? KeyFrom { get; }
    /// <summary>Gets the literal pose, or <see langword="null"/>.</summary>
    public CompiledWorldPose? Pose { get; }
    /// <summary>Gets the spawn point's id, or empty for a literal pose.</summary>
    public string SpawnPoint { get; }
    /// <inheritdoc/>
    public override bool SubmitsMutation => false;

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) => CompiledCellRef.CollectReference(
        into: into,
        reference: KeyFrom
    );
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => 1L;
}
/// <summary>Applies a body motion operation to a world-addressed body.</summary>
public sealed class WorldBodyMotionEffect : WorldFactEffect {
    /// <summary>Initializes the effect.</summary>
    /// <param name="key">The addressed body's literal key, or the invalid default.</param>
    /// <param name="keyFrom">The addressed body's live indirection, or <see langword="null"/>.</param>
    /// <param name="action">The compiled action.</param>
    /// <param name="describe">The authored spelling, for the rules read-back.</param>
    public WorldBodyMotionEffect(CellKey key, CompiledCellRef? keyFrom, WorldBodyAction action, string describe) : base(describe: describe) {
        Action = action;
        Key = key;
        KeyFrom = keyFrom;
        m_sources = (DesignatesBody(action: action)
            ? [EffectSource.Key(keyFrom: keyFrom, literal: key), EffectSource.Key(keyFrom: action.TargetKeyFrom, literal: action.TargetKey)]
            : [EffectSource.Key(keyFrom: keyFrom, literal: key)]);
    }

    private readonly EffectSource[] m_sources;

    /// <summary>The position of the addressed body's index among the sources.</summary>
    public const int BodySource = 0;
    /// <summary>The position of the designated body's index among the sources, present only for a designation of a
    /// body.</summary>
    public const int TargetSource = 1;

    /// <inheritdoc/>
    public override EffectSource[] Sources => m_sources;

    private static bool DesignatesBody(WorldBodyAction action) => ((action.Operation == BodyMotionOp.Designate) && (action.Designation != WorldBodyDesignationKind.Clear));

    /// <summary>Gets the compiled action.</summary>
    public WorldBodyAction Action { get; }
    /// <summary>Gets the addressed body's literal key, or the invalid default.</summary>
    public CellKey Key { get; }
    /// <summary>Gets the addressed body's live indirection, or <see langword="null"/>.</summary>
    public CompiledCellRef? KeyFrom { get; }
    /// <inheritdoc/>
    public override bool SubmitsMutation => false;

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        CompiledCellRef.CollectReference(
            into: into,
            reference: KeyFrom
        );
        CompiledCellRef.CollectReference(
            into: into,
            reference: Action.TargetKeyFrom
        );
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => 1L;
}
/// <summary>Applies a rigid-body impulse to a live-resolved struck body, along a second live-resolved heading body's
/// own forward facing, scaled by a live state cell.</summary>
public sealed class WorldRigidImpulseEffect : WorldFactEffect {
    /// <summary>Initializes the effect.</summary>
    /// <param name="target">The struck body.</param>
    /// <param name="heading">The heading body.</param>
    /// <param name="magnitude">The live magnitude operand.</param>
    /// <param name="describe">The authored spelling, for the rules read-back.</param>
    public WorldRigidImpulseEffect(WorldBodyRef target, WorldBodyRef heading, Puck.State.Rules.IRuleOperand magnitude, string describe) : base(describe: describe) {
        ArgumentNullException.ThrowIfNull(argument: magnitude);

        Heading = heading;
        Magnitude = magnitude;
        Target = target;
        m_sources = [EffectSource.FromOperand(kind: CellKind.Fixed, operand: magnitude)];
    }

    private readonly EffectSource[] m_sources;

    /// <summary>The position of the impulse magnitude among the sources.</summary>
    public const int MagnitudeSource = 0;

    /// <inheritdoc/>
    public override EffectSource[] Sources => m_sources;
    /// <summary>Gets the heading body.</summary>
    public WorldBodyRef Heading { get; }
    /// <summary>Gets the live magnitude operand.</summary>
    public Puck.State.Rules.IRuleOperand Magnitude { get; }
    /// <inheritdoc/>
    public override bool SubmitsMutation => false;
    /// <summary>Gets the struck body.</summary>
    public WorldBodyRef Target { get; }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) => Magnitude.CollectReads(into: into);
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => (1L + Magnitude.Cost(context: context));
}
/// <summary>Paints a lattice field cell or the cube around it.</summary>
public sealed class WorldPaintFieldEffect : WorldFactEffect {
    /// <summary>Initializes the effect.</summary>
    /// <param name="paint">The compiled paint.</param>
    /// <param name="describe">The authored spelling, for the rules read-back.</param>
    public WorldPaintFieldEffect(CompiledWorldFieldPaint paint, string describe) : base(describe: describe) => Paint = paint;

    /// <summary>Gets the compiled paint.</summary>
    public CompiledWorldFieldPaint Paint { get; }
    /// <inheritdoc/>
    public override bool SubmitsMutation => false;

    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) {
        var diameter = ((2L * Paint.Radius) + 1L);

        return (1L + ((diameter * diameter) * diameter));
    }
}
/// <summary>Writes one <c>state.body</c> counter of a world-addressed body, in the arena's participant lane.</summary>
/// <remarks>The lane cell is an arena row, so this arm is rewound by the firing's own scope and declares nothing. A
/// <c>bodyState</c> read is a fact rather than a row read, so the write declares no row either.</remarks>
public sealed class WorldBodyStateEffect : WorldFactEffect {
    /// <summary>Initializes a new instance of the <see cref="WorldBodyStateEffect"/> class.</summary>
    /// <param name="key">The addressed body's literal key, or the invalid default.</param>
    /// <param name="keyFrom">The addressed body's live indirection, or <see langword="null"/>.</param>
    /// <param name="slot">The register's slot in the world's action-state register file.</param>
    /// <param name="source">The compiled value source, in raw <see cref="FixedQ4816"/> bits.</param>
    /// <param name="describe">The authored spelling, for the rules read-back.</param>
    public WorldBodyStateEffect(CellKey key, CompiledCellRef? keyFrom, int slot, in CompiledValueSource source, string describe) : base(describe: describe) {
        Key = key;
        KeyFrom = keyFrom;
        Slot = slot;
        Source = source;
        m_sources = ((source.Expression is { } program)
            ? [EffectSource.Key(keyFrom: keyFrom, literal: key), EffectSource.Expression(kind: CellKind.Fixed, program: program)]
            : [EffectSource.Key(keyFrom: keyFrom, literal: key)]);
    }

    private readonly EffectSource[] m_sources;

    /// <summary>The position of the addressed body's index among the sources.</summary>
    public const int BodySource = 0;
    /// <summary>The position of the value expression among the sources, present only for a computed value.</summary>
    public const int ValueSource = 1;

    /// <inheritdoc/>
    public override EffectSource[] Sources => m_sources;
    /// <summary>Gets the addressed body's literal key, or the invalid default.</summary>
    public CellKey Key { get; }
    /// <summary>Gets the addressed body's live indirection, or <see langword="null"/>.</summary>
    public CompiledCellRef? KeyFrom { get; }
    /// <summary>Gets what firing needs: the lane cell it writes is an arena row the firing's scope rewinds.</summary>
    public override EffectNeeds Needs => EffectNeeds.None;
    /// <summary>Gets the register's slot in the world's action-state register file.</summary>
    public int Slot { get; }
    /// <summary>Gets the compiled value source.</summary>
    public CompiledValueSource Source { get; }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        Source.CollectReads(into: into);
        CompiledCellRef.CollectReference(
            into: into,
            reference: KeyFrom
        );
    }
    /// <inheritdoc/>
    /// <remarks>Against a world document, a register the firing's commit derives from adds what that commit spends: an
    /// input-selection register (an input mode family's body-state selector, or the kit assignment's selector)
    /// revalidates and resynchronizes the written body's routing owner, one pass over the input families and the kit
    /// selection each, and a register the population derives a body's scale, kit or look from resyncs every body.</remarks>
    public override RuleWork Cost(IRuleCostContext context) => ((1L + Source.Cost(context: context)) + CommitCost(context: context));

    private long CommitCost(IRuleCostContext context) {
        if (context is not WorldFactsCompileContext { Definition: var definition }) { return 0L; }
        var bodyState = definition.BodyState;
        var identityState = definition.IdentityState;

        if ((Slot < 0) || (Slot >= (bodyState.Count + identityState.Count))) { return 0L; }
        var name = ((Slot < bodyState.Count) ? bodyState[Slot].Name : identityState[(Slot - bodyState.Count)].Name);
        var kit = (definition.Assignment.Select?.BodyState == name);
        var catalog = WorldInputCatalog.For(definition: definition);
        var selects = kit;

        foreach (var selector in catalog.Selectors) {
            selects |= ((selector.Kind == WorldInputModeSelectorKind.BodyState) && (selector.Name == name));
        }
        var cost = (selects ? (2L * (catalog.Selectors.Length + 1L)) : 0L);

        if (kit || (definition.Population.Scale == name) || (definition.LookAssignment.Select?.BodyState == name)) {
            cost += definition.Population.Capacity;
        }
        return cost;
    }
}
/// <summary>Writes one fact on the identity a world-addressed body drives under — the body's lane cell and the
/// identity's persisted row together.</summary>
/// <remarks>The lane cell is an arena row, so this arm is rewound by the firing's own scope and declares
/// nothing.</remarks>
public sealed class WorldIdentityFactEffect : WorldFactEffect {
    /// <summary>Initializes the effect.</summary>
    /// <param name="key">The addressed body's literal key, or the invalid default.</param>
    /// <param name="keyFrom">The addressed body's live indirection, or <see langword="null"/>.</param>
    /// <param name="fact">The fact key on the identity's row.</param>
    /// <param name="laneOrdinal">The identity lane row's catalog ordinal.</param>
    /// <param name="source">The compiled value source.</param>
    /// <param name="describe">The authored spelling, for the rules read-back.</param>
    public WorldIdentityFactEffect(CellKey key, CompiledCellRef? keyFrom, CellName fact, int laneOrdinal, in CompiledValueSource source, string describe) : base(describe: describe) {
        Fact = fact;
        Key = key;
        KeyFrom = keyFrom;
        LaneOrdinal = laneOrdinal;
        Source = source;
        m_sources = ((source.Expression is { } program)
            ? [EffectSource.Key(keyFrom: keyFrom, literal: key), EffectSource.Expression(kind: CellKind.Int, program: program)]
            : [EffectSource.Key(keyFrom: keyFrom, literal: key)]);
    }

    private readonly EffectSource[] m_sources;

    /// <summary>The position of the addressed body's index among the sources.</summary>
    public const int BodySource = 0;
    /// <summary>The position of the value expression among the sources, present only for a computed value.</summary>
    public const int ValueSource = 1;

    /// <inheritdoc/>
    public override EffectSource[] Sources => m_sources;
    /// <summary>Gets the compiled numeric expression, or <see langword="null"/>.</summary>
    public CompiledExpressionToken[]? Expression => Source.Expression;
    /// <summary>Gets the fact key on the identity's row.</summary>
    public CellName Fact { get; }
    /// <summary>Gets the addressed body's literal key, or the invalid default.</summary>
    public CellKey Key { get; }
    /// <summary>Gets the addressed body's live indirection, or <see langword="null"/>.</summary>
    public CompiledCellRef? KeyFrom { get; }
    /// <summary>Gets the identity lane row's catalog ordinal.</summary>
    public int LaneOrdinal { get; }
    /// <summary>Gets what firing needs: the lane cell it writes is an arena row the firing's scope rewinds.</summary>
    public override EffectNeeds Needs => EffectNeeds.None;
    /// <summary>Gets the compiled value source.</summary>
    public CompiledValueSource Source { get; }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        Source.CollectReads(into: into);
        CompiledCellRef.CollectReference(
            into: into,
            reference: KeyFrom
        );
    }
    /// <inheritdoc/>
    public override void CollectWrites(List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        into.Add(item: new CellAccess(
            IsSet: true,
            Key: default,
            RowOrdinal: LaneOrdinal
        ));
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => (512L + Source.Cost(context: context));
}
