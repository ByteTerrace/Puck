using System.Numerics;
using Puck.World.Client;

namespace Puck.World;

/// <summary>Stateless pointer adapter for the view state owned by each occupied seat.</summary>
internal sealed class WorldSeatViewInput : IWorldPointerConsumer {
    private readonly WorldInstanceHost m_instances;
    private readonly WorldPointer m_pointer;
    private readonly PlayerRoster m_roster;

    public WorldSeatViewInput(WorldInstanceHost instances, PlayerRoster roster, WorldPointer pointer, WorldSeatAuthorityRouter seatRouter) {
        ArgumentNullException.ThrowIfNull(argument: instances);
        ArgumentNullException.ThrowIfNull(argument: roster);
        ArgumentNullException.ThrowIfNull(argument: pointer);
        ArgumentNullException.ThrowIfNull(argument: seatRouter);
        m_instances = instances;
        m_roster = roster;
        m_pointer = pointer;
        seatRouter.RouteChanged += OnLocationChanged;
    }

    private void OnLocationChanged(int slot) {
        m_roster.Seat(slot: slot)?.View.Reclamp(views: m_instances.ResolveRoutedDefinition(slot: slot).Views);
    }

    /// <summary>Whether pointer motion steers the seat camera this frame — <c>player.orbit</c> or <c>player.steer</c> held.</summary>
    public bool IsSteering(int slot) => ((m_roster.Seat(slot: slot) is { } seat) && (seat.Orbiting || seat.PointerSteering));
    public void OnPointer(int slot) {
        var definition = m_instances.ResolveRoutedDefinition(slot: slot);
        var preference = Preference(
            definition: definition,
            slot: slot
        );
        var motion = m_pointer.TakeMotion(slot: slot);

        if (
            (motion == Vector2.Zero) ||
            !IsSteering(slot: slot)
        ) {
            return;
        }

        m_roster.Seat(slot: slot)?.View.Nudge(
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
    public WorldSeatCameraFeel Preference(int slot, WorldDefinition definition) =>
        (m_roster.Seat(slot: slot)?.Profile?.SeatLook ?? definition.PlayerDefaults.SeatLook);
}
