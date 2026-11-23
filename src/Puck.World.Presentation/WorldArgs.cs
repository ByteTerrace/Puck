using Puck.Commands;

namespace Puck.World;

/// <summary>
/// Shared player-index parsing for the world verbs: the trailing (or positional) integer index the drive-a-player and
/// roster-management verbs constrain to <c>[min, max]</c>. A local index-in-range convenience over
/// <see cref="WireArgs"/>. The trailing <c>instance:&lt;name&gt;</c> token grammar is
/// <see cref="Protocol.WorldInstanceToken"/>.
/// </summary>
public static class WorldArgs {
    /// <summary>Parses an integer index token at <paramref name="at"/> constrained to <c>[min, max]</c>, straight from
    /// its span. When <paramref name="fallback"/> is non-<see langword="null"/> the token is optional: an absent token
    /// yields the fallback (and <see langword="true"/>); when it is <see langword="null"/> an absent token fails. A
    /// present token that does not parse or falls outside the range always fails.</summary>
    /// <param name="args">The wire arguments.</param>
    /// <param name="at">The token position the index is expected at.</param>
    /// <param name="min">The inclusive lower bound.</param>
    /// <param name="max">The inclusive upper bound.</param>
    /// <param name="fallback">The default when the token is absent, or <see langword="null"/> to require it.</param>
    /// <param name="value">The parsed (or fallback) value; <c>0</c> on failure.</param>
    /// <param name="count">The tokens the verb reads as positional values, when a trailing option token (such as
    /// <c>body.engage</c>'s <c>capture:on</c>) has been read off the end, or <see langword="null"/> for every token.</param>
    /// <returns>Whether a valid index (or the fallback) was resolved.</returns>
    public static bool TryParseIndex(in WireArgs args, int at, int min, int max, int? fallback, out int value, int? count = null) {
        if ((count ?? args.Count) <= at) {
            value = (fallback ?? 0);

            return fallback.HasValue;
        }

        return (
            args.TryInt(
            index: at,
            value: out value
        ) &&
            (value >= min) &&
            (value <= max)
        );
    }
}
