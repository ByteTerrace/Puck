using Puck.World.Protocol;

namespace Puck.World;

/// <summary>The abstract case-type base for <see cref="CompiledWorldEffect"/>'s closed union — one sealed class per
/// <see cref="WorldRuleEffectKind"/>, declared in <c>WorldEffectKinds.cs</c>, each carrying only the parameters that
/// kind's own firing path actually touches. Case types are CLASSES, for the same reason <see cref="WorldOperandFact"/>'s
/// are: nothing at runtime compares two effects for equality or identity, and a value-type case would box on every
/// store into the carrier below anyway. <see cref="Kind"/> and <see cref="Describe"/> are the two members every case
/// carries, set once by the case's own constructor and never overridden afterward — everything else lives on the
/// concrete case type, reached by a type-pattern switch (see <c>WorldServer.FireWorldRuleEffect</c> and
/// <c>WorldRuleWorkBudget.EffectCost</c>) or through one of the narrow shared interfaces below when several cases
/// genuinely share a shape. The <see langword="private protected"/> constructor closes the union to this assembly.</summary>
public abstract class WorldEffectFact {
    private protected WorldEffectFact(WorldRuleEffectKind kind, string describe) {
        Kind = kind;
        Describe = describe;
    }

    /// <summary>Which effect this compiled row fires — one-to-one with the concrete case type.</summary>
    public WorldRuleEffectKind Kind { get; }
    /// <summary>The authored spelling, for the <c>world.rules</c> read-back.</summary>
    public string Describe { get; }
}

/// <summary>Shared shape for the case types whose firing path addresses a state cell through a (row, key-or-indirection)
/// pair before the kind-specific work runs — <see cref="WriteEffect"/>, <see cref="CountdownEffect"/>,
/// <see cref="ScheduleStateEffect"/>, <see cref="RemoveStateCellEffect"/>, <see cref="GenerateEffect"/>, and the four
/// whole-row HUD/placement effects — so <c>WorldServer.FireWorldRuleEffect</c> can resolve the one destination-key
/// indirection every one of them shares without a type-pattern switch enumerating each case.</summary>
public interface IStateAddressedEffect {
    /// <summary>The destination state row name, or a HUD panel/placement id for a whole-row upsert/remove.</summary>
    string Row { get; }
    /// <summary>The destination cell key (or an unused constant for a whole-row upsert/remove).</summary>
    string Key { get; }
    /// <summary>The live key indirection (<see cref="RuleFacts.CellKeyPrefix"/>), or <see langword="null"/> for
    /// a literal <see cref="Key"/>.</summary>
    CompiledCellRef? KeyFrom { get; }
}

/// <summary>Widens <see cref="IStateAddressedEffect"/> with the set/add write mode — <see cref="WriteEffect"/>,
/// <see cref="CountdownEffect"/> (always <see cref="WorldDocumentWriteKind.Add"/>), and
/// <see cref="ScheduleStateEffect"/> (always <see cref="WorldDocumentWriteKind.Set"/>).</summary>
public interface IStateWriteEffect : IStateAddressedEffect {
    /// <summary>Set or add.</summary>
    WorldDocumentWriteKind Write { get; }
}

/// <summary>Shared shape for the two case types whose numeric value is read live rather than carried as a literal —
/// <see cref="WriteEffect"/> and <see cref="PushStateEffect"/> — so the shared cost/refusal walk that inspects a
/// live source needs no type-pattern switch between them.</summary>
public interface IValueSourcedEffect {
    /// <summary>The compiled numeric expression, or <see langword="null"/> for another source spelling.</summary>
    CompiledWorldExpressionToken[]? Expression { get; }
    /// <summary>The live copy-source operand, or <see langword="null"/> when the value is a literal or an
    /// expression instead.</summary>
    CompiledWorldOperand? From { get; }
    /// <summary>The authored constant, pre-converted to the destination row's raw encoding at compile time — read
    /// only when neither <see cref="Expression"/> nor <see cref="From"/> applies.</summary>
    long RawValue { get; }
}

