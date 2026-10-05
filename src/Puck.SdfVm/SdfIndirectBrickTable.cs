using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;

namespace Puck.SdfVm;

/// <summary>The existing brick region: stable pool records followed by a sorted directory of their slot indices.</summary>
public static class SdfIndirectBrickTable {
    /// <summary>Gets the bytes for four record words and one directory word per pool slot.</summary>
    /// <param name="capacity">The tier's brick capacity.</param>
    /// <returns>The complete region length, including unused directory entries.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The capacity is negative.</exception>
    /// <exception cref="OverflowException">The region length exceeds an array's index range.</exception>
    public static int ByteLength(int capacity) {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        return checked(((capacity * 5) * sizeof(int)));
    }
    /// <summary>Indexes the placed pool records without moving them. Unused directory entries are minus one and
    /// follow every live key; the shader compares the referenced full key in <see cref="IrradianceBrickKey"/> order.</summary>
    /// <param name="table">The complete region; each absent pool record has minus one in its fourth word.</param>
    /// <param name="capacity">The number of pool records, followed by the same number of directory words.</param>
    /// <exception cref="ArgumentNullException">The table is null.</exception>
    /// <exception cref="ArgumentException">The table does not have the complete region length.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The capacity is negative.</exception>
    /// <exception cref="OverflowException">The region length exceeds an array's index range.</exception>
    public static void Index(byte[] table, int capacity) {
        ArgumentNullException.ThrowIfNull(table);
        if (table.Length != ByteLength(capacity: capacity)) { throw new ArgumentException(message: "The brick table must include its complete slot directory.", paramName: nameof(table)); }
        var directory = MemoryMarshal.Cast<byte, int>(span: table.AsSpan(start: (capacity * 16)));

        directory.Fill(value: -1);
        var count = 0;

        for (var slot = 0; (slot < capacity); slot++) {
            if (BinaryPrimitives.ReadInt32LittleEndian(source: table.AsSpan(start: ((slot * 16) + 12))) != -1) { directory[count++] = slot; }
        }
        directory[..count].Sort(comparer: new SlotOrder(table: table));
        // The temporary sorting span uses native integers; the published directory uses the region's little-endian words.
        for (var index = 0; (index < count); index++) {
            BinaryPrimitives.WriteInt32LittleEndian(destination: table.AsSpan(start: ((capacity * 16) + (index * 4))), value: directory[index]);
        }
    }

    private readonly struct SlotOrder(byte[] table) : IComparer<int> {
        public int Compare(int x, int y) => Key(slot: x).CompareTo(other: Key(slot: y));

        private IrradianceBrickKey Key(int slot) {
            var row = table.AsSpan(length: 16, start: (slot * 16));

            return new(Level: BinaryPrimitives.ReadInt32LittleEndian(source: row[12..]) & SdfIndirectLayout.BrickLevelMask,
                X: BinaryPrimitives.ReadInt32LittleEndian(source: row), Y: BinaryPrimitives.ReadInt32LittleEndian(source: row[4..]),
                Z: BinaryPrimitives.ReadInt32LittleEndian(source: row[8..]));
        }
    }
}
