using Puck.World.Protocol;

namespace Puck.World;

// One sealed class per WorldRuleEffectKind — the case types of CompiledWorldEffect's union (WorldEffectUnion.cs).

/// <summary>A state cell write (<see cref="WorldRuleEffectKind.Write"/>) — <c>setState</c>/<c>addState</c>, a
/// literal, a live copy, an expression, or (for a kind=text row) a text literal.</summary>
public sealed class WriteEffect : WorldEffectFact, IStateWriteEffect, IValueSourcedEffect {
    /// <param name="row">The destination state row name.</param>
    /// <param name="key">The destination cell key.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/> for a literal <paramref name="key"/>.</param>
    /// <param name="write">Set or add.</param>
    /// <param name="rawValue">The authored literal, pre-converted to the destination row's raw encoding — read only
    /// when neither <paramref name="from"/> nor <paramref name="expression"/> applies.</param>
    /// <param name="from">The live copy-source operand, or <see langword="null"/> for a literal/expression/text write.</param>
    /// <param name="text">The text literal for a kind=text row, or <see langword="null"/> for a numeric write.</param>
    /// <param name="expression">The compiled numeric expression, or <see langword="null"/> for another source spelling.</param>
    /// <param name="describe">The authored spelling, for the <c>world.rules</c> read-back.</param>
    public WriteEffect(string row, string key, CompiledCellRef? keyFrom, WorldDocumentWriteKind write, long rawValue, CompiledWorldOperand? from, string? text, CompiledWorldExpressionToken[]? expression, string describe)
        : base(WorldRuleEffectKind.Write, describe) {
        Row = row;
        Key = key;
        KeyFrom = keyFrom;
        Write = write;
        RawValue = rawValue;
        From = from;
        Text = text;
        Expression = expression;
    }

    /// <inheritdoc/>
    public string Row { get; }
    /// <inheritdoc/>
    public string Key { get; }
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom { get; }
    /// <inheritdoc/>
    public WorldDocumentWriteKind Write { get; }
    /// <inheritdoc/>
    public long RawValue { get; }
    /// <inheritdoc/>
    public CompiledWorldOperand? From { get; }
    /// <summary>The text literal for a kind=text row, or <see langword="null"/> for a numeric write.</summary>
    public string? Text { get; }
    /// <inheritdoc/>
    public CompiledWorldExpressionToken[]? Expression { get; }
}

/// <summary>Consumes a non-negative integer countdown by the simulation step's engine-tick width
/// (<see cref="WorldRuleEffectKind.Countdown"/>).</summary>
public sealed class CountdownEffect : WorldEffectFact, IStateWriteEffect {
    /// <param name="row">The destination state row name.</param>
    /// <param name="key">The destination cell key.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/> for a literal <paramref name="key"/>.</param>
    /// <param name="describe">The authored spelling, for the <c>world.rules</c> read-back.</param>
    public CountdownEffect(string row, string key, CompiledCellRef? keyFrom, string describe) : base(WorldRuleEffectKind.Countdown, describe) {
        Row = row;
        Key = key;
        KeyFrom = keyFrom;
    }

    /// <inheritdoc/>
    public string Row { get; }
    /// <inheritdoc/>
    public string Key { get; }
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom { get; }
    /// <summary>Always <see cref="WorldDocumentWriteKind.Add"/> — a countdown subtracts from the current value.</summary>
    public WorldDocumentWriteKind Write => WorldDocumentWriteKind.Add;
}

/// <summary>Fires a generator row into its draw site (<see cref="WorldRuleEffectKind.Generate"/>).</summary>
public sealed class GenerateEffect : WorldEffectFact, IStateAddressedEffect {
    /// <param name="row">The draw site's state row.</param>
    /// <param name="generator">The generator row name.</param>
    /// <param name="describe">The authored spelling, for the <c>world.rules</c> read-back.</param>
    public GenerateEffect(string row, string generator, string describe) : base(WorldRuleEffectKind.Generate, describe) {
        Row = row;
        Generator = generator;
    }

    /// <inheritdoc/>
    public string Row { get; }
    /// <summary>The generator row name.</summary>
    public string Generator { get; }
    /// <summary>Always the row's own slot cell — a draw site is a scalar slot by construction.</summary>
    public string Key => WorldStateRow.SlotKey;
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom => null;
}

