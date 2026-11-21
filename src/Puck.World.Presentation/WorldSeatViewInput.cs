using System.Numerics;
using Puck.World.Client;

namespace Puck.World;

/// <summary>Pointer adapter for the view state owned by each occupied seat: pointer motion turns the seat camera while
/// the seat holds <c>player.orbit</c> or <c>player.steer</c>, counted from the press that starts the drag to the release
/// that ends it (<see cref="WorldDragMotion"/>). The document framing a seat is the one its claim routes it to
/// (<see cref="WorldSeatAuthorityRouter.DefinitionOf"/>), never an authority's.</summary>
public sealed class WorldSeatViewInput : IWorldPointerConsumer {
    private readonly WorldClient m_client;
    private readonly WorldDragMotion m_drag;
    private readonly PlayerRoster m_roster;
    private readonly WorldSeatAuthorityRouter m_seatRouter;

    /// <summary>Initializes a new instance of the <see cref="WorldSeatViewInput"/> class.</summary>
    /// <param name="client">The client whose delivered definition frames a seat with no claim.</param>
    /// <param name="roster">The roster whose seats' views turn.</param>
    /// <param name="pointer">The pointer whose drags turn them.</param>
    /// <param name="seatRouter">The router whose claims name the document framing each seat.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldSeatViewInput(WorldClient client, PlayerRoster roster, WorldPointer pointer, WorldSeatAuthorityRouter seatRouter) {
        ArgumentNullException.ThrowIfNull(argument: client);
        ArgumentNullException.ThrowIfNull(argument: roster);
        ArgumentNullException.ThrowIfNull(argument: pointer);
        ArgumentNullException.ThrowIfNull(argument: seatRouter);
        m_client = client;
        m_roster = roster;
        m_seatRouter = seatRouter;
        m_drag = new WorldDragMotion(
            pointer: pointer,
            steering: IsSteering,
            turn: Turn
        );
        seatRouter.RouteChanged += OnLocationChanged;
    }

    private void OnLocationChanged(int slot) {
        m_roster.Seat(slot: slot)?.View.Reclamp(views: m_seatRouter.DefinitionOf(home: m_client.Definition, slot: slot).Views);
    }
    private void Turn(int slot, Vector2 motion) {
        if (m_roster.Seat(slot: slot) is not { } seat) {
            return;
        }

        var definition = m_seatRouter.DefinitionOf(home: m_client.Definition, slot: slot);
        var preference = Preference(
            definition: definition,
            slot: slot
        );

        seat.View.Nudge(
            input: new Vector2(
                x: motion.X,
                y: -motion.Y
            ),
            yawScale: preference.YawSensitivity,
            pitchScale: preference.PitchSensitivity,
            preference: preference,
            views: definition.Views
        );
    }

    /// <summary>Gets the seat camera drag's motion gate, through which the drag commands apply their edges.</summary>
    public WorldDragMotion Drag => m_drag;
    /// <summary>Whether pointer motion steers the seat camera this frame — <c>player.orbit</c> or <c>player.steer</c> held.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <returns><see langword="true"/> while the seat orbits or steers by pointer.</returns>
    public bool IsSteering(int slot) => ((m_roster.Seat(slot: slot) is { } seat) && (seat.Orbiting || seat.PointerSteering));
    /// <inheritdoc/>
    public void OnPointer(int slot) => m_drag.OnPointer(slot: slot);
    /// <summary>Returns the look feel a seat turns with: its identity's, else the document's player defaults.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="definition">The document framing the seat.</param>
    /// <returns>The seat's camera feel.</returns>
    public WorldSeatCameraFeel Preference(int slot, WorldDefinition definition) =>
        (m_roster.Seat(slot: slot)?.Profile?.SeatLook ?? definition.PlayerDefaults.SeatLook);
}
