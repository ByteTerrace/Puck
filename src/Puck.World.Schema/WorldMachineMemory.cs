using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.World;

/// <summary>The direction of a named hardware binding.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldMachineMemoryDirection>))]
public enum WorldMachineMemoryDirection : byte {
    /// <summary>Observe machine hardware and mirror accepted values into world state.</summary>
    Read,
    /// <summary>Send world state into machine hardware.</summary>
    Write,
}

/// <summary>An ordered scalar binding between a named device and an Int world-state cell.</summary>
/// <param name="Name">The binding identity within its machine.</param>
/// <param name="Direction">Whether values enter or leave world state.</param>
/// <param name="Space">The provider's hardware address space.</param>
/// <param name="Format">The signed or unsigned scalar format: i8/u8, i16/u16, i32/u32, or i64/u64.</param>
/// <param name="Row">The world-state row.</param>
/// <param name="Address">An unsigned raw address; mutually exclusive with Symbol.</param>
/// <param name="Symbol">An exported content symbol; mutually exclusive with Address.</param>
/// <param name="Key">The key of a table-shaped state row, or null for a slot.</param>
/// <param name="Access">inspect for reads; patch or bus for writes.</param>
/// <param name="Update">onChange or everyTick. Repeating a hardware-visible write requires explicit everyTick authoring.</param>
/// <param name="Conversion">checked by default; truncate explicitly admits narrowing and bit reinterpretation.</param>
public sealed record WorldMachineMemory(string Name, WorldMachineMemoryDirection Direction, string Space,
    string Format, string Row,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ulong? Address = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Symbol = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null,
    string Access = "inspect", string Update = "onChange", string Conversion = "checked") {
    /// <summary>Gets the scalar's byte width, or zero for an unsupported format.</summary>
    [JsonIgnore]
    public int Width => Format switch {
        "i8" or "u8" => 1, "i16" or "u16" => 2, "i32" or "u32" => 4, "i64" or "u64" => 8, _ => 0,
    };

    /// <summary>Converts a world value into hardware bits under the authored narrowing policy.</summary>
    /// <param name="value">The world's signed Int64 value.</param>
    /// <returns>The scalar bit pattern.</returns>
    /// <exception cref="OverflowException">Checked conversion cannot represent the value.</exception>
    /// <exception cref="InvalidOperationException">The format is unsupported.</exception>
    public ulong Encode(long value) {
        if (Conversion == "truncate") {
            return Width switch {
                1 => unchecked((byte)value), 2 => unchecked((ushort)value),
                4 => unchecked((uint)value), 8 => unchecked((ulong)value),
                _ => throw new InvalidOperationException($"Unsupported format '{Format}'."),
            };
        }
        return Format switch {
            "u8" => checked((byte)value), "u16" => checked((ushort)value),
            "u32" => checked((uint)value), "u64" => checked((ulong)value),
            "i8" => unchecked((byte)checked((sbyte)value)), "i16" => unchecked((ushort)checked((short)value)),
            "i32" => unchecked((uint)checked((int)value)), "i64" => unchecked((ulong)value),
            _ => throw new InvalidOperationException($"Unsupported format '{Format}'."),
        };
    }

    /// <summary>Interprets hardware bits as a world-state Int64 value.</summary>
    /// <param name="value">The provider's scalar bit pattern.</param>
    /// <returns>The decoded value.</returns>
    /// <exception cref="OverflowException">An unsigned 64-bit value cannot fit the checked world value.</exception>
    /// <exception cref="InvalidOperationException">The format is unsupported.</exception>
    public long Decode(ulong value) => Format switch {
        "u8" => checked((byte)value), "u16" => checked((ushort)value), "u32" => checked((uint)value),
        "u64" => Conversion == "truncate" ? unchecked((long)value) : checked((long)value),
        "i8" => unchecked((sbyte)checked((byte)value)), "i16" => unchecked((short)checked((ushort)value)),
        "i32" => unchecked((int)checked((uint)value)), "i64" => unchecked((long)value),
        _ => throw new InvalidOperationException($"Unsupported format '{Format}'."),
    };
}