/// <summary>Upserts a HUD panel row (<see cref="WorldRuleEffectKind.UpsertHudPanel"/>).</summary>
public sealed class UpsertHudPanelEffect : WorldEffectFact, IStateAddressedEffect {
    /// <param name="panel">The whole panel row.</param>
    /// <param name="describe">The authored spelling, for the <c>world.rules</c> read-back.</param>
    public UpsertHudPanelEffect(WorldHudPanel panel, string describe) : base(WorldRuleEffectKind.UpsertHudPanel, describe) => HudPanel = panel;

    /// <summary>The whole panel row.</summary>
    public WorldHudPanel HudPanel { get; }
    /// <inheritdoc/>
    public string Row => HudPanel.Id;
    /// <inheritdoc/>
    public string Key => string.Empty;
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom => null;
}

/// <summary>Removes a HUD panel row (<see cref="WorldRuleEffectKind.RemoveHudPanel"/>).</summary>
public sealed class RemoveHudPanelEffect : WorldEffectFact, IStateAddressedEffect {
    /// <param name="id">The panel id.</param>
    /// <param name="describe">The authored spelling, for the <c>world.rules</c> read-back.</param>
    public RemoveHudPanelEffect(string id, string describe) : base(WorldRuleEffectKind.RemoveHudPanel, describe) => Row = id;

    /// <inheritdoc/>
    public string Row { get; }
    /// <inheritdoc/>
    public string Key => string.Empty;
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom => null;
}

/// <summary>Upserts a placement row (<see cref="WorldRuleEffectKind.UpsertPlacement"/>).</summary>
public sealed class UpsertPlacementEffect : WorldEffectFact, IStateAddressedEffect {
    /// <param name="placement">The whole placement row.</param>
    /// <param name="describe">The authored spelling, for the <c>world.rules</c> read-back.</param>
    public UpsertPlacementEffect(WorldPlacement placement, string describe) : base(WorldRuleEffectKind.UpsertPlacement, describe) => Placement = placement;

    /// <summary>The whole placement row.</summary>
    public WorldPlacement Placement { get; }
    /// <inheritdoc/>
    public string Row => Placement.Id;
    /// <inheritdoc/>
    public string Key => string.Empty;
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom => null;
}

/// <summary>Removes a placement row (<see cref="WorldRuleEffectKind.RemovePlacement"/>).</summary>
public sealed class RemovePlacementEffect : WorldEffectFact, IStateAddressedEffect {
    /// <param name="id">The placement id.</param>
    /// <param name="describe">The authored spelling, for the <c>world.rules</c> read-back.</param>
    public RemovePlacementEffect(string id, string describe) : base(WorldRuleEffectKind.RemovePlacement, describe) => Row = id;

    /// <inheritdoc/>
    public string Row { get; }
    /// <inheritdoc/>
    public string Key => string.Empty;
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom => null;
}

/// <summary>Writes a session snapshot of the world to its own file (<see cref="WorldRuleEffectKind.Save"/>).
/// Stateless: every read shares <see cref="Instance"/>.</summary>
public sealed class SaveEffect : WorldEffectFact {
    /// <summary>The shared instance.</summary>
    public static readonly SaveEffect Instance = new();
    private SaveEffect() : base(WorldRuleEffectKind.Save, "save") { }
}

/// <summary>Teleports a body to a pose (<see cref="WorldRuleEffectKind.Pose"/>).</summary>
public sealed class PoseEffect : WorldEffectFact {
    /// <param name="spawnPoint">The spawn-point id, or empty when <paramref name="pose"/> carries a literal position.</param>
    /// <param name="key">The literal body-index text, or the authored body-key indirection source.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/> for a literal <paramref name="key"/>.</param>
    /// <param name="pose">The literal pose, or <see langword="null"/> when <paramref name="spawnPoint"/> names a
    /// declared spawn point instead.</param>
    /// <param name="describe">The authored spelling, for the <c>world.rules</c> read-back.</param>
    public PoseEffect(string spawnPoint, string key, CompiledCellRef? keyFrom, CompiledWorldPose? pose, string describe) : base(WorldRuleEffectKind.Pose, describe) {
        SpawnPoint = spawnPoint;
        Key = key;
        KeyFrom = keyFrom;
        Pose = pose;
    }

    /// <summary>The spawn-point id, or empty when <see cref="Pose"/> carries a literal position.</summary>
    public string SpawnPoint { get; }
    /// <summary>The literal body-index text, or the authored body-key indirection source.</summary>
    public string Key { get; }
    /// <summary>The live key indirection, or <see langword="null"/> for a literal <see cref="Key"/>.</summary>
    public CompiledCellRef? KeyFrom { get; }
    /// <summary>The literal pose, or <see langword="null"/> when <see cref="SpawnPoint"/> names a declared spawn
    /// point instead.</summary>
    public CompiledWorldPose? Pose { get; }
}

