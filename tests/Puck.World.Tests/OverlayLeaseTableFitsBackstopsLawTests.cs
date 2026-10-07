using Puck.Overlays;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for the Schema-to-presentation capacity seam: the composition root hands <c>Puck.Overlays</c> a
/// capacity derived from the document contract's ceilings, and the lease table it derives must fit the overlay's
/// addressable backstops. Neither assembly can state this alone — Schema never names the overlay, and the overlay
/// never names the game — so the cross-assembly check lives here.</summary>
public sealed class OverlayLeaseTableFitsBackstopsLawTests {
    /// <summary>The construction-time refusal is real: a capacity that over-subscribes a backstop throws by name,
    /// while the Schema-derived one (the control) builds. Proves the law above can fail for the right reason.</summary>
    [Fact]
    public void OverSubscribedCapacityRefusesAtConstructionByName() {
        var control = WorldOverlayCapacity.FromSchema();
        var oversubscribed = (control with { Seats = (control.Seats * 16) });

        var refusal = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new OverlayChannelLeases(capacity: oversubscribed));

        Assert.Contains(
            actualString: refusal.Message,
            expectedSubstring: "OverlayFrameBuilder."
        );
        Assert.NotNull(@object: new OverlayChannelLeases(capacity: control));
    }
    /// <summary>An adversarial host count is multiplied exactly before the backstop check; it cannot wrap the six
    /// per-seat clip reservations negative and masquerade as spare capacity.</summary>
    [Fact]
    public void OversubscribedCapacityArithmeticCannotWrapPastTheBackstop() {
        var adversarial = new OverlayCapacity(
            BindingBarMaxBanks: 0,
            BindingBarMaxModifiers: 0,
            BindingBarMaxSlotsPerBank: 0,
            HudElementsPerPanel: 0,
            HudElementsPerSeatPanel: 0,
            HudPanels: 0,
            HudSeatPanelsPerSeat: 0,
            MarkerMaxChipsPerSeat: 0,
            Seats: (1 << 30),
            WheelMaxRings: 0,
            WheelMaxSectorsPerRing: 0
        );

        var refusal = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new OverlayChannelLeases(capacity: adversarial));

        Assert.Contains(
            actualString: refusal.Message,
            expectedSubstring: "6442450944"
        );
    }
    /// <summary>The Schema-derived capacity builds a lease table without a construction-time refusal, and every
    /// channel's reservation sits at or below its resource's backstop — the property a world boot relies on before
    /// its first frame.</summary>
    [Fact]
    public void SchemaDerivedCapacityBuildsALeaseTableWithinEveryBackstop() {
        var leases = new OverlayChannelLeases(capacity: WorldOverlayCapacity.FromSchema());

        Assert.Equal(
            expected: WorldBodiesLimits.LocalSeatCount,
            actual: leases.MaxSeats
        );
        Assert.True(
            condition: (leases.TotalClips <= OverlayFrameBuilder.MaxClips),
            userMessage: $"clips {leases.TotalClips} exceed the backstop {OverlayFrameBuilder.MaxClips}"
        );
        Assert.True(
            condition: (leases.TotalElements <= OverlayFrameBuilder.MaxElements),
            userMessage: $"elements {leases.TotalElements} exceed the backstop {OverlayFrameBuilder.MaxElements}"
        );
        Assert.True(
            condition: (leases.TotalPanels <= OverlayFrameBuilder.MaxPanels),
            userMessage: $"panels {leases.TotalPanels} exceed the backstop {OverlayFrameBuilder.MaxPanels}"
        );
        Assert.True(
            condition: (leases.TotalTextWords <= leases.TextWordCapacity),
            userMessage: $"text words {leases.TotalTextWords} exceed the backstop {leases.TextWordCapacity}"
        );

        for (var index = 0; (index < OverlayChannelLeases.Count); index++) {
            var channel = ((OverlayChannel)index);
            var reservation = leases.ReservationOf(channel: channel);

            Assert.True(
                condition: (reservation.Clips <= OverlayFrameBuilder.MaxClips),
                userMessage: OverlayChannelLeases.NameOf(channel: channel)
            );
            Assert.True(
                condition: (reservation.Elements <= OverlayFrameBuilder.MaxElements),
                userMessage: OverlayChannelLeases.NameOf(channel: channel)
            );
            Assert.True(
                condition: (reservation.Panels <= OverlayFrameBuilder.MaxPanels),
                userMessage: OverlayChannelLeases.NameOf(channel: channel)
            );
            Assert.True(
                condition: (reservation.TextWords <= leases.TextWordCapacity),
                userMessage: OverlayChannelLeases.NameOf(channel: channel)
            );
        }
    }
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [Theory]
    public void TextCapacityIsTheSmallestPowerOfTwoCoveringEveryDeclaredWriter(int seats) {
        var leases = new OverlayChannelLeases(capacity: WorldOverlayCapacity.FromSchema() with { Seats = seats });
        var sum = Enumerable.Range(count: OverlayChannelLeases.Count, start: 0)
            .Sum(selector: index => leases.ReservationOf(channel: ((OverlayChannel)index)).TextWords);

        Assert.Equal(expected: sum, actual: leases.TotalTextWords);
        Assert.True(condition: (leases.TextWordCapacity >= sum));
        Assert.True(condition: ((leases.TextWordCapacity / 2) < sum));
        Assert.Equal(expected: 0, actual: leases.TextWordCapacity & (leases.TextWordCapacity - 1));
        Assert.Equal(expected: (seats * ((InspectorWriter.MaxLines * InspectorWriter.MaxLineChars) + HistoryRowWriter.MaxLabelChars)),
            actual: leases.ReservationOf(channel: OverlayChannel.Editor).TextWords);
    }

}
