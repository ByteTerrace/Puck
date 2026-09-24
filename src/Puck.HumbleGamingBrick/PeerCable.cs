namespace Puck.HumbleGamingBrick;

/// <summary>
/// The symmetric wiring both point-to-point media share — the serial cable between two <see cref="SerialComponent"/>s
/// and the infrared path between two <see cref="InfraredPort"/>s: each end's peer reference is set to the other, after
/// refusing a port linked to itself and a port that already has a peer. The peer references are host wiring and are
/// never serialized; the session that owns a connected pair is the only caller.
/// </summary>
internal static class PeerCable {
    /// <summary>Wires two ports as each other's peer.</summary>
    /// <typeparam name="TPeer">The peer seam the medium's ports expose to each other.</typeparam>
    /// <param name="first">The first port.</param>
    /// <param name="second">The second port.</param>
    /// <param name="firstPeer">The first port's peer field.</param>
    /// <param name="secondPeer">The second port's peer field.</param>
    /// <param name="port">The port named as a sentence subject, such as "A serial port", for the refusals.</param>
    /// <exception cref="ArgumentException"><paramref name="first"/> and <paramref name="second"/> are the same
    /// port.</exception>
    /// <exception cref="InvalidOperationException">Either port already has a peer.</exception>
    public static void Connect<TPeer>(TPeer first, TPeer second, ref TPeer? firstPeer, ref TPeer? secondPeer, string port) where TPeer : class {
        if (ReferenceEquals(
            objA: first,
            objB: second
        )) {
            throw new ArgumentException(
                message: $"{port} cannot be linked to itself.",
                paramName: nameof(second)
            );
        }

        if (
            (firstPeer is not null) ||
            (secondPeer is not null)
        ) {
            throw new InvalidOperationException(message: $"{port} is already linked; disconnect its session first.");
        }

        firstPeer = second;
        secondPeer = first;
    }
}
