namespace Puck.State;

/// <summary>A <c>copy</c>: one vector written into a cell unchanged.</summary>
/// <param name="IntoRowOrdinal">The destination vector row's catalog ordinal.</param>
/// <param name="IntoKey">The destination cell key, interned by the arena's catalog.</param>
/// <param name="From">Where the vector to write comes from.</param>
public readonly record struct VectorCopyRequest(int IntoRowOrdinal, CellKey IntoKey, VectorSource From);
public static partial class ArenaVectorTransforms {
    /// <summary>Writes one vector into a cell with its components unchanged.</summary>
    /// <param name="arena">The arena holding the rows.</param>
    /// <param name="request">The pre-resolved operands.</param>
    /// <param name="refusal">Why the transform refused, or the default on success.</param>
    /// <returns><see langword="true"/> when the vector was written.</returns>
    /// <remarks>A copy is not a one-term mix: a mix normalizes its sum, and a copy carries the source's own
    /// components byte for byte.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> is <see langword="null"/>.</exception>
    public static bool TryCopy(StateArena arena, in VectorCopyRequest request, out VectorTransformRefusal refusal) {
        ArgumentNullException.ThrowIfNull(argument: arena);

        if (!VectorColumn.TryOpen(
            arena: arena,
            column: out var into,
            refusal: out refusal,
            rowOrdinal: request.IntoRowOrdinal
        )) {
            return false;
        }
        if (!TryResolveSource(
            arena: arena,
            components: out var components,
            dimensions: into.Dimensions,
            refusal: out refusal,
            role: "copied vector",
            source: request.From
        )) {
            return false;
        }

        var mark = arena.BeginScope();

        if (!TryAdmitWrite(
            components: components.Span,
            into: into,
            key: request.IntoKey,
            refusal: out refusal
        )) {
            arena.Rewind(mark: mark);

            return false;
        }

        arena.Commit(mark: mark);

        return true;
    }
}
