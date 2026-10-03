using Puck.Hosting;

namespace Puck.World.Client;

public sealed partial class WorldStateMirror {
    private readonly DeliveredClock m_deliveredClock;

    /// <summary>Fires after an install or completed tick delivery has refreshed its registered samples, before
    /// presentation interpolation. Subscribers release their registration when their presentation owner retires.</summary>
    public event Action? Delivered;

    /// <summary>Gets whether the latest installation completes a snapshot at an already installed tick, rather
    /// than seeking or replacing the document. Consumers resample its completed values without resetting identity.</summary>
    public bool LastInstallCompletedDelivery { get; private set; }

    private bool TryScalarNumber(int slot, out float value, bool delivered) {
        if (!delivered) { return TryNumber(slot: slot, value: out value); }
        var found = TryDeliveredValue(slot: slot, value: out var current);

        value = ((float)current);
        return found;
    }
    private bool TryDeliveredValue(int slot, out double value) {
        value = (((slot >= 0) && m_slots[slot].HasNumber) ? m_slots[slot].Current : 0);
        return ((slot >= 0) && m_slots[slot].HasNumber);
    }

    private sealed class DeliveredClock(WorldStateMirror mirror) : IWorldClockSource {
        public PresentedTick Presented => new(Whole: mirror.EngineTick, Fraction: 0);

        public bool TryClockPhase(WorldClock clock, out double phase) => mirror.TryClockPhase(clock: clock, delivered: true, phase: out phase);
    }
}
