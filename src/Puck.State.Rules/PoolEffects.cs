namespace Puck.State.Rules;

internal static class PoolEffectWork {
    private const long RetainedObjectBytes = 32L;

    public static long FieldJournalWidth(StatePoolFieldDescriptor field) => (field.Kind switch {
        CellKind.Text => ((ArenaJournal.EntryBytes + RetainedObjectBytes) + (2L * StateCapacity.MaxTextValueLength)),
        CellKind.Vector => (ArenaJournal.EntryBytes + Math.Max(val1: 1L, val2: field.Declaration.Dimensions.GetValueOrDefault())),
        _ => ArenaJournal.EntryBytes,
    });
    public static long TextSourceWidth(string? text) => (2L * (text?.Length ?? 0));
    public static long InsertWidth(StatePoolDescriptor pool, IRuleCostContext context) {
        var units = RowMutationWidth(valueWidth: ArenaJournal.EntryBytes);

        foreach (var field in pool.Fields) {
            units = RuleWorkBudget.SaturatingAdd(
                left: units,
                right: RowMutationWidth(valueWidth: FieldJournalWidth(field: field))
            );
        }
        return units;
    }
    public static long ReleaseWidth(StatePoolDescriptor pool, IRuleCostContext context) {
        var units = RowMutationWidth(valueWidth: ArenaJournal.EntryBytes);

        units = RuleWorkBudget.SaturatingAdd(left: units, right: ArenaJournal.EntryBytes);
        foreach (var field in pool.Fields) {
            units = RuleWorkBudget.SaturatingAdd(
                left: units,
                right: RowMutationWidth(valueWidth: FieldJournalWidth(field: field))
            );
        }
        return units;
    }
    public static long SaturatingMultiply(long left, long right) => (((left != 0L) && (right > (long.MaxValue / left)))
        ? long.MaxValue
        : (left * right));

    // One fixed slot, its member count, and every optional per-cell metadata lane. The typed value
    // includes retained text and vector bytes. A claim clears then initializes its vector, journaling
    // the payload twice; no live-count multiplier remains.
    private static long RowMutationWidth(long valueWidth) => RuleWorkBudget.SaturatingAdd(
        left: (20L * ArenaJournal.EntryBytes),
        right: SaturatingMultiply(left: 2L, right: valueWidth)
    );

    public static long OccupancyWords(StatePoolDescriptor pool) => ((pool.Capacity + 63L) / 64L);

}

/// <summary>Claims one fresh pool instance, then fires its lexical body while that handle occupies a distinct
/// evaluator register. The arena operation is itself atomic; the enclosing firing journal also rewinds the claim
/// when a body initializer or later sibling refuses.</summary>
public sealed class ClaimEffect : RuleEffect {
    private readonly IRuleEffect[][] m_arms;

    /// <summary>Initializes a compiled pool claim.</summary>
    public ClaimEffect(StatePoolDescriptor pool, int bindingSlot, IRuleEffect[] effects, string describe) : base(describe: describe) {
        ArgumentNullException.ThrowIfNull(argument: effects);
        ArgumentNullException.ThrowIfNull(argument: pool);
        BindingSlot = bindingSlot;
        Effects = effects;
        Pool = pool;
        m_arms = [effects];
    }

