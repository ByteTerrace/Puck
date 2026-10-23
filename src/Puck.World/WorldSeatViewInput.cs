using System.Numerics;
using Puck.World.Client;

namespace Puck.World;

/// <summary>Pointer adapter for the view state owned by each occupied seat: pointer motion turns the seat camera while
/// the seat holds <c>player.orbit</c> or <c>player.steer</c>, counted from the press that starts the drag to the release
/// that ends it (<see cref="WorldDragMotion"/>).</summary>
internal sealed class WorldSeatViewInput : IWorldPointerConsumer {
    private readonly WorldDragMotion m_drag;
    private readonly WorldInstanceHost m_instances;
    private readonly PlayerRoster m_roster;

    public WorldSeatViewInput(WorldInstanceHost instances, PlayerRoster roster, WorldPointer pointer, WorldSeatAuthorityRouter seatRouter) {
        ArgumentNullException.ThrowIfNull(argument: instances);
        ArgumentNullException.ThrowIfNull(argument: roster);
        ArgumentNullException.ThrowIfNull(argument: pointer);
        ArgumentNullException.ThrowIfNull(argument: seatRouter);
        m_instances = instances;
        m_roster = roster;
        m_drag = new WorldDragMotion(
            pointer: pointer,
            steering: IsSteering,
            turn: Turn
        );
        seatRouter.RouteChanged += OnLocationChanged;
    }

    private void OnLocationChanged(int slot) {
        m_roster.Seat(slot: slot)?.View.Reclamp(views: m_instances.ResolveRoutedDefinition(slot: slot).Views);
    }
    private void Turn(int slot, Vector2 motion) {
        if (m_roster.Seat(slot: slot) is not { } seat) {
            return;
        }

        var definition = m_instances.ResolveRoutedDefinition(slot: slot);
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
    public bool IsSteering(int slot) => ((m_roster.Seat(slot: slot) is { } seat) && (seat.Orbiting || seat.PointerSteering));
    public void OnPointer(int slot) => m_drag.OnPointer(slot: slot);
    public WorldSeatCameraFeel Preference(int slot, WorldDefinition definition) =>
        (m_roster.Seat(slot: slot)?.Profile?.SeatLook ?? definition.PlayerDefaults.SeatLook);
}
