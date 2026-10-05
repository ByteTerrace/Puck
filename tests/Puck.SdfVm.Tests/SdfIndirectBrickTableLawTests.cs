using System.Buffers.Binary;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed class SdfIndirectBrickTableLawTests {
    [Fact]
    public void TheDirectoryOrdersExactKeysWithoutMovingSlotsAndRebuildsAfterEviction() {
        const int Capacity = 8;
        Assert.Equal(5120, SdfIndirectBrickTable.ByteLength(256));
        Assert.Equal(10240, SdfIndirectBrickTable.ByteLength(512));
        var table = new byte[SdfIndirectBrickTable.ByteLength(Capacity)];
        for (var slot = 0; slot < Capacity; slot++) { Store(slot, 0, 0, 0, -1); }
        // Independent literal order: level, then Z, Y, X. Classified bits are not part of a key.
        Store(6, -2, -3, -4, 0);
        Store(1, -1, -3, -4, SdfIndirectLayout.BrickClassified);
        Store(5, -99, -2, -4, 0);
        Store(2, -99, -99, -3, 0);
        Store(4, -2, -3, -4, 1);
        var records = table.AsSpan(0, Capacity * 16).ToArray();
        SdfIndirectBrickTable.Index(table, Capacity);
        Assert.Equal(records, table.AsSpan(0, Capacity * 16).ToArray());
        Assert.Equal(new[] { 6, 1, 5, 2, 4, -1, -1, -1 }, Directory());

        Store(6, 0, 0, 0, -1);
        Store(0, 9, 8, -5, 0);
        SdfIndirectBrickTable.Index(table, Capacity);
        Assert.Equal(new[] { 0, 1, 5, 2, 4, -1, -1, -1 }, Directory());
        for (var slot = 0; slot < Capacity; slot++) { Store(slot, 0, 0, 0, -1); }
        SdfIndirectBrickTable.Index(table, Capacity);
        Assert.All(Directory(), slot => Assert.Equal(-1, slot));

        void Store(int slot, int x, int y, int z, int state) {
            var row = table.AsSpan(slot * 16, 16);
            BinaryPrimitives.WriteInt32LittleEndian(row, x);
            BinaryPrimitives.WriteInt32LittleEndian(row[4..], y);
            BinaryPrimitives.WriteInt32LittleEndian(row[8..], z);
            BinaryPrimitives.WriteInt32LittleEndian(row[12..], state);
        }
        int[] Directory() => Enumerable.Range(0, Capacity).Select(index =>
            BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(Capacity * 16 + index * 4))).ToArray();
    }
}
