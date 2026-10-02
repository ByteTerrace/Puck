namespace Puck.World;

/// <summary>One field of a coupled numeric presentation constraint.</summary>
/// <param name="Name">The property's name within its authored row.</param>
/// <param name="Value">The optional binding or keys.</param>
/// <param name="Default">The field's ordinary absent value.</param>
/// <param name="Domain">Its scalar admission and runtime domain, applied before the coupled constraint.</param>
/// <param name="Predicate">An optional consumed-format constraint owed by every authored key leaf. The tuple's
/// complete predicate includes the same constraint for live values.</param>
/// <param name="Requirement">The author-facing statement of the leaf constraint.</param>
public readonly record struct WorldValueTupleOperand(string Name, BindableScalar? Value, float Default,
    WorldValueDomain Domain, Func<float, bool>? Predicate = null, string? Requirement = null);

/// <summary>A prepared description of coupled presentation operands. Admission and live presentation use the same
/// ordered fields and predicate; the predicate performs numeric checks, never field resolution.</summary>
public sealed class WorldValueTuple {
    private readonly WorldValueTupleOperand[] m_operands;

    /// <summary>Prepares a numeric tuple when its authored row is installed.</summary>
    /// <param name="name">The tuple's diagnostic name within the row.</param>
    /// <param name="operands">The ordered field definitions.</param>
    /// <param name="predicate">The consumed-format numeric constraint.</param>
    /// <param name="requirement">The concrete author-facing requirement.</param>
    public WorldValueTuple(string name, ReadOnlySpan<WorldValueTupleOperand> operands,
        WorldValueTuplePredicate predicate, string requirement) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(predicate);
        if (operands.IsEmpty) { throw new ArgumentException("A numeric tuple requires at least one operand.", nameof(operands)); }
        Name = name;
        m_operands = operands.ToArray();
        Predicate = predicate;
        Requirement = requirement;
    }

    /// <summary>The tuple's diagnostic name.</summary>
    public string Name { get; }
    /// <summary>The ordered scalar fields.</summary>
    public ReadOnlySpan<WorldValueTupleOperand> Operands => m_operands;
    /// <summary>The shared consumed-format predicate.</summary>
    public WorldValueTuplePredicate Predicate { get; }
    /// <summary>The concrete numeric requirement.</summary>
    public string Requirement { get; }

    /// <summary>Resolves the authored initial operands without clamping or holding them.</summary>
    /// <param name="values">The admitted-definition resolver at tick zero.</param>
    /// <param name="destination">Caller-owned storage for all operands.</param>
    public void Initial(WorldValueResolver values, Span<float> destination) {
        if (destination.Length < m_operands.Length) { throw new ArgumentException("Initial tuple storage is too short.", nameof(destination)); }
        for (var index = 0; index < m_operands.Length; index++) {
            var operand = m_operands[index];
            destination[index] = operand.Value is { } value ? (float)values.Scalar(value, operand.Default) : operand.Default;
        }
    }
}