    /// <inheritdoc/>
    public override IReadOnlyList<IRuleEffect[]> Arms => m_arms;
    /// <summary>Gets the evaluator register holding the fresh handle during <see cref="Effects"/>.</summary>
    public int BindingSlot { get; }
    /// <summary>Gets the claim body.</summary>
    public IRuleEffect[] Effects { get; }
    /// <summary>Gets the claimed pool.</summary>
    public StatePoolDescriptor Pool { get; }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        into.Add(item: new CellAccess(Key: default, RowOrdinal: Pool.DomainRowOrdinal));
        into.Add(item: new CellAccess(Key: default, RowOrdinal: Pool.GenerationRowOrdinal));
        foreach (var effect in Effects) {
            effect.CollectReads(into: into);
        }
    }
    /// <inheritdoc/>
    public override void CollectWrites(List<CellAccess> into) {
        into.Add(item: new CellAccess(IsSet: true, Key: default, RowOrdinal: Pool.DomainRowOrdinal));
        into.Add(item: new CellAccess(IsSet: true, Key: default, RowOrdinal: Pool.GenerationRowOrdinal));
        foreach (var field in Pool.Fields) {
            into.Add(item: new CellAccess(IsSet: true, Key: default, RowOrdinal: field.RowOrdinal));
        }

        foreach (var effect in Effects) {
            effect.CollectWrites(into: into);
        }
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => (RuleWork.Known(units: RuleWorkBudget.SaturatingAdd(
        left: RuleWorkBudget.SaturatingAdd(left: 512L, right: PoolEffectWork.OccupancyWords(pool: Pool)),
        right: PoolEffectWork.InsertWidth(pool: Pool, context: context)
    )) + RuleWorkBudget.EffectsCost(context: context, effects: Effects));
}
/// <summary>Releases one generation-checked pool handle held in an evaluator register.</summary>
public sealed class ReleaseEffect : RuleEffect {
    private readonly IReadOnlyList<StatePoolDescriptor> m_cascadePools;

    /// <summary>Initializes a compiled release.</summary>
    public ReleaseEffect(StatePoolDescriptor pool, int bindingSlot, IReadOnlyList<StatePoolDescriptor> cascadePools, string describe) : base(describe: describe) {
        ArgumentNullException.ThrowIfNull(argument: pool);
        ArgumentNullException.ThrowIfNull(argument: cascadePools);
        BindingSlot = bindingSlot;
        Pool = pool;
        m_cascadePools = cascadePools;
    }

    /// <summary>Gets the evaluator register holding the released handle.</summary>
    public int BindingSlot { get; }
    /// <summary>Gets the pool the handle must belong to.</summary>
    public StatePoolDescriptor Pool { get; }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        CollectPoolReads(pool: Pool, into: into);
        foreach (var cascade in m_cascadePools) {
            CollectPoolReads(into: into, pool: cascade);
        }
    }
    /// <inheritdoc/>
    public override void CollectWrites(List<CellAccess> into) {
        into.Add(item: new CellAccess(IsSet: true, Key: default, RowOrdinal: Pool.DomainRowOrdinal));
        into.Add(item: new CellAccess(IsSet: true, Key: default, RowOrdinal: Pool.GenerationRowOrdinal));
        foreach (var field in Pool.Fields) {
            into.Add(item: new CellAccess(IsSet: true, Key: default, RowOrdinal: field.RowOrdinal));
        }
        foreach (var cascade in m_cascadePools) {
            into.Add(item: new CellAccess(IsSet: true, Key: default, RowOrdinal: cascade.DomainRowOrdinal));
            into.Add(item: new CellAccess(IsSet: true, Key: default, RowOrdinal: cascade.GenerationRowOrdinal));
            foreach (var field in cascade.Fields) {
                into.Add(item: new CellAccess(IsSet: true, Key: default, RowOrdinal: field.RowOrdinal));
            }
        }
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) {
        var units = RuleWorkBudget.SaturatingAdd(left: 512L, right: PoolEffectWork.ReleaseWidth(pool: Pool, context: context));
        var releaseCalls = 1L;
        var scanWidth = 0L;

        // Every recursive release scans every dependent pair domain again, including sparse descendants that are
        // not incident to the handle being released. At most every live pair is released once, so the total number
        // of scans is one for the requested handle plus one for each possibly live cascade handle.
        foreach (var pair in m_cascadePools) {
            var live = pair.MaxLive;
            var pairRelease = RuleWorkBudget.SaturatingAdd(left: 512L, right: PoolEffectWork.ReleaseWidth(context: context, pool: pair));

            units = RuleWorkBudget.SaturatingAdd(left: units, right: PoolEffectWork.SaturatingMultiply(left: live, right: pairRelease));
            releaseCalls = RuleWorkBudget.SaturatingAdd(left: releaseCalls, right: live);
            scanWidth = RuleWorkBudget.SaturatingAdd(left: scanWidth, right: RuleWorkBudget.SaturatingAdd(left: live, right: PoolEffectWork.OccupancyWords(pool: pair)));
        }

        units = RuleWorkBudget.SaturatingAdd(
            left: units,
            right: PoolEffectWork.SaturatingMultiply(left: releaseCalls, right: scanWidth)
        );

        return RuleWork.Known(units: units);
    }

    private static void CollectPoolReads(StatePoolDescriptor pool, List<CellAccess> into) {
        into.Add(item: new CellAccess(Key: default, RowOrdinal: pool.DomainRowOrdinal));
        into.Add(item: new CellAccess(Key: default, RowOrdinal: pool.GenerationRowOrdinal));
        foreach (var field in pool.Fields) {
            into.Add(item: new CellAccess(Key: default, RowOrdinal: field.RowOrdinal));
        }
    }
}
/// <summary>Claims the stable pair slot named by two live endpoint handles and runs a lexical initializer under
/// the resulting generation-checked pair handle.</summary>
public sealed class ClaimPairEffect : RuleEffect {
    private readonly IRuleEffect[][] m_arms;

