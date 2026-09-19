namespace Puck.State;

/// <summary>One term of a <c>mix</c>: a vector and the integer weight it enters the sum with.</summary>
/// <param name="Source">Where the term's components come from.</param>
/// <param name="Weight">The term's weight, non-zero and within
/// <c>[-StateCapacity.MaxMixWeight, StateCapacity.MaxMixWeight]</c>.</param>
public readonly record struct VectorMixTerm(VectorSource Source, int Weight);
public static partial class ArenaVectorTransforms {
    /// <summary>Writes the normalized weighted sum of 1 to <c>StateCapacity.MaxMixTerms</c> vector terms into one
    /// cell.</summary>
    /// <param name="arena">The arena holding the rows.</param>
    /// <param name="intoRowOrdinal">The destination row's catalog ordinal.</param>
    /// <param name="intoKey">The destination cell key, interned by the arena's catalog.</param>
    /// <param name="terms">The weighted terms, each carrying the destination row's dimension count.</param>
    /// <param name="refusal">Why the transform refused, or the default on success.</param>
    /// <returns><see langword="true"/> when the mix was written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> is <see langword="null"/>.</exception>
    public static bool TryMix(StateArena arena, int intoRowOrdinal, CellKey intoKey, ReadOnlySpan<VectorMixTerm> terms, out VectorTransformRefusal refusal) {
        ArgumentNullException.ThrowIfNull(argument: arena);

        if (!VectorColumn.TryOpen(
            arena: arena,
            column: out var into,
            refusal: out refusal,
            rowOrdinal: intoRowOrdinal
        )) {
            return false;
        }
        if ((terms.Length == 0) || (terms.Length > StateCapacity.MaxMixTerms)) {
            refusal = Refused(
                code: RuleRefusal.VectorMixTerms,
                reason: $"a mix takes 1 to {StateCapacity.MaxMixTerms} terms, not {terms.Length}"
            );

            return false;
        }

        var vectors = new ReadOnlyMemory<sbyte>[terms.Length];
        var weights = new int[terms.Length];

        for (var index = 0; (index < terms.Length); index++) {
            var term = terms[index];

            if (!TryResolveSource(
                arena: arena,
                components: out vectors[index],
                dimensions: into.Dimensions,
                refusal: out refusal,
                role: $"mix term {index}",
                source: term.Source
            )) {
                return false;
            }

            weights[index] = term.Weight;
        }

        Span<sbyte> destination = stackalloc sbyte[into.Dimensions];

        if (!VectorTransforms.TryMix(
            destination: destination,
            refusal: out var code,
            vectors: vectors,
            weights: weights
        )) {
            refusal = Refused(
                code: code.Value,
                reason: $"row '{into.RowName()}' cell '{into.KeyName(key: intoKey)}' takes no mix of its {terms.Length} terms"
            );

            return false;
        }

        var mark = arena.BeginScope();

        if (!TryAdmitWrite(
            components: destination,
            into: into,
            key: intoKey,
            refusal: out refusal
        )) {
            arena.Rewind(mark: mark);

            return false;
        }

        arena.Commit(mark: mark);

        return true;
    }
}