/// <summary>Removes an addressed state cell (<see cref="WorldRuleEffectKind.RemoveStateCell"/>).</summary>
public sealed class RemoveStateCellEffect : WorldEffectFact, IStateAddressedEffect {
    /// <param name="row">The destination state row name.</param>
    /// <param name="key">The destination cell key.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/> for a literal <paramref name="key"/>.</param>
    /// <param name="describe">The authored spelling, for the <c>world.rules</c> read-back.</param>
    public RemoveStateCellEffect(string row, string key, CompiledCellRef? keyFrom, string describe) : base(WorldRuleEffectKind.RemoveStateCell, describe) {
        Row = row;
        Key = key;
        KeyFrom = keyFrom;
    }

    /// <inheritdoc/>
    public string Row { get; }
    /// <inheritdoc/>
    public string Key { get; }
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom { get; }
}

/// <summary>Writes an absolute simulation due tick into an integer state cell
/// (<see cref="WorldRuleEffectKind.ScheduleState"/>).</summary>
public sealed class ScheduleStateEffect : WorldEffectFact, IStateWriteEffect {
    /// <param name="row">The destination state row name.</param>
    /// <param name="key">The destination cell key.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/> for a literal <paramref name="key"/>.</param>
    /// <param name="delayTicks">The authored delay, in simulation ticks, added to the firing tick at runtime.</param>
    /// <param name="describe">The authored spelling, for the <c>world.rules</c> read-back.</param>
    public ScheduleStateEffect(string row, string key, CompiledCellRef? keyFrom, long delayTicks, string describe) : base(WorldRuleEffectKind.ScheduleState, describe) {
        Row = row;
        Key = key;
        KeyFrom = keyFrom;
        DelayTicks = delayTicks;
    }

    /// <inheritdoc/>
    public string Row { get; }
    /// <inheritdoc/>
    public string Key { get; }
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom { get; }
    /// <summary>The authored delay, in simulation ticks, added to the firing tick at runtime.</summary>
    public long DelayTicks { get; }
    /// <summary>Always <see cref="WorldDocumentWriteKind.Set"/> — a schedule overwrites the due tick.</summary>
    public WorldDocumentWriteKind Write => WorldDocumentWriteKind.Set;
}

/// <summary>Applies a preflighted state-cell mutation bundle with an optional failure branch
/// (<see cref="WorldRuleEffectKind.Transaction"/>).</summary>
public sealed class TransactionEffect : WorldEffectFact {
    /// <param name="effects">The atomic transaction's main branch.</param>
    /// <param name="onFailure">The transaction's refusal branch.</param>
    /// <param name="describe">The authored spelling, for the <c>world.rules</c> read-back.</param>
    public TransactionEffect(CompiledWorldEffect[] effects, CompiledWorldEffect[] onFailure, string describe) : base(WorldRuleEffectKind.Transaction, describe) {
        Effects = effects;
        OnFailure = onFailure;
    }

    /// <summary>The atomic transaction's main branch.</summary>
    public CompiledWorldEffect[] Effects { get; }
    /// <summary>The transaction's refusal branch.</summary>
    public CompiledWorldEffect[] OnFailure { get; }
}

/// <summary>Emits a presentation-neutral gameplay cue (<see cref="WorldRuleEffectKind.EmitCue"/>).</summary>
public sealed class EmitCueEffect : WorldEffectFact {
    /// <param name="cue">The gameplay cue identifier.</param>
    /// <param name="payload">The optional cue payload.</param>
    /// <param name="key">The literal body-index text, or the authored body-key indirection source; empty when the
    /// cue carries no body.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/> for a literal <paramref name="key"/>.</param>
    /// <param name="describe">The authored spelling, for the <c>world.rules</c> read-back.</param>
    public EmitCueEffect(string cue, string? payload, string key, CompiledCellRef? keyFrom, string describe) : base(WorldRuleEffectKind.EmitCue, describe) {
        Cue = cue;
        Payload = payload;
        Key = key;
        KeyFrom = keyFrom;
    }

    /// <summary>The gameplay cue identifier.</summary>
    public string Cue { get; }
    /// <summary>The optional cue payload.</summary>
    public string? Payload { get; }
    /// <summary>The literal body-index text, or the authored body-key indirection source; empty when the cue
    /// carries no body.</summary>
    public string Key { get; }
    /// <summary>The live key indirection, or <see langword="null"/> for a literal <see cref="Key"/>.</summary>
    public CompiledCellRef? KeyFrom { get; }
}