    /// <summary>Initializes a compiled pair claim.</summary>
    public ClaimPairEffect(StatePoolDescriptor pool, StatePoolDescriptor leftPool, StatePoolDescriptor rightPool, int leftBindingSlot, int rightBindingSlot, int bindingSlot, IRuleEffect[] effects, string describe) : base(describe: describe) {
        ArgumentNullException.ThrowIfNull(argument: effects);
        ArgumentNullException.ThrowIfNull(argument: leftPool);
        ArgumentNullException.ThrowIfNull(argument: pool);
        ArgumentNullException.ThrowIfNull(argument: rightPool);
        BindingSlot = bindingSlot;
        Effects = effects;
        LeftBindingSlot = leftBindingSlot;
        LeftPool = leftPool;
        Pool = pool;
        RightBindingSlot = rightBindingSlot;
        RightPool = rightPool;
        m_arms = [effects];
    }

    /// <inheritdoc/>
    public override IReadOnlyList<IRuleEffect[]> Arms => m_arms;
    /// <summary>Gets the register receiving the claimed pair.</summary>
    public int BindingSlot { get; }
    /// <summary>Gets the claim body.</summary>
    public IRuleEffect[] Effects { get; }
    /// <summary>Gets the left endpoint register.</summary>
    public int LeftBindingSlot { get; }
    /// <summary>Gets the left endpoint pool.</summary>
    public StatePoolDescriptor LeftPool { get; }
    /// <summary>Gets the pair pool.</summary>
    public StatePoolDescriptor Pool { get; }
    /// <summary>Gets the right endpoint register.</summary>
    public int RightBindingSlot { get; }
    /// <summary>Gets the right endpoint pool.</summary>
    public StatePoolDescriptor RightPool { get; }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        into.Add(item: new CellAccess(Key: default, RowOrdinal: Pool.DomainRowOrdinal));
        into.Add(item: new CellAccess(Key: default, RowOrdinal: Pool.GenerationRowOrdinal));
        into.Add(item: new CellAccess(Key: default, RowOrdinal: LeftPool.DomainRowOrdinal));
        into.Add(item: new CellAccess(Key: default, RowOrdinal: LeftPool.GenerationRowOrdinal));
        into.Add(item: new CellAccess(Key: default, RowOrdinal: RightPool.DomainRowOrdinal));
        into.Add(item: new CellAccess(Key: default, RowOrdinal: RightPool.GenerationRowOrdinal));
        foreach (var effect in Effects) {
            effect.CollectReads(into: into);
        }
    }
    /// <inheritdoc/>
    public override void CollectWrites(List<CellAccess> into) {
        into.Add(item: new CellAccess(IsSet: true, Key: default, RowOrdinal: Pool.DomainRowOrdinal));
        foreach (var field in Pool.Fields) {
            into.Add(item: new CellAccess(IsSet: true, Key: default, RowOrdinal: field.RowOrdinal));
        }
        foreach (var effect in Effects) {
            effect.CollectWrites(into: into);
        }
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) {
        // Endpoint validation and pair-slot arithmetic are fixed work; insertion touches one slot per field.
        var units = RuleWorkBudget.SaturatingAdd(
            left: 512L,
            right: PoolEffectWork.InsertWidth(pool: Pool, context: context)
        );

        return (RuleWork.Known(units: units) + RuleWorkBudget.EffectsCost(context: context, effects: Effects));
    }
}
/// <summary>Runs a lexical body once for each full handle snapshotted from a pool. Handles carry generations, so a
/// released and reclaimed slot is not confused with its earlier lifetime.</summary>
public sealed class ForEachPoolEffect : RuleEffect {
    private readonly IRuleEffect[][] m_arms;

