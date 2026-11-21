namespace Puck.World.Client;

/// <summary>
/// The body each local seat's presentation reads for: the body a <c>$body</c> key names when a row authored for a seat
/// (a haptic row, a key light row, a seat framing) reads state through that seat's <see cref="WorldStateLease"/>. It is
/// the body the seat's authority route names when that differs from the seat's own slot, else the body its bindings
/// are routed to, else the body it perceives through (its own, or one it possesses).
/// </summary>
/// <remarks>Presentation only: it reads what routing and perception already decided and decides nothing.</remarks>
public sealed class WorldSeatBodies {
    private readonly WorldPerceptionAnchor? m_perception;
    private readonly WorldSeatBindings m_seatBindings;
    private readonly WorldSeatAuthorityRouter m_seatRouter;

    /// <summary>Initializes a new instance of the <see cref="WorldSeatBodies"/> class.</summary>
    /// <param name="seatBindings">Each seat's routed entity.</param>
    /// <param name="seatRouter">Each seat's authority route.</param>
    /// <param name="perception">The body each seat perceives through, or <see langword="null"/> for a host with no
    /// possession, where a seat perceives through its own slot.</param>
    /// <exception cref="ArgumentNullException"><paramref name="seatBindings"/> or <paramref name="seatRouter"/> is
    /// <see langword="null"/>.</exception>
    public WorldSeatBodies(WorldSeatBindings seatBindings, WorldSeatAuthorityRouter seatRouter, WorldPerceptionAnchor? perception = null) {
        ArgumentNullException.ThrowIfNull(argument: seatBindings);
        ArgumentNullException.ThrowIfNull(argument: seatRouter);

        m_seatBindings = seatBindings;
        m_seatRouter = seatRouter;
        m_perception = perception;
    }

    /// <summary>Returns the body a seat's <c>$body</c> key names.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <returns>The 0-based body index.</returns>
    public int Of(int slot) {
        if (
            (m_seatRouter.TryRoute(slot: slot) is { } route) &&
            (route.Entity.Index != slot)
        ) {
            return route.Entity.Index;
        }

        var routed = m_seatBindings.RoutedEntityIndex(slot: slot);

        if (routed != slot) {
            return routed;
        }

        return (m_perception?.PerceivedBody(slot: slot) ?? slot);
    }
}
