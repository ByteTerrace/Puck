using Puck.Overlays;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the binding bar's modifier-indicator reservation to the document contract's single ceiling: the
/// overlay capacity crosses <see cref="WorldBindingBarCapacity.MaxModifiers"/> from <c>Puck.World.Schema</c> to
/// <c>Puck.Overlays</c>, and the lease table reserves one binding-bar element per modifier per seat from that number —
/// not a private constant of the writer's own.</summary>
public sealed class ModifierReservationDerivationLawTests {
    /// <summary>The Schema-derived overlay capacity carries the document contract's own modifier ceiling, so the
    /// feed's modifier array, the validator's boot-time count, and this reservation all size from one number.</summary>
    [Fact]
    public void OverlayCapacityCarriesTheSchemaModifierCeiling() =>
        Assert.Equal(expected: WorldBindingBarCapacity.MaxModifiers, actual: WorldOverlayCapacity.FromSchema().BindingBarMaxModifiers);

    /// <summary>The binding-bar element reservation moves with the ceiling: raising the modifier count by one grows
    /// the reservation by exactly one element per seat — proving the reservation derives from the threaded constant
    /// rather than a fixed writer-side cap.</summary>
    [Fact]
    public void BindingBarReservationScalesOneElementPerModifierPerSeat() {
        var capacity = WorldOverlayCapacity.FromSchema();
        var baseElements = ReservedBindingBarElements(capacity: capacity);
        var plusOneElements = ReservedBindingBarElements(capacity: (capacity with { BindingBarMaxModifiers = (capacity.BindingBarMaxModifiers + 1) }));

        Assert.Equal(expected: capacity.Seats, actual: (plusOneElements - baseElements));
    }

    private static int ReservedBindingBarElements(OverlayCapacity capacity) =>
        new OverlayChannelLeases(capacity: capacity).ReservationOf(channel: OverlayChannel.BindingBar).Elements;
}
