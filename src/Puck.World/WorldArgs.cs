using Puck.Commands;
using static Puck.Commands.CommandArgs;

namespace Puck.World;

/// <summary>
/// Shared player-index parsing for the world verbs: the trailing (or positional) integer index the drive-a-player and
/// roster-management verbs constrain to <c>[min, max]</c>. A local index-in-range convenience over
/// <see cref="Puck.Commands.CommandArgs.TryParseInt"/>.
/// </summary>
internal static class WorldArgs {
    /// <summary>Parses an integer index token at <paramref name="at"/> constrained to <c>[min, max]</c>. When
    /// <paramref name="fallback"/> is non-<see langword="null"/> the token is optional — an absent token yields the
    /// fallback (and <see langword="true"/>); when it is <see langword="null"/> the token is required — an absent token
    /// fails. A present token that does not parse or falls outside the range always fails.</summary>
    /// <param name="args">The full argument array.</param>
    /// <param name="at">The index into <paramref name="args"/> the token is expected at.</param>
    /// <param name="min">The inclusive lower bound.</param>
    /// <param name="max">The inclusive upper bound.</param>
    /// <param name="fallback">The default when the token is absent, or <see langword="null"/> to require it.</param>
    /// <param name="value">The parsed (or fallback) value; <c>0</c> on failure.</param>
    /// <returns>Whether a valid index (or the fallback) was resolved.</returns>
    public static bool TryParseIndex(string[] args, int at, int min, int max, int? fallback, out int value) {
        if (args.Length <= at) {
            value = (fallback ?? 0);

            return fallback.HasValue;
        }

        return (TryParseInt(text: args[at], value: out value) && (value >= min) && (value <= max));
    }

    /// <summary>The zero-copy peer of <see cref="TryParseIndex(string[], int, int, int, int?, out int)"/> over a
    /// <see cref="WireArgs"/>, parsing the index token straight from its span. Same optional/required contract as the
    /// array overload.</summary>
    /// <param name="args">The wire arguments.</param>
    /// <param name="at">The token position the index is expected at.</param>
    /// <param name="min">The inclusive lower bound.</param>
    /// <param name="max">The inclusive upper bound.</param>
    /// <param name="fallback">The default when the token is absent, or <see langword="null"/> to require it.</param>
    /// <param name="value">The parsed (or fallback) value; <c>0</c> on failure.</param>
    /// <returns>Whether a valid index (or the fallback) was resolved.</returns>
    public static bool TryParseIndex(in WireArgs args, int at, int min, int max, int? fallback, out int value) {
        if (args.Count <= at) {
            value = (fallback ?? 0);

            return fallback.HasValue;
        }

        return (args.TryInt(index: at, value: out value) && (value >= min) && (value <= max));
    }
}
