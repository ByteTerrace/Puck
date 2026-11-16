using System.Runtime.InteropServices;
using Puck.Maths;
using Puck.Networking;

namespace Puck.World.Server;

/// <summary>A continuation section's fixed tables: a table of a fixed count is written without a length, because its
/// length is no part of the state. A table whose length the kit or the step decides is written behind its count, by
/// the checkpoint codec's own counted writers.</summary>
internal static class WorldSectionWire {
    // A fixed table writes exactly count elements: the values given, then zeros. A table a body does not carry (a
    // seat with no relative channel has no remainders) is written as the zeros it stands for.
    private static ReadOnlySpan<T> Fixed<T>(ReadOnlySpan<T> values, int count, out int padding) {
        if (values.Length > count) {
            throw new InvalidOperationException(message: $"a fixed section table of {count} holds {values.Length} values.");
        }
        padding = (count - values.Length);
        return values;
    }

    /// <summary>Writes a fixed table of 64-bit raws.</summary>
    internal static void WriteFixed(WireWriter writer, ReadOnlySpan<long> values, int count) {
        writer.WriteInt64s(values: Fixed(count: count, padding: out var padding, values: values));
        for (var index = 0; (index < padding); index++) { writer.WriteInt64(value: 0L); }
    }
    /// <summary>Writes a fixed table of unsigned 64-bit values.</summary>
    internal static void WriteFixed(WireWriter writer, ReadOnlySpan<ulong> values, int count) =>
        WriteFixed(count: count, values: MemoryMarshal.Cast<ulong, long>(span: values), writer: writer);
    /// <summary>Writes a fixed table of fixed-point values.</summary>
    internal static void WriteFixed(WireWriter writer, ReadOnlySpan<FixedQ4816> values, int count) =>
        WriteFixed(count: count, values: MemoryMarshal.Cast<FixedQ4816, long>(span: values), writer: writer);
    /// <summary>Writes a fixed table of flags.</summary>
    internal static void WriteFixed(WireWriter writer, ReadOnlySpan<bool> values, int count) {
        writer.WriteBooleans(values: Fixed(count: count, padding: out var padding, values: values));
        for (var index = 0; (index < padding); index++) { writer.WriteBoolean(value: false); }
    }
}
