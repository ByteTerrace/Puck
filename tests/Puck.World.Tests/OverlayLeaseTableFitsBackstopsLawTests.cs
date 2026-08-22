using Puck.Overlays;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for the Schema-to-presentation capacity seam: the composition root hands <c>Puck.Overlays</c> a
/// capacity derived from the document contract's ceilings, and the lease table it derives must fit the overlay's
/// addressable backstops. Neither assembly can state this alone — Schema never names the overlay, and the overlay
/// never names the game — so the cross-assembly check lives here.</summary>
public sealed class OverlayLeaseTableFitsBackstopsLawTests {
    /// <summary>The Schema-derived capacity builds a lease table without a construction-time refusal, and every
    /// channel's reservation sits at or below its resource's backstop — the property a world boot relies on before
    /// its first frame.</summary>
    [Fact]
    public void SchemaDerivedCapacityBuildsALeaseTableWithinEveryBackstop() {
        var leases = new OverlayChannelLeases(capacity: WorldOverlayCapacity.FromSchema());

        Assert.Equal(expected: WorldPopulationLimits.LocalSeatCount, actual: leases.MaxSeats);
        Assert.True(condition: (leases.TotalClips <= OverlayFrameBuilder.MaxClips), userMessage: $"clips {leases.TotalClips} exceed the backstop {OverlayFrameBuilder.MaxClips}");
        Assert.True(condition: (leases.TotalElements <= OverlayFrameBuilder.MaxElements), userMessage: $"elements {leases.TotalElements} exceed the backstop {OverlayFrameBuilder.MaxElements}");
        Assert.True(condition: (leases.TotalPanels <= OverlayFrameBuilder.MaxPanels), userMessage: $"panels {leases.TotalPanels} exceed the backstop {OverlayFrameBuilder.MaxPanels}");
        Assert.True(condition: (leases.TotalTextWords <= OverlayFrameBuilder.TextWordCapacity), userMessage: $"text words {leases.TotalTextWords} exceed the backstop {OverlayFrameBuilder.TextWordCapacity}");

        for (var index = 0; (index < OverlayChannelLeases.Count); index++) {
            var channel = ((OverlayChannel)index);
            var reservation = leases.ReservationOf(channel: channel);

            Assert.True(condition: (reservation.Clips <= OverlayFrameBuilder.MaxClips), userMessage: OverlayChannelLeases.NameOf(channel: channel));
            Assert.True(condition: (reservation.Elements <= OverlayFrameBuilder.MaxElements), userMessage: OverlayChannelLeases.NameOf(channel: channel));
            Assert.True(condition: (reservation.Panels <= OverlayFrameBuilder.MaxPanels), userMessage: OverlayChannelLeases.NameOf(channel: channel));
            Assert.True(condition: (reservation.TextWords <= OverlayFrameBuilder.TextWordCapacity), userMessage: OverlayChannelLeases.NameOf(channel: channel));
        }
    }
    /// <summary>The construction-time refusal is real: a capacity that over-subscribes a backstop throws by name,
    /// while the Schema-derived one (the control) builds. Proves the law above can fail for the right reason.</summary>
    [Fact]
    public void OverSubscribedCapacityRefusesAtConstructionByName() {
        var control = WorldOverlayCapacity.FromSchema();
        var oversubscribed = (control with { Seats = (control.Seats * 16) });

        var refusal = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new OverlayChannelLeases(capacity: oversubscribed));

        Assert.Contains(actualString: refusal.Message, expectedSubstring: "OverlayFrameBuilder.");
        Assert.NotNull(@object: new OverlayChannelLeases(capacity: control));
    }
    /// <summary>An adversarial host count is multiplied exactly before the backstop check; it cannot wrap the four
    /// per-seat clip reservations negative and masquerade as spare capacity.</summary>
    [Fact]
    public void OversubscribedCapacityArithmeticCannotWrapPastTheBackstop() {
        var adversarial = new OverlayCapacity(
            Seats: (1 << 30),
            HudPanels: 0,
            HudElementsPerPanel: 0,
            HudSeatPanelsPerSeat: 0,
            HudElementsPerSeatPanel: 0,
            BindingBarMaxBanks: 0,
            BindingBarMaxSlotsPerBank: 0,
            BindingBarMaxModifiers: 0,
            MarkerMaxChipsPerSeat: 0
        );

        var refusal = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new OverlayChannelLeases(capacity: adversarial));

        Assert.Contains(actualString: refusal.Message, expectedSubstring: "4294967296");
    }
}