    /// <summary>Initializes a compiled pool sweep.</summary>
    public ForEachPoolEffect(StatePoolDescriptor pool, int bindingSlot, IRuleEffect[] effects, string describe) : base(describe: describe) {
        BindingSlot = bindingSlot;
        Effects = effects;
        Pool = pool;
        m_arms = [effects];
    }

    /// <inheritdoc/>
    public override IReadOnlyList<IRuleEffect[]> Arms => m_arms;
    /// <summary>Gets the body binding's register slot.</summary>
    public int BindingSlot { get; }
    /// <summary>Gets the per-instance body.</summary>
    public IRuleEffect[] Effects { get; }
    /// <summary>Gets the snapshotted pool.</summary>
    public StatePoolDescriptor Pool { get; }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        into.Add(item: new CellAccess(Key: default, RowOrdinal: Pool.DomainRowOrdinal));
        into.Add(item: new CellAccess(Key: default, RowOrdinal: Pool.GenerationRowOrdinal));
        foreach (var effect in Effects) {
            effect.CollectReads(into: into);
        }
    }
    /// <inheritdoc/>
    public override void CollectWrites(List<CellAccess> into) {
        foreach (var effect in Effects) {
            effect.CollectWrites(into: into);
        }
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) {
        var capacity = (Pool.IsPair
            ? Pool.MaxLive
            : context.RowCapacity(rowOrdinal: Pool.DomainRowOrdinal));
        var snapshot = RuleWorkBudget.SaturatingAdd(left: PoolEffectWork.SaturatingMultiply(left: capacity, right: 3L), right: PoolEffectWork.OccupancyWords(pool: Pool));

        return (RuleWork.Known(units: snapshot) + (capacity * RuleWorkBudget.EffectsCost(context: context, effects: Effects)));
    }
}
/// <summary>Reads one field addressed by a typed, generation-checked instance binding.</summary>
public sealed class InstanceFieldOperand : RuleOperand {
    /// <summary>Initializes an instance-field operand.</summary>
    public InstanceFieldOperand(StatePoolDescriptor pool, StatePoolFieldDescriptor field, int bindingSlot) : base(valueKind: field.Kind) {
        ArgumentNullException.ThrowIfNull(argument: pool);
        BindingSlot = bindingSlot;
        Field = field;
        Pool = pool;
    }

    /// <summary>Gets the evaluator register that supplies the handle.</summary>
    public int BindingSlot { get; }
    /// <summary>Gets field storage metadata.</summary>
    public StatePoolFieldDescriptor Field { get; }
    /// <summary>Gets the handle's owning pool.</summary>
    public StatePoolDescriptor Pool { get; }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        into.Add(item: new CellAccess(Key: default, RowOrdinal: Pool.DomainRowOrdinal));
        into.Add(item: new CellAccess(Key: default, RowOrdinal: Pool.GenerationRowOrdinal));
        into.Add(item: new CellAccess(Key: default, RowOrdinal: Field.RowOrdinal));
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => RuleWork.Known(units: RuleWorkBudget.SaturatingAdd(
        left: 2L,
        right: PoolEffectWork.FieldJournalWidth(field: Field)
    ));
    /// <inheritdoc/>
    public override RuleFact Read(IStateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);
        var bindings = reader.InstanceBindings;

        if (((uint)BindingSlot) >= ((uint)bindings.Length)) {
            return RuleFact.Absent(kind: ValueKind);
        }

        var handle = bindings[BindingSlot];

