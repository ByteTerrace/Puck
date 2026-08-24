using Puck.Commands;
using Puck.Overlays;

namespace Puck.World.Client;

/// <summary>The one bridge from the document contract's declared ceilings to the overlay's host-supplied
/// <see cref="OverlayCapacity"/>: every host that composes a <see cref="UnifiedOverlayNode"/> over a world calls
/// <see cref="FromSchema"/> rather than restating a number, so the seat count and the HUD ceilings cross from
/// <c>Puck.World.Schema</c> to <c>Puck.Overlays</c> as constructor data.</summary>
public static class WorldOverlayCapacity {
    /// <summary>Creates the overlay capacity from the document contract's constants: <see cref="WorldPopulationLimits.LocalSeatCount"/>
    /// seats, the <see cref="WorldHudCapacity"/> ceilings at both scopes, <see cref="WorldMarkerCapacity.MaxChipsPerSeat"/>,
    /// and the binding substrate's <see cref="BindingWheelDefinition"/> ring/sector bounds.</summary>
    /// <returns>The capacity the composition root hands the overlay.</returns>
    public static OverlayCapacity FromSchema() =>
        new(
            BindingBarMaxBanks: WorldBindingBarCapacity.MaxBanks,
            BindingBarMaxModifiers: WorldBindingBarCapacity.MaxModifiers,
            BindingBarMaxSlotsPerBank: WorldBindingBarCapacity.MaxSlots,
            HudElementsPerPanel: WorldHudCapacity.MaxElementsPerPanel,
            HudElementsPerSeatPanel: WorldHudCapacity.MaxElementsPerSeatPanel,
            HudPanels: WorldHudCapacity.MaxWorldPanels,
            HudSeatPanelsPerSeat: WorldHudCapacity.MaxSeatPanels,
            MarkerMaxChipsPerSeat: WorldMarkerCapacity.MaxChipsPerSeat,
            Seats: WorldPopulationLimits.LocalSeatCount,
            WheelMaxRings: BindingWheelDefinition.MaxRings,
            WheelMaxSectorsPerRing: BindingWheelDefinition.MaxSectorsPerRing
        );
}
