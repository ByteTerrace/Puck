namespace Puck.World;

/// <summary>Upserts a HUD panel document row. A document write, not a state write: it submits a mutation but touches
/// no state cell, so it contributes nothing to the dataflow sets.</summary>
public sealed class UpsertHudPanelEffect : EffectFact, IStateAddressedEffect {
    public UpsertHudPanelEffect(WorldHudPanel panel, string describe) : base(describe: describe) => HudPanel = panel;

    public WorldHudPanel HudPanel { get; }
    public string Row => HudPanel.Id;
    public string Key => string.Empty;
    public CompiledCellRef? KeyFrom => null;

    public override long Cost(RuleCompileContext context) => 4_096L;
}

/// <summary>Removes a HUD panel document row.</summary>
public sealed class RemoveHudPanelEffect : EffectFact, IStateAddressedEffect {
    public RemoveHudPanelEffect(string id, string describe) : base(describe: describe) => Row = id;

    public string Row { get; }
    public string Key => string.Empty;
    public CompiledCellRef? KeyFrom => null;

    public override long Cost(RuleCompileContext context) => 4_096L;
}

/// <summary>Upserts a placement document row; rebuilds the active population, so it closes a transaction.</summary>
public sealed class UpsertPlacementEffect : EffectFact, IStateAddressedEffect {
    public UpsertPlacementEffect(WorldPlacement placement, string describe) : base(describe: describe) => Placement = placement;

    public WorldPlacement Placement { get; }
    public string Row => Placement.Id;
    public string Key => string.Empty;
    public CompiledCellRef? KeyFrom => null;

    public override bool ClosesTransaction => true;
    public override long Cost(RuleCompileContext context) => 32_768L;
}

/// <summary>Removes a placement document row; rebuilds the active population, so it closes a transaction.</summary>
public sealed class RemovePlacementEffect : EffectFact, IStateAddressedEffect {
    public RemovePlacementEffect(string id, string describe) : base(describe: describe) => Row = id;

    public string Row { get; }
    public string Key => string.Empty;
    public CompiledCellRef? KeyFrom => null;

    public override bool ClosesTransaction => true;
    public override long Cost(RuleCompileContext context) => 32_768L;
}

/// <summary>Saves the world through the host's save tap.</summary>
public sealed class SaveEffect : EffectFact {
    public static readonly SaveEffect Instance = new();
    private SaveEffect() : base(describe: "save") { }

    public override bool SubmitsMutation => false;
    public override long Cost(RuleCompileContext context) => 1L;
}

/// <summary>Teleports a body to a spawn point or a literal pose.</summary>
public sealed class PoseEffect : EffectFact {
    public PoseEffect(string spawnPoint, string key, CompiledCellRef? keyFrom, CompiledWorldPose? pose, string describe) : base(describe: describe) {
        SpawnPoint = spawnPoint;
        Key = key;
        KeyFrom = keyFrom;
        Pose = pose;
    }

    public string SpawnPoint { get; }
    public string Key { get; }
    public CompiledCellRef? KeyFrom { get; }
    public CompiledWorldPose? Pose { get; }

    public override bool SubmitsMutation => false;
    public override long Cost(RuleCompileContext context) => 1L;
    public override void CollectReads(List<RuleAccess> into) => RuleAccess.CollectReference(reference: KeyFrom, into: into);
}

/// <summary>Emits a gameplay cue.</summary>
public sealed class EmitCueEffect : EffectFact {
    public EmitCueEffect(string cue, string? payload, string key, CompiledCellRef? keyFrom, string describe) : base(describe: describe) {
        Cue = cue;
        Payload = payload;
        Key = key;
        KeyFrom = keyFrom;
    }

    public string Cue { get; }
    public string? Payload { get; }
    public string Key { get; }
    public CompiledCellRef? KeyFrom { get; }

    public override bool SubmitsMutation => false;
    public override long Cost(RuleCompileContext context) => 1L;
    public override void CollectReads(List<RuleAccess> into) => RuleAccess.CollectReference(reference: KeyFrom, into: into);
}

