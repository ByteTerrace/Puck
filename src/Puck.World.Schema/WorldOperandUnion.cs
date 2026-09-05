namespace Puck.World;

/// <summary>The abstract case-type base for <see cref="CompiledWorldOperand"/>'s closed union — one sealed class per
/// <see cref="WorldRuleFactKind"/>, declared in <c>WorldOperandKinds.cs</c>, each carrying only the parameters that
/// kind's own reader actually touches. Case types are CLASSES, never records or structs: nothing at runtime compares
/// two operands for equality or identity, so a generated structural <c>Equals</c> would be a hazard nobody asked for,
/// and a value-type case would box on every store into the carrier below anyway. <see cref="Kind"/> and
/// <see cref="ValueKind"/> are the two members every case carries, set once by the case's own constructor and never
/// overridden afterward — everything else lives on the concrete case type, reached by a type-pattern switch (see
/// <c>WorldServer.ReadWorldFact</c> and <c>WorldRuleWorkBudget.OperandCost</c>) or through one of the narrow shared
/// interfaces below when several cases genuinely share a shape. The <see langword="private protected"/> constructor
/// closes the union to this assembly — no external project may add a case.</summary>
public abstract class WorldOperandFact {
    private protected WorldOperandFact(WorldRuleFactKind kind, CellKind valueKind) {
        Kind = kind;
        ValueKind = valueKind;
    }

    /// <summary>Which live quantity this operand reads — one-to-one with the concrete case type.</summary>
    public WorldRuleFactKind Kind { get; }
    /// <summary>The raw encoding this operand's value is returned in (see <c>Server.WorldServer.ReadWorldFact</c>).</summary>
    public CellKind ValueKind { get; }
}

/// <summary>Shared shape for the four case types that address a state row through a (row, key-or-indirection)
/// pair — <see cref="StateCellOperand"/>, <see cref="BoardOperand"/>, <see cref="PatternOperand"/>, and
/// <see cref="SymmetryOperand"/> — so a generic caller that must accept any of them (a token-domain check inside a
/// pattern's own value expression, <c>WorldRuleCompiler.TryCompilePatternValue</c>) can read the row name and the
/// live key indirection without a type-pattern switch enumerating every other case.</summary>
public interface IStateAddressedOperand {
    /// <summary>The row this operand addresses.</summary>
    string Row { get; }
    /// <summary>The live key indirection (<see cref="RuleFacts.CellKeyPrefix"/>), or <see langword="null"/> for
    /// a literal key.</summary>
    CompiledCellRef? KeyFrom { get; }
}

