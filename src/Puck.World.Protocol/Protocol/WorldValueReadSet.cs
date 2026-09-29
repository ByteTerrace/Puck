using System.Numerics;

namespace Puck.World.Client;

/// <summary>Dependencies of one cached presentation resolve. It records only the slots and clocks the shared resolver
/// actually read; unrelated state and unused clocks cannot invalidate the result. Reuse it between resolves.</summary>
/// <param name="Mirror">The mirror being presented.</param>
public sealed class WorldValueReadSet(WorldStateMirror Mirror) : IWorldValueSource, IWorldValueTrace {
    private readonly List<(int Slot, int Revision)> m_slots = [];
    private readonly List<(WorldClock Clock, double Phase)> m_clocks = [];
    private int m_generation = -1;

    /// <summary>Gets the mirror whose reads are recorded.</summary>
    public WorldStateMirror Mirror { get; } = Mirror;

    /// <summary>Gets the shared resolver that records this resolve's reads.</summary>
    public WorldValueResolver Values => new(Mirror.Manifest.Timeline, Mirror.Presented, this, Trace: this);

    /// <summary>Starts recording a replacement result, retaining the allocated list storage.</summary>
    public void Reset() {
        m_slots.Clear();
        m_clocks.Clear();
        m_generation = Mirror.Generation;
    }

    /// <summary>Gets whether a recorded slot or clock has changed since the result was resolved.</summary>
    public bool Changed {
        get {
            if (m_generation != Mirror.Generation) { return true; }
            for (var index = 0; (index < m_slots.Count); index++) {
                var (slot, revision) = m_slots[index];
                if (Mirror.Changed(slot: slot) != revision) { return true; }
            }
            var values = Mirror.Values;

            for (var index = 0; (index < m_clocks.Count); index++) {
                var (clock, phase) = m_clocks[index];
                if (values.Phase(clock: clock) != phase) { return true; }
            }
            return false;
        }
    }

    /// <inheritdoc/>
    public bool TryScalar(in StateBinding binding, out double value) {
        var slot = Mirror.SlotOf(binding: in binding, conversion: WorldStateConversion.Number);

        Note(slot: slot);
        return Mirror.TryValue(slot: slot, value: out value);
    }
    /// <inheritdoc/>
    public bool TryColor(in StateBinding binding, out Vector4 value) {
        var slot = Mirror.SlotOf(binding: in binding, conversion: WorldStateConversion.Color);

        Note(slot: slot);
        return Mirror.TryColor(slot: slot, value: out value);
    }
    /// <inheritdoc/>
    public void ReadClock(WorldClock clock, double phase) {
        for (var index = 0; (index < m_clocks.Count); index++) {
            if (ReferenceEquals(objA: m_clocks[index].Clock, objB: clock)) { return; }
        }
        m_clocks.Add(item: (clock, phase));
    }

    private void Note(int slot) {
        for (var index = 0; (index < m_slots.Count); index++) {
            if (m_slots[index].Slot == slot) { return; }
        }
        m_slots.Add(item: (slot, Mirror.Changed(slot: slot)));
    }
}