        return (((handle.PoolOrdinal == Pool.Ordinal) && reader.Arena.TryReadLiveRaw(fieldOrdinal: Field.Ordinal, handle: handle, raw: out var raw, time: reader.Time))
            ? RuleFact.Finite(kind: ValueKind, value: raw)
            : RuleFact.Absent(kind: ValueKind));
    }
}
/// <summary>Reads the currently live lifetime in one declared pool slot.</summary>
public sealed class StaticInstanceFieldOperand : RuleOperand {
    /// <summary>Initializes a direct logical pool-field read.</summary>
    public StaticInstanceFieldOperand(StatePoolDescriptor pool, StatePoolFieldDescriptor field, StateInstanceHandle handle) : base(valueKind: field.Kind) { Pool = pool; Field = field; Handle = handle; }

    /// <summary>Gets the field metadata.</summary>
    public StatePoolFieldDescriptor Field { get; }
    /// <summary>Gets the compiled slot address. Its generation is resolved from the arena at evaluation time.</summary>
    public StateInstanceHandle Handle { get; }
    /// <summary>Gets the pool metadata.</summary>
    public StatePoolDescriptor Pool { get; }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) { into.Add(item: new CellAccess(Pool.DomainRowOrdinal, default)); into.Add(item: new CellAccess(Pool.GenerationRowOrdinal, default)); into.Add(item: new CellAccess(Field.RowOrdinal, default)); }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => RuleWork.Known(units: RuleWorkBudget.SaturatingAdd(
        left: 2L,
        right: PoolEffectWork.FieldJournalWidth(field: Field)
    ));
    /// <inheritdoc/>
    public override RuleFact Read(IStateReader reader) {
        return ((reader.Arena.TryResolvePoolSlot(poolOrdinal: Pool.Ordinal, slot: Handle.Slot, handle: out var handle) &&
            reader.Arena.TryReadLiveRaw(fieldOrdinal: Field.Ordinal, handle: handle, raw: out var raw, time: reader.Time))
            ? RuleFact.Finite(kind: ValueKind, value: raw)
            : RuleFact.Absent(kind: ValueKind));
    }
}
/// <summary>Writes the currently live lifetime in one declared pool slot.</summary>
public sealed class StaticInstanceFieldWriteEffect : RuleEffect, IValueSourcedEffect {
    /// <summary>Initializes a direct logical pool-field write.</summary>
    public StaticInstanceFieldWriteEffect(StatePoolDescriptor pool, StatePoolFieldDescriptor field, StateInstanceHandle handle, StateWriteKind write, in CompiledValueSource source, string? text, string describe) : base(describe) { Pool = pool; Field = field; Handle = handle; Write = write; Source = source; Text = text; }

    /// <summary>Gets the field metadata.</summary>
    public StatePoolFieldDescriptor Field { get; }
    /// <summary>Gets the compiled slot address. Its generation is resolved from the arena at evaluation time.</summary>
    public StateInstanceHandle Handle { get; }
    /// <summary>Gets the pool metadata.</summary>
    public StatePoolDescriptor Pool { get; }
    /// <inheritdoc/>
    public CompiledValueSource Source { get; }
    /// <summary>Gets the literal text source.</summary>
    public string? Text { get; }
    /// <summary>Gets the write kind.</summary>
    public StateWriteKind Write { get; }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        Source.CollectReads(into: into);
        into.Add(item: new CellAccess(Pool.DomainRowOrdinal, default));
        into.Add(item: new CellAccess(Pool.GenerationRowOrdinal, default));
        if (Write == StateWriteKind.Add) {
            into.Add(item: new CellAccess(Field.RowOrdinal, default));
        }
    }
    /// <inheritdoc/>
    public override void CollectWrites(List<CellAccess> into) => into.Add(item: new CellAccess(Field.RowOrdinal, default, (Write == StateWriteKind.Set)));
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => (RuleWork.Known(units: RuleWorkBudget.SaturatingAdd(
        left: RuleWorkBudget.SaturatingAdd(left: 2L, right: PoolEffectWork.FieldJournalWidth(field: Field)),
        right: PoolEffectWork.TextSourceWidth(text: Text)
    )) + Source.Cost(context));
}
/// <summary>Writes one qualified field through its bound, generation-checked instance handle.</summary>
public sealed class InstanceFieldWriteEffect : RuleEffect, IValueSourcedEffect {
    /// <summary>Initializes a compiled qualified-field write.</summary>
    public InstanceFieldWriteEffect(StatePoolDescriptor pool, StatePoolFieldDescriptor field, int bindingSlot, StateWriteKind write, in CompiledValueSource source, string? text, string describe) : base(describe: describe) {
        BindingSlot = bindingSlot;
        Field = field;
        Pool = pool;
        Source = source;
        Text = text;
        Write = write;
    }

