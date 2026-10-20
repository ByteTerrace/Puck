using System.Numerics;
using Puck.World.Client;

namespace Puck.World;

/// <summary>Pointer adapter for the view state owned by each occupied seat: pointer motion turns the seat camera while
/// the seat holds <c>player.orbit</c> or <c>player.steer</c>.</summary>
/// <remarks>The router dispatches a drag's press at the next fixed tick, after the window has already reported the
/// motion that came with the press. So motion while no drag holds is kept from the latest pointer-button press for as
/// long as a button stays down, and <see cref="BeginDrag"/> turns the camera by it when the press starts a drag: a
/// flick that starts with the press counts from its first frame. Motion with no button down, or from before the
/// press, is discarded.</remarks>
internal sealed class WorldSeatViewInput : IWorldPointerConsumer {
    // The pointer buttons each seat held at its last event, so a press shows as the button it adds.
    private readonly int[] m_buttons = new int[PlayerRoster.MaxSlots];
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

    private int ButtonsDown(int slot) {
        var buttons = 0;

        for (var button = 0; (button < 3); button++) {
            if (m_pointer.IsButtonDown(
                button: button,
                slot: slot
            )) {
                buttons |= (1 << button);
            }
        }

        return buttons;
    }
    private void OnLocationChanged(int slot) {
        m_roster.Seat(slot: slot)?.View.Reclamp(views: m_instances.ResolveRoutedDefinition(slot: slot).Views);
    }
    // Turns the seat camera by the motion the seat's pointer has carried since it was last taken.
    private void Turn(int slot) {
        var motion = m_pointer.TakeMotion(slot: slot);

        if (
            (motion == Vector2.Zero) ||
            (m_roster.Seat(slot: slot) is not { } seat)
        ) {
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

    /// <summary>Turns the seat camera by the motion kept since the press that started a drag on the seat.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public void BeginDrag(int slot) {
        if (IsSteering(slot: slot)) {
            Turn(slot: slot);
        }
    }
    /// <summary>Whether pointer motion steers the seat camera this frame — <c>player.orbit</c> or <c>player.steer</c> held.</summary>
    public bool IsSteering(int slot) => ((m_roster.Seat(slot: slot) is { } seat) && (seat.Orbiting || seat.PointerSteering));
    public void OnPointer(int slot) {
        if (((uint)slot) >= PlayerRoster.MaxSlots) {
            return;
        }

        var buttons = ButtonsDown(slot: slot);
        var pressed = ((buttons & ~m_buttons[slot]) != 0);

        m_buttons[slot] = buttons;

        if (IsSteering(slot: slot)) {
            Turn(slot: slot);

            return;
        }

        if (
            pressed ||
            (buttons == 0)
        ) {
            _ = m_pointer.TakeMotion(slot: slot);
        }
    }
    public WorldSeatCameraFeel Preference(int slot, WorldDefinition definition) =>
        (m_roster.Seat(slot: slot)?.Profile?.SeatLook ?? definition.PlayerDefaults.SeatLook);
}
