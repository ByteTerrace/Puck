namespace Puck.State;

/// <summary>
/// The vector transforms as operations over a <see cref="StateArena"/>: <c>copy</c>, <c>mix</c>, <c>mean</c>,
/// <c>nearest</c>, and <c>remember</c>.
/// </summary>
/// <remarks>
/// <para>Each entry point takes pre-resolved operands — row ordinals and interned cell keys, never authored
/// strings — reads vector columns through <see cref="VectorColumn"/>, and answers a
/// <see cref="VectorTransformRefusal"/> carrying the catalogued code and the text naming what refused.</para>
/// <para>A transform opens one journal scope before its first write and rewinds it on any later refusal, so a
/// refused transform leaves the arena exactly as it found it; the scope nests inside whatever scope the caller
/// already opened.</para>
/// <para>Every vector a transform writes is admitted through <see cref="StateVector.TryCreate"/> first, so
/// nothing reaches a column that the document boundary would have refused. The arithmetic is
/// <see cref="VectorTransforms"/> over <c>Puck.Maths.SignedByteVectorFunctions</c>; this class addresses rows,
/// walks members, and journals writes.</para>
/// <para>The arena lays out a vector row by its dimension count alone, so a dimension disagreement refuses here
/// as <see cref="RuleRefusal.VectorSpaceMismatch"/> while the model and revision halves of a space's identity stay
/// a compile-time and document-load check.</para>
/// </remarks>
public static partial class ArenaVectorTransforms {
    private static bool Admits(StateArena arena, int whereRowOrdinal, CellKey key) => (
        (whereRowOrdinal < 0) ||
        (
            arena.TryRead(
            key: key,
            rowOrdinal: whereRowOrdinal,
            value: out var value
        ) &&
            value.HasValue &&
            (value.Kind == CellKind.Bool) &&
            value.AsBool
        )
    );
    private static VectorTransformRefusal Refused(Enum code, string reason) => new(
        Code: code,
        Reason: reason
    );
    // The one write door of every transform: the produced components decide through the same admission the
    // document boundary uses before the column ever sees them.
    private static bool TryAdmitWrite(in VectorColumn into, CellKey key, ReadOnlySpan<sbyte> components, out VectorTransformRefusal refusal) {
        if (!StateVector.TryCreate(
            components: components,
            error: out var error,
            vector: out _
        )) {
            refusal = Refused(
                code: RuleEffectRefusal.MutationRejected,
                reason: $"row '{into.RowName()}' cell '{into.KeyName(key: key)}' {error}"
            );

            return false;
        }
        if (!into.TryWrite(
            components: components,
            key: key,
            reason: out var reason
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
    private static bool TryOpenFilter(StateArena arena, int whereRowOrdinal, out VectorTransformRefusal refusal) {
        if (whereRowOrdinal < 0) {
            refusal = default;

            return true;
        }
        if (((uint)whereRowOrdinal) >= ((uint)arena.Layout.RowCount)) {
            refusal = Refused(
                code: RuleRefusal.StateRowUnknown,
                reason: $"row ordinal {whereRowOrdinal} names no row of this arena"
            );

            return false;
        }

        ref readonly var layout = ref arena.Layout[whereRowOrdinal];

        if ((layout.Kind != CellKind.Bool) || (layout.Shape != RowShape.Keyed)) {
            refusal = Refused(
                code: RuleRefusal.VectorFilterShape,
                reason: $"where row '{arena.Catalog.Descriptors[whereRowOrdinal].Name}' must be a keyed Bool table"
            );

            return false;
        }

        refusal = default;

        return true;
    }
    private static bool TryResolveSource(StateArena arena, in VectorSource source, int dimensions, string role, out ReadOnlyMemory<sbyte> components, out VectorTransformRefusal refusal) {
        components = default;

        if (source.IsLiteral) {
            components = source.Components;
        } else if (source.IsCell) {
            var address = source.Address;

            if (!VectorColumn.TryOpen(
                arena: arena,
                column: out var column,
                refusal: out refusal,
                rowOrdinal: address.RowOrdinal
            )) {
                return false;
            }
            if (!column.TryReadMemory(
                components: out components,
                key: address.Key
            )) {
                refusal = Refused(
                    code: RuleRefusal.StateCellUnaddressable,
                    reason: $"{role} cell '{column.RowName()}[{column.KeyName(key: address.Key)}]' holds no vector"
                );

                return false;
            }
        } else {
            refusal = Refused(
                code: RuleRefusal.VectorOperandNotVector,
                reason: $"{role} names no vector"
            );

            return false;
        }

        if (components.Length != dimensions) {
            refusal = Refused(
                code: RuleRefusal.VectorSpaceMismatch,
                reason: $"{role} carries {components.Length} components where {dimensions} are laid out"
            );

            return false;
        }

        refusal = default;

        return true;
    }
}
