namespace Puck.State.Rules;

public static partial class ArenaTransforms {
    /// <summary>Resolves one compiled vector effect into the transform the arena applies.</summary>
    /// <param name="effect">The compiled effect.</param>
    /// <param name="catalog">The catalog the destination key of a <c>remember</c> is named from.</param>
    /// <param name="reader">The reader a live key resolves against, or <see langword="null"/> when the caller
    /// carries no bindings and a live key is therefore refused.</param>
    /// <param name="transform">The resolved transform, on success.</param>
    /// <param name="reason">Why the effect did not resolve, or empty on success.</param>
    /// <returns><see langword="true"/> when the effect resolved; <see langword="false"/> when a live key's
    /// computation faulted, a remember's key names no cell, or the effect is no vector transform.</returns>
    /// <remarks>This is the one conversion: the evaluator takes it with its host, so each term's own live key
    /// resolves fresh per firing, and a host's command path takes it with no reader, where every key is already
    /// literal.</remarks>
    public static bool TryVectorTransform(IRuleEffect effect, StateCatalog catalog, IStateReader? reader, out ArenaTransform? transform, out string reason) {
        var faulted = false;

        reason = string.Empty;
        transform = (effect switch {
            VectorCopyEffect copy => new ArenaTransform.Copy(
            From: SourceOf(
                faulted: ref faulted,
                reader: reader,
                vector: copy.Source
            ),
            IntoKey: KeyOf(
                faulted: ref faulted,
                key: copy.Key,
                keyFrom: copy.KeyFrom,
                reader: reader
            ),
            IntoRowOrdinal: copy.RowOrdinal
        ),
            VectorMixEffect mix => MixOf(
            faulted: ref faulted,
            mix: mix,
            reader: reader
        ),
            VectorMeanEffect mean => new ArenaTransform.Mean(
            FromRowOrdinal: mean.FromRowOrdinal,
            IntoKey: KeyOf(
                faulted: ref faulted,
                key: mean.Key,
                keyFrom: mean.KeyFrom,
                reader: reader
            ),
            IntoRowOrdinal: mean.RowOrdinal,
            WhereRowOrdinal: mean.WhereRowOrdinal
        ),
            VectorNearestEffect nearest => new ArenaTransform.Nearest(
            Exclude: KeyOf(
                faulted: ref faulted,
                key: nearest.ExcludeKey,
                keyFrom: nearest.ExcludeKeyFrom,
                reader: reader
            ),
            Farthest: nearest.Farthest,
            FromRowOrdinal: nearest.FromRowOrdinal,
            IntoRowOrdinal: nearest.RowOrdinal,
            K: nearest.K,
            Query: SourceOf(
                faulted: ref faulted,
                reader: reader,
                vector: nearest.Query
            ),
            Threshold: nearest.Threshold,
            WhereRowOrdinal: nearest.WhereRowOrdinal
        ),
            VectorRememberEffect remember => RememberOf(
            catalog: catalog,
            faulted: ref faulted,
            reader: reader,
            remember: remember
        ),
            _ => null,
        });

        if (faulted) {
            transform = null;
            reason = RuleReads.KeyFaultReason;

            return false;
        }
        if (transform is null) {
            // A remember is the one arm whose key must name a cell before the transform exists, so its own key is
            // what failed rather than the arm's shape.
            reason = ((effect is VectorRememberEffect)
                ? "a remember's destination key names no cell the catalog interns"
                : $"'{(effect?.Describe ?? "an unbound arm")}' is not a vector transform this arena applies"
            );

            return false;
        }

        var reads = new List<CellAccess>();
        effect.CollectReads(into: reads);
        transform.DisclosureReads = reads.Select(static access => access.RowOrdinal)
            .Concat((effect as VectorEffect)?.DisclosureControls ?? []).Distinct().ToArray();
        return true;
    }

    private static ArenaTransform? MixOf(VectorMixEffect mix, IStateReader? reader, ref bool faulted) {
        var terms = new ArenaMixTerm[mix.Terms.Count];

        for (var term = 0; (term < terms.Length); term++) {
            terms[term] = new ArenaMixTerm(
                Source: SourceOf(
                    faulted: ref faulted,
                    reader: reader,
                    vector: mix.Terms[term].Source
                ),
                Weight: mix.Terms[term].Weight
            );
        }

        return new ArenaTransform.Mix(
            IntoKey: KeyOf(
                faulted: ref faulted,
                key: mix.Key,
                keyFrom: mix.KeyFrom,
                reader: reader
            ),
            IntoRowOrdinal: mix.RowOrdinal,
            Terms: terms
        );
    }
    private static ArenaTransform? RememberOf(VectorRememberEffect remember, StateCatalog catalog, IStateReader? reader, ref bool faulted) {
        var key = KeyOf(
            faulted: ref faulted,
            key: remember.Key,
            keyFrom: remember.KeyFrom,
            reader: reader
        );

        // The destination cell may not exist yet, so the key travels as the name a mint interns rather than as an
        // address.
        return ((reader?.Arena.Keys ?? catalog.Keys).TryGetName(
            key: key,
            name: out var name
        )
            ? new ArenaTransform.Remember(
                From: SourceOf(
                    faulted: ref faulted,
                    reader: reader,
                    vector: remember.Source
                ),
                IntoRowOrdinal: remember.RowOrdinal,
                Key: name,
                UnlessWithinQ16: remember.UnlessWithinQ16
            )
            : null
        );
    }
    // A vector operand is either a literal the compiler folded or a cell the arena reads; a literal never
    // allocates here, because the compiled vector already holds its components.
    private static VectorSource SourceOf(CompiledVector vector, IStateReader? reader, ref bool faulted) => (vector.IsConstant
        ? VectorSource.Literal(components: vector.Constant!.Memory)
        : VectorSource.Cell(
            key: KeyOf(
                faulted: ref faulted,
                key: vector.Key,
                keyFrom: vector.KeyFrom,
                reader: reader
            ),
            rowOrdinal: vector.RowOrdinal
        )
    );
    // A live key whose computation faulted sets faulted, which refuses the whole transform rather than letting it
    // address the invalid key as an absent cell.
    private static CellKey KeyOf(CellKey key, CompiledCellRef? keyFrom, IStateReader? reader, ref bool faulted) {
        if (
            (keyFrom is not { } indirection) ||
            (reader is not { } live)
        ) {
            return ((keyFrom is null)
                ? key
                : default);
        }

        var resolved = RuleReads.ResolveReference(
            reader: live,
            reference: in indirection
        );

        faulted |= (resolved.Case == FaultCase.Faulted);
        return (resolved.TryNamed(value: out var resolvedKey)
            ? resolvedKey
            : default);
    }
}
