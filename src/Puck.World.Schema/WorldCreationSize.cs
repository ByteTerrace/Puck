using Puck.Maths;

namespace Puck.World;

/// <summary>The deterministic creation-size reading, shared by simulation and presentation.</summary>
public static class WorldCreationSize {
    /// <summary>Maps a numeric state sample to the creation's closed unit scale; absent values hide it.</summary>
    public static FixedQ4816 Of(CellValue value) => (!value.HasValue ? FixedQ4816.Zero : value.Kind switch {
        CellKind.Fixed => FixedQ4816.Clamp(FixedQ4816.FromRawBits(value: value.Raw), FixedQ4816.Zero, FixedQ4816.One),
        CellKind.Int or CellKind.Bool => ((value.Raw > 0) ? FixedQ4816.One : FixedQ4816.Zero),
        _ => FixedQ4816.Zero,
    });
    /// <summary>Reads an authored size binding at the authority's tick, without presentation easing.</summary>
    public static FixedQ4816 Read(WorldDefinition definition, string? reference, ulong tick, ulong engineTick, int bodyIndex = -1) {
        if (reference is null) { return FixedQ4816.One; }
        return ((StateBinding.TryParse(binding: out var binding, token: reference) && StateBinding.TryResolveBodyKey(binding.Key, bodyIndex, out var key) &&
            WorldStateReader.TryReadValue(definition, binding.Row, key, tick, engineTick, out _, out var value))
            ? Of(value: value) : FixedQ4816.Zero);
    }
}
