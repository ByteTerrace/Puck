using System.Collections.Concurrent;
using System.Globalization;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// Finds what an overlay's body-keyed state read (<c>state.&lt;row&gt;.$body</c>, <see cref="StateBinding.BodyKey"/>)
/// reads for one local seat: the cell keyed by the body the seat controls, in the world the seat stands in, through the
/// mirror the seat registered that read on (<see cref="WorldPresentationManifest.SeatBindings"/>). Each token is parsed
/// once and each body's resolution kept, so a steady frame allocates nothing; the frame and a console read-back may ask
/// at once.
/// </summary>
/// <param name="seatBindings">The seat bindings that say which world and body each seat reads.</param>
public sealed class WorldSeatStateReads(WorldSeatBindings seatBindings) {
    private readonly ConcurrentDictionary<string, StateBinding?> m_tokens = new(comparer: StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Token, int Body), StateBinding> m_resolved = new();

    /// <summary>Finds the mirror slot a body-keyed token reads for a seat.</summary>
    /// <param name="token">The authored token.</param>
    /// <param name="seat">The 0-based local seat the read is for, or negative for none.</param>
    /// <param name="mirror">The mirror the seat reads through, or <see langword="null"/> when the token is not keyed by
    /// the body, no seat is named, or the seat has not been routed yet.</param>
    /// <param name="slot">The slot in <paramref name="mirror"/>, or -1 when nothing registered the seat's read.</param>
    /// <returns><see langword="true"/> when <paramref name="token"/> is keyed by the body, whether or not it resolved;
    /// <see langword="false"/> for any other token, which its reader reads its own way.</returns>
    public bool TryFind(string token, int seat, out WorldStateMirror? mirror, out int slot) {
        mirror = null;
        slot = -1;

        if (m_tokens.GetOrAdd(key: token, valueFactory: static token => (StateBinding.IsBodyKeyed(token: token) ? StateBinding.Parse(token: token) : null)) is not { } binding) {
            return false;
        }

        if (seat < 0) {
            return true;
        }

        seatBindings.GetRoutedState(
            definition: out _,
            slot: seat,
            state: out var routed
        );

        var body = seatBindings.RoutedEntityIndex(slot: seat);

        if (
            (routed is null) ||
            (body < 0)
        ) {
            return true;
        }

        var resolved = m_resolved.GetOrAdd(
            factoryArgument: binding,
            key: (token, body),
            valueFactory: static (key, binding) => (binding with { Key = key.Body.ToString(provider: CultureInfo.InvariantCulture) })
        );

        mirror = routed;
        slot = routed.SlotOf(
            binding: in resolved,
            conversion: WorldStateConversion.Number
        );

        return true;
    }
}