/// <summary>One resolved operand of a world-rule comparison — a closed union over <see cref="WorldOperandFact"/>'s
/// case hierarchy (<c>WorldOperandKinds.cs</c>), one case per <see cref="WorldRuleFactKind"/>. Both sides of a
/// <see cref="ActionPredicate.CompareState"/> conjunct — the primary and, when spelled, the comparand — carry this
/// same carrier type, read by the same <c>Server.WorldServer.ReadWorldFact</c> switch, so the two sides can never
/// drift into two readings of one name. See <see cref="WorldOperandFact"/>'s own remarks for why the case types are
/// classes and why dispatch is a type-pattern switch rather than a per-kind field on this carrier.</summary>
[Union]
public readonly partial struct CompiledWorldOperand : IUnion {
    private readonly WorldOperandFact? m_value;

    /// <summary>The one live case. <see langword="null"/> only for a default-initialized carrier, which no compiled
    /// rule ever installs (every operand is minted by <c>WorldRuleCompiler.ResolveOperand</c> or a sibling resolver).</summary>
    public WorldOperandFact? Value => m_value;
    object? IUnion.Value => m_value;

    /// <summary>Whether this carrier holds a case at all — <see langword="false"/> only for <see langword="default"/>.</summary>
    public bool HasValue => (m_value is not null);

    /// <summary>Which live quantity this operand reads (<see cref="WorldOperandFact.Kind"/>).</summary>
    public WorldRuleFactKind Kind => m_value!.Kind;
    /// <summary>The raw encoding this operand's value is returned in (<see cref="WorldOperandFact.ValueKind"/>).</summary>
    public CellKind ValueKind => m_value!.ValueKind;

    private bool TryGetCore<T>([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out T? value) where T : WorldOperandFact {
        if (m_value is T typed) {
            value = typed;
            return true;
        }
        value = null;
        return false;
    }

    /// <summary>Reads the carried case as a <see cref="StateCellOperand"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out StateCellOperand? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as a <see cref="TickOperand"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TickOperand? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as a <see cref="PopulationOperand"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PopulationOperand? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as a <see cref="PhysicsQuiescentOperand"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PhysicsQuiescentOperand? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as a <see cref="RegionOccupancyOperand"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RegionOccupancyOperand? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as a <see cref="MachineMemoryOperand"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MachineMemoryOperand? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as a <see cref="ReductionOperand"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ReductionOperand? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as an <see cref="ArgBodyOperand"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ArgBodyOperand? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as a <see cref="BodyDistanceOperand"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out BodyDistanceOperand? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as a <see cref="LineOfSightOperand"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out LineOfSightOperand? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as a <see cref="ParkedOperand"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ParkedOperand? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as an <see cref="UprightOperand"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out UprightOperand? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as a <see cref="LinkStalenessOperand"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out LinkStalenessOperand? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as a <see cref="ChannelOperand"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ChannelOperand? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as a <see cref="NearestOperand"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out NearestOperand? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as a <see cref="SymmetryOperand"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SymmetryOperand? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as a <see cref="NavigationOperand"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out NavigationOperand? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as a <see cref="BoardOperand"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out BoardOperand? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as a <see cref="PhaseOperand"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PhaseOperand? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as a <see cref="PatternOperand"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PatternOperand? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as a <see cref="HistoryOperand"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out HistoryOperand? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as a <see cref="ClockOperand"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ClockOperand? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as a <see cref="BindingOperand"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out BindingOperand? value) => TryGetCore(out value);
    /// <summary>Reads the carried case as a <see cref="TableOperand"/> when it holds one.</summary>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TableOperand? value) => TryGetCore(out value);

    /// <summary>Constructs a carrier over a <see cref="StateCellOperand"/> case.</summary>
    public CompiledWorldOperand(StateCellOperand value) => m_value = value;
    /// <summary>Constructs a carrier over a <see cref="TickOperand"/> case.</summary>
    public CompiledWorldOperand(TickOperand value) => m_value = value;
    /// <summary>Constructs a carrier over a <see cref="PopulationOperand"/> case.</summary>
    public CompiledWorldOperand(PopulationOperand value) => m_value = value;
    /// <summary>Constructs a carrier over a <see cref="PhysicsQuiescentOperand"/> case.</summary>
    public CompiledWorldOperand(PhysicsQuiescentOperand value) => m_value = value;
    /// <summary>Constructs a carrier over a <see cref="RegionOccupancyOperand"/> case.</summary>
    public CompiledWorldOperand(RegionOccupancyOperand value) => m_value = value;
    /// <summary>Constructs a carrier over a <see cref="MachineMemoryOperand"/> case.</summary>
    public CompiledWorldOperand(MachineMemoryOperand value) => m_value = value;
    /// <summary>Constructs a carrier over a <see cref="ReductionOperand"/> case.</summary>
    public CompiledWorldOperand(ReductionOperand value) => m_value = value;
    /// <summary>Constructs a carrier over an <see cref="ArgBodyOperand"/> case.</summary>
    public CompiledWorldOperand(ArgBodyOperand value) => m_value = value;
    /// <summary>Constructs a carrier over a <see cref="BodyDistanceOperand"/> case.</summary>
    public CompiledWorldOperand(BodyDistanceOperand value) => m_value = value;
    /// <summary>Constructs a carrier over a <see cref="LineOfSightOperand"/> case.</summary>
    public CompiledWorldOperand(LineOfSightOperand value) => m_value = value;
    /// <summary>Constructs a carrier over a <see cref="ParkedOperand"/> case.</summary>
    public CompiledWorldOperand(ParkedOperand value) => m_value = value;
    /// <summary>Constructs a carrier over an <see cref="UprightOperand"/> case.</summary>
    public CompiledWorldOperand(UprightOperand value) => m_value = value;
    /// <summary>Constructs a carrier over a <see cref="LinkStalenessOperand"/> case.</summary>
    public CompiledWorldOperand(LinkStalenessOperand value) => m_value = value;
    /// <summary>Constructs a carrier over a <see cref="ChannelOperand"/> case.</summary>
    public CompiledWorldOperand(ChannelOperand value) => m_value = value;
    /// <summary>Constructs a carrier over a <see cref="NearestOperand"/> case.</summary>
    public CompiledWorldOperand(NearestOperand value) => m_value = value;
    /// <summary>Constructs a carrier over a <see cref="SymmetryOperand"/> case.</summary>
    public CompiledWorldOperand(SymmetryOperand value) => m_value = value;
    /// <summary>Constructs a carrier over a <see cref="NavigationOperand"/> case.</summary>
    public CompiledWorldOperand(NavigationOperand value) => m_value = value;
    /// <summary>Constructs a carrier over a <see cref="BoardOperand"/> case.</summary>
    public CompiledWorldOperand(BoardOperand value) => m_value = value;
    /// <summary>Constructs a carrier over a <see cref="PhaseOperand"/> case.</summary>
    public CompiledWorldOperand(PhaseOperand value) => m_value = value;
    /// <summary>Constructs a carrier over a <see cref="PatternOperand"/> case.</summary>
    public CompiledWorldOperand(PatternOperand value) => m_value = value;
    /// <summary>Constructs a carrier over a <see cref="HistoryOperand"/> case.</summary>
    public CompiledWorldOperand(HistoryOperand value) => m_value = value;
    /// <summary>Constructs a carrier over a <see cref="ClockOperand"/> case.</summary>
    public CompiledWorldOperand(ClockOperand value) => m_value = value;
    /// <summary>Constructs a carrier over a <see cref="BindingOperand"/> case.</summary>
    public CompiledWorldOperand(BindingOperand value) => m_value = value;
    /// <summary>Constructs a carrier over a <see cref="TableOperand"/> case.</summary>
    public CompiledWorldOperand(TableOperand value) => m_value = value;
}
