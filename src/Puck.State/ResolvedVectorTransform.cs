namespace Puck.State;

/// <summary>A pre-resolved vector transform whose operands are row ordinals, concrete keys, and vector literals, ready for frame application without parsing.</summary>
public abstract record ResolvedVectorTransform {
    private ResolvedVectorTransform() { }


    /// <summary>Applies a weighted mix of vector terms into a target cell.</summary>
    public sealed record Mix(int TargetRowOrdinal, string TargetRowName, CellName TargetKey, IReadOnlyList<ResolvedMixTerm> Terms) : ResolvedVectorTransform;

    /// <summary>Computes the mean of vector cells in a table, optionally filtered by a boolean row, into a target cell.</summary>
    public sealed record Mean(int TargetRowOrdinal, string TargetRowName, CellName TargetKey, int FromRowOrdinal, string FromRowName, int? WhereRowOrdinal = null, string? WhereRowName = null) : ResolvedVectorTransform;

    /// <summary>Selects the nearest or farthest k vector keys from a source table relative to a query vector.</summary>
    public sealed record Nearest(int TargetRowOrdinal, string TargetRowName, CellName TargetKey, int FromRowOrdinal, string FromRowName, int? QueryRowOrdinal, string? QueryRowName, CellName QueryKey, StateVector? QueryVector, int K, long? Threshold = null, int? WhereRowOrdinal = null, string? WhereRowName = null, CellName? Exclude = null, bool Farthest = false, CellKind IntoKind = CellKind.Int) : ResolvedVectorTransform;

    /// <summary>Remembers a vector into a history table under a key unless a near-duplicate is within threshold.</summary>
    public sealed record Remember(int IntoRowOrdinal, string IntoRowName, CellName Key, int? FromRowOrdinal, string? FromRowName, CellName FromKey, StateVector? FromVector, long UnlessWithinQ16) : ResolvedVectorTransform;
}

/// <summary>One term of a <see cref="ResolvedVectorTransform.Mix"/>: a source cell or vector literal with an integer weight.</summary>
public readonly record struct ResolvedMixTerm(int? SourceRowOrdinal, string? SourceRowName, CellName SourceKey, StateVector? Vector, int Weight);
