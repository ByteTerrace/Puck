namespace Puck.World;

public sealed partial class WorldValueDomainGroup {
    private readonly Dictionary<WorldValueTuple, TupleField> m_tuples = new(ReferenceEqualityComparer.Instance);

    private sealed record TupleField(string Path, string Source, float[] Initial, string Requirement);

    /// <summary>Resolves raw scalar fields, then admits their scalar domains and derived constraint together.
    /// Rejected candidates never replace the last presented operands. Names and seeds are prepared once per tuple.</summary>
    /// <param name="tuple">The row's prepared ordered field and constraint description.</param>
    /// <param name="values">The current shared resolver.</param>
    /// <returns>The guard-owned valid operands, in the tuple's declared order.</returns>
    public ReadOnlySpan<float> Tuple(WorldValueTuple tuple, WorldValueResolver values) {
        if (!m_tuples.TryGetValue(tuple, out var field)) {
            var initial = new float[tuple.Operands.Length];
            tuple.Initial(m_initial, initial);
            var sources = new string[tuple.Operands.Length];
            var domains = new string[tuple.Operands.Length];
            for (var index = 0; index < sources.Length; index++) {
                var operand = tuple.Operands[index];
                sources[index] = operand.Name + "=" + WorldValueDomain.SourceOf(operand.Value ?? new BindableScalar(operand.Default));
                domains[index] = operand.Name + " in " + operand.Domain;
            }
            field = new(path + "." + tuple.Name, string.Join(", ", sources), initial,
                tuple.Requirement + "; " + string.Join(", ", domains));
            m_tuples.Add(tuple, field);
        }
        Span<float> resolved = stackalloc float[tuple.Operands.Length];
        for (var index = 0; index < resolved.Length; index++) {
            var operand = tuple.Operands[index];
            resolved[index] = operand.Value is { } value ? (float)values.Scalar(value, operand.Default) : operand.Default;
        }
        return guard.Tuple(field.Path, field.Source, resolved, field.Initial, tuple.Predicate, field.Requirement, tuple.Operands);
    }
}
