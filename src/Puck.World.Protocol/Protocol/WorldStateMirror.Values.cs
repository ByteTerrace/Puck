using System.Numerics;

namespace Puck.World.Client;

public sealed partial class WorldStateMirror {
    /// <summary>Gets the shared resolver at the mirror's presented tick. It can read only registered mirror slots and
    /// the manifest's presentation clocks, never the world's simulation document.</summary>
    public WorldValueResolver Values => new(Manifest.Timeline, Presented, this);

    /// <inheritdoc/>
    public bool TryScalar(in StateBinding binding, out double value) => TryValue(slot: SlotOf(binding: in binding, conversion: WorldStateConversion.Number), value: out value);
    /// <inheritdoc/>
    public bool TryColor(in StateBinding binding, out Vector4 value) => TryColor(slot: SlotOf(binding: in binding, conversion: WorldStateConversion.Color), value: out value);
}