    /// <summary>Gets the handle register.</summary>
    public int BindingSlot { get; }
    /// <summary>Gets the target field.</summary>
    public StatePoolFieldDescriptor Field { get; }
    /// <summary>Gets the target pool.</summary>
    public StatePoolDescriptor Pool { get; }
    /// <inheritdoc/>
    public CompiledValueSource Source { get; }
    /// <summary>Gets the literal text, when the field is text-backed.</summary>
    public string? Text { get; }
    /// <summary>Gets whether this replaces or adds.</summary>
    public StateWriteKind Write { get; }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        Source.CollectReads(into: into);
        into.Add(item: new CellAccess(Key: default, RowOrdinal: Pool.DomainRowOrdinal));
        into.Add(item: new CellAccess(Key: default, RowOrdinal: Pool.GenerationRowOrdinal));
        if (Write == StateWriteKind.Add) {
            into.Add(item: new CellAccess(Key: default, RowOrdinal: Field.RowOrdinal));
        }
    }
    /// <inheritdoc/>
    public override void CollectWrites(List<CellAccess> into) => into.Add(item: new CellAccess(IsSet: (Write == StateWriteKind.Set), Key: default, RowOrdinal: Field.RowOrdinal));
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => (RuleWork.Known(units: RuleWorkBudget.SaturatingAdd(
        left: RuleWorkBudget.SaturatingAdd(left: 2L, right: PoolEffectWork.FieldJournalWidth(field: Field)),
        right: PoolEffectWork.TextSourceWidth(text: Text)
    )) + Source.Cost(context: context));
}
/// <summary>Writes an absolute due tick to an integer field through either a lexical instance binding or a static pool slot.</summary>
public sealed class InstanceFieldScheduleEffect : RuleEffect {
    /// <summary>Initializes a lexical pool-field schedule.</summary>
    /// <param name="pool">The target pool.</param>
    /// <param name="field">The integer target field.</param>
    /// <param name="bindingSlot">The lexical handle register.</param>
    /// <param name="delayTicks">The delay in simulation ticks.</param>
    /// <param name="describe">The authored spelling.</param>
    public InstanceFieldScheduleEffect(StatePoolDescriptor pool, StatePoolFieldDescriptor field, int bindingSlot, long delayTicks, string describe) : base(describe) {
        Pool = pool; Field = field; BindingSlot = bindingSlot; DelayTicks = delayTicks;
    }
    /// <summary>Initializes a static pool-field schedule.</summary>
    /// <param name="pool">The target pool.</param>
    /// <param name="field">The integer target field.</param>
    /// <param name="handle">The compiled static slot address.</param>
    /// <param name="delayTicks">The delay in simulation ticks.</param>
    /// <param name="describe">The authored spelling.</param>
    public InstanceFieldScheduleEffect(StatePoolDescriptor pool, StatePoolFieldDescriptor field, StateInstanceHandle handle, long delayTicks, string describe) : base(describe) {
        Pool = pool; Field = field; StaticSlot = handle.Slot; DelayTicks = delayTicks;
    }