/// <summary>Writes one fact on the identity a world-addressed body drives under — the body's lane cell and the
/// identity's persisted row together (<see cref="WorldEffect.SetIdentityFact"/>).</summary>
public sealed class IdentityFactEffect : EffectFact, IValueSourcedEffect {
    private CellName[]? m_laneKeys;

    public IdentityFactEffect(string key, CompiledCellRef? keyFrom, CellName fact, long rawValue, CompiledExpressionToken[]? expression, string describe) : base(describe: describe) {
        Key = key;
        KeyFrom = keyFrom;
        Fact = fact;
        RawValue = rawValue;
        Expression = expression;
    }

    /// <summary>Gets the body address — a literal index, or the spelling <see cref="KeyFrom"/> resolves live.</summary>
    public string Key { get; }
    /// <summary>Gets the live body indirection, or <see langword="null"/> for a literal <see cref="Key"/>.</summary>
    public CompiledCellRef? KeyFrom { get; }
    /// <summary>Gets the fact key on the identity's row.</summary>
    public CellName Fact { get; }
    /// <inheritdoc/>
    public long RawValue { get; }
    /// <inheritdoc/>
    public OperandFact? From => null;
    /// <inheritdoc/>
    public CompiledExpressionToken[]? Expression { get; }

    /// <summary>Returns the lane key of <paramref name="bodyIndex"/>'s cell for this fact, minted once per body.</summary>
    /// <param name="bodyIndex">The 0-based body index.</param>
    /// <param name="capacity">The population capacity the cache is sized to.</param>
    public CellName LaneKey(int bodyIndex, int capacity) {
        m_laneKeys ??= new CellName[capacity];

        if (((uint)bodyIndex) >= ((uint)m_laneKeys.Length)) {
            return CellName.Parse(candidate: WorldIdentityFactLane.Key(bodyIndex: bodyIndex, fact: Fact.Value));
        }
        if (m_laneKeys[bodyIndex].Value is null) {
            m_laneKeys[bodyIndex] = CellName.Parse(candidate: WorldIdentityFactLane.Key(bodyIndex: bodyIndex, fact: Fact.Value));
        }

        return m_laneKeys[bodyIndex];
    }

    public override long Cost(RuleCompileContext context) => EffectCosts.Sourced(baseCost: 512L, effect: this, context: context);
    public override void CollectReads(List<RuleAccess> into) {
        EffectCosts.CollectSourceReads(effect: this, into: into);
        RuleAccess.CollectReference(reference: KeyFrom, into: into);
    }
    public override void CollectWrites(List<RuleAccess> into) => into.Add(item: new RuleAccess(Row: WorldIdentityFactLane.RowName, Key: null, IsSet: true));
    public override bool ReadsHost => true;
}

/// <summary>Applies a body motion operation to a world-addressed body.</summary>
public sealed class BodyEffect : EffectFact {
    public BodyEffect(string key, CompiledCellRef? keyFrom, CompiledWorldBodyEffect body, string describe) : base(describe: describe) {
        Key = key;
        KeyFrom = keyFrom;
        Body = body;
    }

    public string Key { get; }
    public CompiledCellRef? KeyFrom { get; }
    public CompiledWorldBodyEffect Body { get; }

    public override bool SubmitsMutation => false;
    public override long Cost(RuleCompileContext context) => 1L;
    public override void CollectReads(List<RuleAccess> into) {
        RuleAccess.CollectReference(reference: KeyFrom, into: into);
        RuleAccess.CollectReference(reference: Body.TargetKeyFrom, into: into);
    }
}

/// <summary>Paints a lattice field cell or the cube around it.</summary>
public sealed class PaintFieldEffect : EffectFact {
    public PaintFieldEffect(CompiledWorldFieldPaint paint, string describe) : base(describe: describe) => Paint = paint;

    public CompiledWorldFieldPaint Paint { get; }

    public override bool SubmitsMutation => false;
    public override long Cost(RuleCompileContext context) {
        var diameter = ((2L * Paint.Radius) + 1L);

        return (1L + (diameter * diameter * diameter));
    }
}
