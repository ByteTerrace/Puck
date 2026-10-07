using Puck.Maths;

namespace Puck.World.Server;

/// <summary>The accumulated arrival turn (<see cref="WorldFrameIsometry.AccumulateTurn"/>) of the traveler each local
/// seat follows, as of the last time the seat's view turned for it. A crossing this host publishes turns the seat's view
/// itself and holds the arrived traveler's turn here. A route an authority elsewhere describes later, after the traveler
/// went on through doors of its own, turns the view by the turn between the held turn and the route's
/// (<see cref="WorldFrameIsometry.TurnBetween"/>), however many arrivals lie between.</summary>
/// <param name="seatCount">The number of local seats.</param>
public sealed class WorldRoutedSeatTurns(int seatCount) {
    private readonly Lock m_gate = new();
    private readonly FixedQ4816[] m_turns = new FixedQ4816[seatCount];

    /// <summary>Holds the accumulated arrival turn of the traveler a seat now follows, without turning its view.</summary>
    /// <param name="slot">The local seat.</param>
    /// <param name="travelTurn">The traveler's accumulated arrival turn.</param>
    public void Hold(int slot, FixedQ4816 travelTurn) {
        lock (m_gate) {
            m_turns[slot] = travelTurn;
        }
    }
    /// <summary>Returns the turn a seat's view takes for a route that describes its traveler at
    /// <paramref name="travelTurn"/>, and holds that turn.</summary>
    /// <param name="slot">The local seat.</param>
    /// <param name="travelTurn">The route's accumulated arrival turn.</param>
    /// <returns>The turn the seat's view takes, zero when the traveler made no turned arrival since.</returns>
    public FixedQ4816 Follow(int slot, FixedQ4816 travelTurn) {
        lock (m_gate) {
            var turn = WorldFrameIsometry.TurnBetween(
                after: travelTurn,
                before: m_turns[slot]
            );

            m_turns[slot] = travelTurn;

            return turn;
        }
    }
}
