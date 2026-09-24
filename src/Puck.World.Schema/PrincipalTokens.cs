using System.Globalization;
using Puck.Commands;

namespace Puck.World.Protocol;

/// <summary>The one token grammar for a <see cref="Principal"/> — the inverse of <see cref="Principal.Describe"/>,
/// bounded by this world's seat and population limits. <see cref="Grantee"/> extends it with the tokens only a grant
/// can name.</summary>
/// <remarks>The console's <c>world.grant</c>/<c>world.why</c> verbs, the document's principal-valued members, the
/// schedule, and the peer wire all parse through here, so a principal from any surface canonicalizes through the
/// identical grammar.</remarks>
public static class PrincipalTokens {
    /// <summary>The principal token grammar <see cref="TryParse"/> accepts, for a refusal to interpolate rather than
    /// hand-spell.</summary>
    public const string Grammar = "seat1..seat4|console|world|addon:<name>|peer:<n>:<generation>";

    /// <summary>Determines whether a principal is canonical — one <see cref="TryParse"/> itself produces from the
    /// principal's own <see cref="Principal.Describe"/> label.</summary>
    /// <param name="principal">The principal to check.</param>
    /// <returns><see langword="true"/> when the principal's own label parses back to this exact value.</returns>
    /// <remarks>An assembled value carrying a field its kind never sets, or one outside the grammar's range (an addon
    /// with a non-zero index, a peer with a zero generation, a seat past slot three, an unstamped value), describes to
    /// a label naming a different principal or none at all, and is refused. An ingress applies this to a
    /// caller-supplied value before treating it as an identity, so a non-canonical spelling can never key the grant
    /// table beside the canonical one it aliases.</remarks>
    public static bool IsCanonical(this Principal principal) => (TryParse(
        token: principal.Describe(),
        out var parsed
    ) && (parsed == principal));
    /// <summary>Parses a principal token (<see cref="Grammar"/>), ignoring case.</summary>
    /// <param name="token">The token to parse.</param>
    /// <param name="principal">The parsed principal, on success; <see cref="Principal.Console"/> otherwise.</param>
    /// <returns><see langword="true"/> when the token parsed.</returns>
    public static bool TryParse(ReadOnlySpan<char> token, out Principal principal) {
        principal = Principal.Console;

        if (token.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "console"
        )) {
            return true;
        }

        // The world token parses so a read-back can name it and the grant door can refuse a row for it by name. It
        // never becomes an acting identity: the wire refuses it, and no ingress stamps it.
        if (token.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "world"
        )) {
            principal = Principal.World;

            return true;
        }

        if (
            token.StartsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: "addon:"
        ) &&
            (token.Length > 6)
        ) {
            principal = Principal.Addon(name: token[6..].ToString());

            return true;
        }

        if (
            token.StartsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: "seat"
        ) &&
            int.TryParse(
            s: token[4..],
            style: NumberStyles.Integer,
            provider: CultureInfo.InvariantCulture,
            result: out var seat
        ) &&
            (seat >= 1) &&
            (seat <= WorldBodiesLimits.LocalSeatCount)
        ) {
            // Seat(slot) is right here: a "seatN" token names the seat identity itself, regardless of who currently
            // claims the slot.
            principal = Principal.Seat(slot: (seat - 1));

            return true;
        }

        if (token.StartsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: "peer:"
        )) {
            var remainder = token[5..];
            var separator = remainder.IndexOf(value: ':');

            if (
                (separator <= 0) ||
                !int.TryParse(
                s: remainder[..separator],
                style: NumberStyles.Integer,
                provider: CultureInfo.InvariantCulture,
                result: out var peer
            ) ||
                !WorldBodiesLimits.IsBodyIndex(index: peer) ||
                !int.TryParse(
                s: remainder[(separator + 1)..],
                style: NumberStyles.Integer,
                provider: CultureInfo.InvariantCulture,
                result: out var generation
            ) ||
                (generation <= 0)
            ) {
                return false;
            }

            principal = Principal.Peer(
                generation: generation,
                index: peer
            );

            return true;
        }

        return false;
    }
    /// <summary>Parses a principal token (<see cref="Grammar"/>) and requires it to be the exact spelling
    /// <see cref="Principal.Describe"/> produces.</summary>
    /// <remarks>A case variant or a range-violating index is refused rather than silently normalised. A token accepted
    /// here always names a principal <see cref="IsCanonical"/> accepts too.</remarks>
    /// <param name="token">The token to parse.</param>
    /// <param name="principal">The parsed principal, on success.</param>
    /// <returns><see langword="true"/> when the token parsed and is that principal's own label.</returns>
    public static bool TryParseCanonical(ReadOnlySpan<char> token, out Principal principal) => (TryParse(
        token: token,
        out principal
    ) && token.SequenceEqual(other: principal.Describe()));
}