/// <summary>One compiled world-rule effect — a closed union over <see cref="WorldEffectFact"/>'s case hierarchy
/// (<c>WorldEffectKinds.cs</c>), one case per <see cref="WorldRuleEffectKind"/>. See <see cref="WorldEffectFact"/>'s
/// own remarks for why the case types are classes and why dispatch is a type-pattern switch rather than a per-kind
/// field on this carrier.</summary>
[Union]
public readonly partial struct CompiledWorldEffect : IUnion {
    private readonly WorldEffectFact? m_value;

    /// <summary>The one live case. <see langword="null"/> only for a default-initialized carrier, which no compiled
    /// rule ever installs (every effect is minted by <c>WorldRuleCompiler.CompileEffect</c> or a sibling resolver).</summary>
    public WorldEffectFact? Value => m_value;
    object? IUnion.Value => m_value;

    /// <summary>Whether this carrier holds a case at all — <see langword="false"/> only for <see langword="default"/>.</summary>
    public bool HasValue => (m_value is not null);

    /// <summary>Which effect this compiled row fires (<see cref="WorldEffectFact.Kind"/>).</summary>
    public WorldRuleEffectKind Kind => m_value!.Kind;
    /// <summary>The authored spelling, for the <c>world.rules</c> read-back (<see cref="WorldEffectFact.Describe"/>).</summary>
    public string Describe => m_value!.Describe;

    private bool TryGetCore<T>([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out T? value) where T : WorldEffectFact {
        if (m_value is T typed) {
            value = typed;
            return true;
        }
        value = null;
        return false;
    }

    /// <summary>Reads the carried case as a <see cref="WriteEffect"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out WriteEffect? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as a <see cref="CountdownEffect"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CountdownEffect? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as a <see cref="GenerateEffect"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out GenerateEffect? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as an <see cref="UpsertHudPanelEffect"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out UpsertHudPanelEffect? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as a <see cref="RemoveHudPanelEffect"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RemoveHudPanelEffect? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as an <see cref="UpsertPlacementEffect"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out UpsertPlacementEffect? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as a <see cref="RemovePlacementEffect"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RemovePlacementEffect? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as a <see cref="SaveEffect"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SaveEffect? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as a <see cref="PoseEffect"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PoseEffect? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as a <see cref="RemoveStateCellEffect"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RemoveStateCellEffect? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as a <see cref="ScheduleStateEffect"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ScheduleStateEffect? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as a <see cref="TransactionEffect"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TransactionEffect? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as an <see cref="EmitCueEffect"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out EmitCueEffect? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as a <see cref="BodyEffect"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out BodyEffect? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as a <see cref="PaintFieldEffect"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PaintFieldEffect? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as a <see cref="TransformStateEffect"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TransformStateEffect? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as a <see cref="PushStateEffect"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PushStateEffect? value) => TryGetCore(out value);

    /// <summary>Constructs a carrier over a <see cref="WriteEffect"/> case.</summary>
    public CompiledWorldEffect(WriteEffect value) => m_value = value;
    /// <summary>Constructs a carrier over a <see cref="CountdownEffect"/> case.</summary>
    public CompiledWorldEffect(CountdownEffect value) => m_value = value;
    /// <summary>Constructs a carrier over a <see cref="GenerateEffect"/> case.</summary>
    public CompiledWorldEffect(GenerateEffect value) => m_value = value;
    /// <summary>Constructs a carrier over an <see cref="UpsertHudPanelEffect"/> case.</summary>
    public CompiledWorldEffect(UpsertHudPanelEffect value) => m_value = value;
    /// <summary>Constructs a carrier over a <see cref="RemoveHudPanelEffect"/> case.</summary>
    public CompiledWorldEffect(RemoveHudPanelEffect value) => m_value = value;
    /// <summary>Constructs a carrier over an <see cref="UpsertPlacementEffect"/> case.</summary>
    public CompiledWorldEffect(UpsertPlacementEffect value) => m_value = value;
    /// <summary>Constructs a carrier over a <see cref="RemovePlacementEffect"/> case.</summary>
    public CompiledWorldEffect(RemovePlacementEffect value) => m_value = value;
    /// <summary>Constructs a carrier over a <see cref="SaveEffect"/> case.</summary>
    public CompiledWorldEffect(SaveEffect value) => m_value = value;
    /// <summary>Constructs a carrier over a <see cref="PoseEffect"/> case.</summary>
    public CompiledWorldEffect(PoseEffect value) => m_value = value;
    /// <summary>Constructs a carrier over a <see cref="RemoveStateCellEffect"/> case.</summary>
    public CompiledWorldEffect(RemoveStateCellEffect value) => m_value = value;
    /// <summary>Constructs a carrier over a <see cref="ScheduleStateEffect"/> case.</summary>
    public CompiledWorldEffect(ScheduleStateEffect value) => m_value = value;
    /// <summary>Constructs a carrier over a <see cref="TransactionEffect"/> case.</summary>
    public CompiledWorldEffect(TransactionEffect value) => m_value = value;
    /// <summary>Constructs a carrier over an <see cref="EmitCueEffect"/> case.</summary>
    public CompiledWorldEffect(EmitCueEffect value) => m_value = value;
    /// <summary>Constructs a carrier over a <see cref="BodyEffect"/> case.</summary>
    public CompiledWorldEffect(BodyEffect value) => m_value = value;
    /// <summary>Constructs a carrier over a <see cref="PaintFieldEffect"/> case.</summary>
    public CompiledWorldEffect(PaintFieldEffect value) => m_value = value;
    /// <summary>Constructs a carrier over a <see cref="TransformStateEffect"/> case.</summary>
    public CompiledWorldEffect(TransformStateEffect value) => m_value = value;
    /// <summary>Constructs a carrier over a <see cref="PushStateEffect"/> case.</summary>
    public CompiledWorldEffect(PushStateEffect value) => m_value = value;
}