    /// <summary>Gets the lexical binding register, or -1 for a static slot.</summary>
    public int BindingSlot { get; } = -1;
    /// <summary>Gets the delay in simulation ticks.</summary>
    public long DelayTicks { get; }
    /// <summary>Gets the target field.</summary>
    public StatePoolFieldDescriptor Field { get; }
    /// <summary>Gets the target pool.</summary>
    public StatePoolDescriptor Pool { get; }
    /// <summary>Gets the static slot, or -1 for a lexical binding.</summary>
    public int StaticSlot { get; } = -1;
    /// <inheritdoc/>
    public override EffectNeeds Needs => EffectNeeds.ReadsTick;

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) { into.Add(item: new CellAccess(RowOrdinal: Pool.DomainRowOrdinal, Key: default)); into.Add(item: new CellAccess(RowOrdinal: Pool.GenerationRowOrdinal, Key: default)); }
    /// <inheritdoc/>
    public override void CollectWrites(List<CellAccess> into) => into.Add(item: new CellAccess(RowOrdinal: Field.RowOrdinal, Key: default, IsSet: true));
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => RuleWork.Known(units: RuleWorkBudget.SaturatingAdd(left: 2L, right: PoolEffectWork.FieldJournalWidth(field: Field)));
}
/// <summary>A vector source for a qualified pool field: a literal, an ordinary state vector, or another qualified
/// field. Each case preserves the vector's declared space until the arena validates the target write.</summary>
public sealed class InstanceVectorSource {
    /// <summary>Initializes a literal or ordinary compiled vector source.</summary>
    public InstanceVectorSource(CompiledVector vector) { Vector = vector; }
    /// <summary>Initializes a qualified field vector source.</summary>
    public InstanceVectorSource(StatePoolDescriptor pool, StatePoolFieldDescriptor field, int bindingSlot) { Pool = pool; Field = field; BindingSlot = bindingSlot; }

    /// <summary>Gets the bound source register, or -1 for an ordinary vector.</summary>
    public int BindingSlot { get; } = -1;
    /// <summary>Gets the bound source field.</summary>
    public StatePoolFieldDescriptor Field { get; }
    /// <summary>Gets the bound source pool.</summary>
    public StatePoolDescriptor? Pool { get; }
    /// <summary>Gets the ordinary source, or null for a bound field.</summary>
    public CompiledVector? Vector { get; }

    /// <summary>Appends source dependencies.</summary>
    public void CollectReads(List<CellAccess> into) {
        Vector?.CollectReads(into: into);
        if (Pool is { } pool) {
            into.Add(item: new CellAccess(Key: default, RowOrdinal: pool.DomainRowOrdinal));
            into.Add(item: new CellAccess(Key: default, RowOrdinal: pool.GenerationRowOrdinal));
            into.Add(item: new CellAccess(Key: default, RowOrdinal: Field.RowOrdinal));
        }
    }
}
/// <summary>Writes a vector into a generation-checked qualified field.</summary>
public sealed class InstanceFieldVectorWriteEffect : RuleEffect {
    /// <summary>Initializes the effect.</summary>
    public InstanceFieldVectorWriteEffect(StatePoolDescriptor pool, StatePoolFieldDescriptor field, int bindingSlot, InstanceVectorSource source, string describe) : base(describe: describe) { Pool = pool; Field = field; BindingSlot = bindingSlot; Source = source; }

    /// <summary>Gets the target binding slot.</summary>
    public int BindingSlot { get; }
    /// <summary>Gets target field metadata.</summary>
    public StatePoolFieldDescriptor Field { get; }
    /// <summary>Gets target pool metadata.</summary>
    public StatePoolDescriptor Pool { get; }
    /// <summary>Gets the vector source.</summary>
    public InstanceVectorSource Source { get; }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) { Source.CollectReads(into: into); into.Add(item: new CellAccess(Key: default, RowOrdinal: Pool.DomainRowOrdinal)); into.Add(item: new CellAccess(Key: default, RowOrdinal: Pool.GenerationRowOrdinal)); }
    /// <inheritdoc/>
    public override void CollectWrites(List<CellAccess> into) => into.Add(item: new CellAccess(IsSet: true, Key: default, RowOrdinal: Field.RowOrdinal));
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) {
        var sourceWidth = ((long)(Source.Vector?.Space.Dimensions ?? Source.Field.Declaration.Dimensions.GetValueOrDefault()));

        return RuleWork.Known(units: RuleWorkBudget.SaturatingAdd(
            left: RuleWorkBudget.SaturatingAdd(left: 2L, right: sourceWidth),
            right: PoolEffectWork.FieldJournalWidth(field: Field)
        ));
    }
}
