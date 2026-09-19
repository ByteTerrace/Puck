using Puck.Maths;

namespace Puck.State;

/// <summary>A <c>remember</c>: one vector stored into a table under a key, unless the table already holds a near
/// duplicate.</summary>
/// <param name="IntoRowOrdinal">The destination vector table's catalog ordinal.</param>
/// <param name="Key">The key to store under; minted when the table does not hold it yet.</param>
/// <param name="From">Where the vector to store comes from.</param>
/// <param name="UnlessWithinQ16">The Q48.16 cosine a stored vector must reach for the write to be skipped, in
/// <c>[0, 1]</c>. The cell under <paramref name="Key"/> is never its own near duplicate.</param>
public readonly record struct VectorRememberRequest(int IntoRowOrdinal, CellName Key, VectorSource From, long UnlessWithinQ16);
public static partial class ArenaVectorTransforms {
    /// <summary>Stores one vector into a table under a key, unless another cell of the table is within the cosine
    /// threshold.</summary>
    /// <param name="arena">The arena holding the rows.</param>
    /// <param name="request">The pre-resolved operands.</param>
    /// <param name="refusal">Why the transform refused, or the default on success.</param>
    /// <returns><see langword="true"/> when the vector was stored or deliberately skipped.</returns>
    /// <remarks>A skipped near duplicate is a success that writes nothing, not a refusal: the table already
    /// remembers the direction the caller offered.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> is <see langword="null"/>.</exception>
    public static bool TryRemember(StateArena arena, in VectorRememberRequest request, out VectorTransformRefusal refusal) {
        ArgumentNullException.ThrowIfNull(argument: arena);

        if (!VectorColumn.TryOpen(
            arena: arena,
            column: out var into,
            refusal: out refusal,
            rowOrdinal: request.IntoRowOrdinal
        )) {
            return false;
        }
        if ((request.UnlessWithinQ16 < 0L) || (request.UnlessWithinQ16 > FixedQ4816.One.Value)) {
            refusal = Refused(
                code: RuleRefusal.VectorRememberShape,
                reason: $"row '{into.RowName()}' takes a cosine threshold within [0, 1], not {FixedQ4816.FromRawBits(value: request.UnlessWithinQ16)}"
            );

            return false;
        }
        if (!TryResolveSource(
            arena: arena,
            components: out var components,
            dimensions: into.Dimensions,
            refusal: out refusal,
            role: "remembered vector",
            source: request.From
        )) {
            return false;
        }

        var count = into.Count;

        using var existingLease = arena.Scratch.Rent<NearestCandidate>(length: count);

        var existing = existingLease.Span;
        var gathered = 0;

        for (var position = 0; (position < count); position++) {
            if (
                into.TryKeyAt(
                key: out var key,
                position: position
            ) &&
                into.TryReadMemory(
                components: out var stored,
                key: key
            )
            ) {
                existing[gathered++] = new NearestCandidate(
                    Components: stored,
                    Key: arena.Catalog.Keys[key: key]
                );
            }
        }

        if (!VectorTransforms.TryRemember(
            existingCells: existing[..gathered],
            key: request.Key,
            matchingKey: out _,
            unlessWithinQ16: request.UnlessWithinQ16,
            vector: components.Span
        )) {
            refusal = default;

            return true;
        }

        var mark = arena.BeginScope();

        if (!TryStore(
            components: components.Span,
            into: into,
            name: request.Key,
            refusal: out refusal
        )) {
            arena.Rewind(mark: mark);

            return false;
        }

        arena.Commit(mark: mark);

        return true;
    }

    // A key the table already holds is rewritten in place; one it does not is minted, which is where the row's
    // own capacity and eviction policy decide.
    private static bool TryStore(in VectorColumn into, CellName name, ReadOnlySpan<sbyte> components, out VectorTransformRefusal refusal) {
        var arena = into.Arena;

        if (
            arena.Catalog.Keys.TryResolve(
            key: out var key,
            name: name
        ) &&
            arena.TryCellSlot(
            key: key,
            rowOrdinal: into.RowOrdinal,
            slot: out _
        )
        ) {
            return TryAdmitWrite(
                components: components,
                into: into,
                key: key,
                refusal: out refusal
            );
        }
        if (into.Shape is not (RowShape.Keyed or RowShape.Ordered)) {
            refusal = Refused(
                code: RuleRefusal.VectorRememberShape,
                reason: $"row '{into.RowName()}' is a {into.Shape} row and holds no cell '{name.Value}' to remember into"
            );

            return false;
        }
        if (!StateVector.TryCreate(
            components: components,
            error: out var error,
            vector: out _
        )) {
            refusal = Refused(
                code: RuleEffectRefusal.MutationRejected,
                reason: $"row '{into.RowName()}' cell '{name.Value}' {error}"
            );

            return false;
        }
        if (!arena.TryMint(
            key: out _,
            name: name,
            reason: out var reason,
            rowOrdinal: into.RowOrdinal,
            value: CellValue.Vector(components: components.ToArray())
        )) {
            refusal = Refused(
                code: RuleEffectRefusal.MutationRejected,
                reason: reason
            );

            return false;
        }

        refusal = default;

        return true;
    }
}
