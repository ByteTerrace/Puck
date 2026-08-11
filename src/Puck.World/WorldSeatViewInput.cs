using System.Numerics;
using Puck.World.Client;

namespace Puck.World;

/// <summary>Stateless pointer adapter for the view state owned by each occupied seat.</summary>
internal sealed class WorldSeatViewInput : IWorldPointerConsumer {
    private readonly WorldInstanceHost m_instances;
    private readonly PlayerRoster m_roster;
    private readonly WorldPointer m_pointer;

    public WorldSeatViewInput(WorldInstanceHost instances, PlayerRoster roster, WorldPointer pointer, WorldSeatInstanceRouter seatRouter) {
        ArgumentNullException.ThrowIfNull(argument: instances);
        ArgumentNullException.ThrowIfNull(argument: roster);
        ArgumentNullException.ThrowIfNull(argument: pointer);
        ArgumentNullException.ThrowIfNull(argument: seatRouter);
        m_instances = instances;
        m_roster = roster;
        m_pointer = pointer;
        seatRouter.LocationChanged += OnLocationChanged;
    }

    public WorldSeatLook Preference(int slot, WorldDefinition definition) =>
        (m_roster.Seat(slot: slot)?.Profile?.SeatLook ?? definition.PlayerDefaults.SeatLook);

    public bool IsSteering(int slot) {
        var definition = m_instances.ResolveRoutedDefinition(slot: slot);
        var arming = Preference(slot: slot, definition: definition).Arming;

        return arming switch {
            WorldSeatLookArming.None => false,
            WorldSeatLookArming.Always => true,
            _ => ((ArmingButtonIndex(arming: arming) is { } button) && m_pointer.IsButtonDown(slot: slot, button: button)),
        };
    }

    public void OnPointer(int slot) {
        var definition = m_instances.ResolveRoutedDefinition(slot: slot);
        var preference = Preference(slot: slot, definition: definition);
        var motion = m_pointer.TakeMotion(slot: slot);

        if ((motion == Vector2.Zero) || !IsSteering(slot: slot)) {
            return;
        }

        m_roster.Seat(slot: slot)?.View.Nudge(
            input: new Vector2(x: motion.X, y: -motion.Y),
            yawScale: preference.YawSensitivity,
            pitchScale: preference.PitchSensitivity,
            preference: preference,
            control: definition.Views.SeatControl
        );
    }

    internal static int? ArmingButtonIndex(WorldSeatLookArming arming) => arming switch {
        WorldSeatLookArming.LeftButton => 0,
        WorldSeatLookArming.RightButton => 1,
        WorldSeatLookArming.MiddleButton => 2,
        _ => null,
    };

    private void OnLocationChanged(int slot) {
        m_roster.Seat(slot: slot)?.View.Reclamp(control: m_instances.ResolveRoutedDefinition(slot: slot).Views.SeatControl);
    }
}