/// <summary>Applies a deterministic operation to an active body (<see cref="WorldRuleEffectKind.Body"/>).</summary>
public sealed class BodyEffect : WorldEffectFact {
    /// <param name="key">The literal body-index text, or the authored body-key indirection source.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/> for a literal <paramref name="key"/>.</param>
    /// <param name="body">The compiled body operation.</param>
    /// <param name="describe">The authored spelling, for the <c>world.rules</c> read-back.</param>
    public BodyEffect(string key, CompiledCellRef? keyFrom, CompiledWorldBodyEffect body, string describe) : base(WorldRuleEffectKind.Body, describe) {
        Key = key;
        KeyFrom = keyFrom;
        Body = body;
    }

    /// <summary>The literal body-index text, or the authored body-key indirection source.</summary>
    public string Key { get; }
    /// <summary>The live key indirection, or <see langword="null"/> for a literal <see cref="Key"/>.</summary>
    public CompiledCellRef? KeyFrom { get; }
    /// <summary>The compiled body operation.</summary>
    public CompiledWorldBodyEffect Body { get; }
}

/// <summary>Paints a bounded neighborhood in the live field lattice (<see cref="WorldRuleEffectKind.PaintField"/>).</summary>
public sealed class PaintFieldEffect : WorldEffectFact {
    /// <param name="paint">The compiled lattice paint.</param>
    /// <param name="describe">The authored spelling, for the <c>world.rules</c> read-back.</param>
    public PaintFieldEffect(CompiledWorldFieldPaint paint, string describe) : base(WorldRuleEffectKind.PaintField, describe) => Paint = paint;

    /// <summary>The compiled lattice paint.</summary>
    public CompiledWorldFieldPaint Paint { get; }
}

/// <summary>An atomic discrete state transform (<see cref="WorldRuleEffectKind.TransformState"/>).</summary>
public sealed class TransformStateEffect : WorldEffectFact {
    /// <param name="transform">The discrete state transform.</param>
    /// <param name="describe">The authored spelling, for the <c>world.rules</c> read-back.</param>
    public TransformStateEffect(WorldStateTransform transform, string describe) : base(WorldRuleEffectKind.TransformState, describe) => Transform = transform;

    /// <summary>The discrete state transform.</summary>
    public WorldStateTransform Transform { get; }
}

/// <summary>Pushes one evaluated value into a history row's ring (<see cref="WorldRuleEffectKind.PushState"/>).</summary>
public sealed class PushStateEffect : WorldEffectFact, IValueSourcedEffect {
    /// <param name="row">The history row.</param>
    /// <param name="rawValue">The authored literal, pre-converted to the row's raw encoding — read only when
    /// neither <paramref name="from"/> nor <paramref name="expression"/> applies.</param>
    /// <param name="from">The live copy-source operand, or <see langword="null"/> for a literal/expression push.</param>
    /// <param name="expression">The compiled numeric expression, or <see langword="null"/> for another source spelling.</param>
    /// <param name="describe">The authored spelling, for the <c>world.rules</c> read-back.</param>
    public PushStateEffect(string row, long rawValue, CompiledWorldOperand? from, CompiledWorldExpressionToken[]? expression, string describe)
        : base(WorldRuleEffectKind.PushState, describe) {
        Row = row;
        RawValue = rawValue;
        From = from;
        Expression = expression;
    }

    /// <summary>The history row.</summary>
    public string Row { get; }
    /// <inheritdoc/>
    public long RawValue { get; }
    /// <inheritdoc/>
    public CompiledWorldOperand? From { get; }
    /// <inheritdoc/>
    public CompiledWorldExpressionToken[]? Expression { get; }

    /// <summary>Reshapes an already-resolved <see cref="WriteEffect"/> into the ring push over it — the union-safe
    /// replacement for the record struct's <c>write with { Kind = PushState, ... }</c> clone: the value spelling
    /// (literal/copy/expression) carries over unchanged, and the row-addressing fields (Key, KeyFrom, Write, Text)
    /// fall away because a push always targets the ring's own next slot.</summary>
    /// <param name="write">The resolved write over the ring's slot cell.</param>
    /// <param name="describe">The push's own read-back spelling.</param>
    public static PushStateEffect FromWrite(WriteEffect write, string describe) =>
        new(write.Row, write.RawValue, write.From, write.Expression, describe);
}
