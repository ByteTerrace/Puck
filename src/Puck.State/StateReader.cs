using Puck.Maths;

namespace Puck.State;

public enum StateReduceOp : byte {
    None,

    Max,

    Min,

    Sum,

    Count,
    ArrangementRank,
}
public static partial class StateReader {
    public static long ArrangementRank(ReadOnlySpan<int> domainOrdinals) {
        if (domainOrdinals.Length > MaxArrangementTokens) {
            return -1L;
        }

        Span<int> relative = stackalloc int[domainOrdinals.Length];

        RelativeOrder(
            ordinals: domainOrdinals,
            relative: relative
        );
        return unchecked((long)Combinatorics.PermutationRank(permutation: relative));
    }
    public static int DomainOrdinals(StateArena arena, int rowOrdinal, int domainOrdinal, Span<int> ordinals) {
        ArgumentNullException.ThrowIfNull(argument: arena);

        if (domainOrdinal < 0) {
            return -1;
        }

        var count = arena.CellCount(rowOrdinal: rowOrdinal);

        if (
            (count > ordinals.Length) ||
            (count > MaxArrangementTokens)
        ) {
            return -1;
        }

        var domainCount = arena.CellCount(rowOrdinal: domainOrdinal);

        for (var position = 0; (position < count); position++) {
            if (!arena.TryKeyAt(
                key: out var key,
                position: position,
                rowOrdinal: rowOrdinal
            )) {
                return -1;
            }

            var found = -1;

            for (var candidate = 0; (candidate < domainCount); candidate++) {
                if (
                    arena.TryKeyAt(
                    key: out var domainKey,
                    position: candidate,
                    rowOrdinal: domainOrdinal
                ) &&
                    (domainKey == key)
                ) {
                    found = candidate;

                    break;
                }
            }
            if (found < 0) {
                return -1;
            }

            ordinals[position] = found;
        }

        return count;
    }
    public static long DynamicsFixedToRowRaw(StateRow row, FixedQ4816 value) => ((row.Kind == CellKind.Fixed)
        ? value.Value
        : (FixedQ4816.Round(value: value).Value >> FixedQ4816.FractionBitCount)
    );
    public static long DynamicsFixedToTraitRaw(FixedQ4816 value) => value.Value;
    public static FixedQ4816 DynamicsRowRawToFixed(StateRow row, long raw) => ((row.Kind == CellKind.Fixed)
        ? FixedQ4816.FromRawBits(value: raw)
        : LiftSaturating(raw: raw)
    );
    public static FixedQ4816 DynamicsTraitRawToFixed(long raw) => FixedQ4816.FromRawBits(value: raw);
    public static FixedQ4816 LiftSaturating(long raw) => FixedQ4816.FromInteger(value: Math.Clamp(
        max: (long.MaxValue >> FixedQ4816.FractionBitCount),
        min: (long.MinValue >> FixedQ4816.FractionBitCount),
        value: raw
    ));
    public static void RelativeOrder(ReadOnlySpan<int> ordinals, Span<int> relative) {
        for (var index = 0; (index < ordinals.Length); index++) {
            var rank = 0;

            for (var other = 0; (other < ordinals.Length); other++) {
                if (ordinals[other] < ordinals[index]) {
                    rank++;
                }
            }
            relative[index] = rank;
        }
    }
    public static bool TryParseCandidateIndex(string key, out int index) {
        if (
            int.TryParse(
            s: key,
            style: System.Globalization.NumberStyles.Integer,
            provider: System.Globalization.CultureInfo.InvariantCulture,
            result: out index
        ) &&
            (index >= 0)
        ) {
            return true;
        }

        index = -1;

        return false;
    }
    public const int MaxArrangementTokens = 20;
}
