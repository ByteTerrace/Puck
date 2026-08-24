using System.Text;

namespace Puck.World.Protocol;

/// <summary>The bit-index algebra every closed-vocabulary <c>ulong</c>-lane mask type (a channel reach/declared/
/// held/consent mask, <see cref="DocumentWriteMask"/>) wraps: test/set by ordinal. The distinct wrapper TYPES stay —
/// each names what its bits mean and which other masks it composes with — this is only their shared arithmetic.
/// <see cref="MutationKindMask"/> does not share this shape: its vocabulary outgrew 64 bits and widened, so it
/// stays on its own lane.</summary>
internal static class ClosedBitset {
    /// <summary>Determines whether <paramref name="ordinal"/>'s bit is set.</summary>
    public static bool Contains(ulong bits, int ordinal) => ((bits & (1UL << ordinal)) != 0UL);
    /// <summary>Returns <paramref name="bits"/> with <paramref name="ordinal"/>'s bit additionally set.</summary>
    public static ulong With(ulong bits, int ordinal) => (bits | (1UL << ordinal));
}
/// <summary>The name-based describe/parse half of <see cref="ClosedBitset"/>, for a mask whose ordinals are a small
/// closed <typeparamref name="TEnum"/> vocabulary rather than a bare channel/register index.</summary>
internal static class ClosedBitset<TEnum> where TEnum : struct, Enum {
    /// <summary>Describes the set bits by member NAME, comma-separated, in declaration order.</summary>
    /// <param name="bits">The raw bit lane.</param>
    /// <param name="emptyToken">The token to return when no bit is set.</param>
    public static string Describe(ulong bits, string emptyToken) {
        var builder = new StringBuilder();

        foreach (var member in Enum.GetValues<TEnum>()) {
            if (!ClosedBitset.Contains(
                bits: bits,
                ordinal: Convert.ToInt32(value: member)
            )) {
                continue;
            }

            _ = builder.Append(value: ((builder.Length == 0)
                ? string.Empty
                : ",")).Append(value: member.ToString());
        }

        return ((builder.Length == 0)
            ? emptyToken
            : builder.ToString()
        );
    }
    /// <summary>Parses a comma-separated member-name list into a bit lane. An unknown name refuses (naming it).</summary>
    /// <param name="text">The comma-separated member names.</param>
    /// <param name="bits">The parsed bit lane, on success.</param>
    /// <param name="unknown">The first unrecognized name, on failure.</param>
    /// <returns><see langword="true"/> when every name resolved to a defined member.</returns>
    public static bool TryParse(string? text, out ulong bits, out string unknown) {
        bits = 0UL;
        unknown = string.Empty;

        if (string.IsNullOrEmpty(value: text)) {
            return false;
        }

        foreach (var name in text.Split(
            options: StringSplitOptions.None,
            separator: ','
        )) {
            if (
                !Enum.TryParse<TEnum>(
                ignoreCase: true,
                result: out var member,
                value: name
            ) ||
                !Enum.IsDefined(value: member)
            ) {
                unknown = name;

                return false;
            }

            bits = ClosedBitset.With(
                bits: bits,
                ordinal: Convert.ToInt32(value: member)
            );
        }

        return true;
    }
}
