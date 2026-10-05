using System.Buffers.Binary;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed class SdfIndirectBrickTableLawTests {
    [Fact]
    public void TheDirectoryOrdersExactKeysWithoutMovingSlotsAndRebuildsAfterEviction() {
        const int Capacity = 8;

        Assert.Equal(5120, SdfIndirectBrickTable.ByteLength(capacity: 256));
        Assert.Equal(10240, SdfIndirectBrickTable.ByteLength(capacity: 512));
        var table = new byte[SdfIndirectBrickTable.ByteLength(capacity: Capacity)];

        for (var slot = 0; (slot < Capacity); slot++) { Store(slot: slot, state: -1, x: 0, y: 0, z: 0); }
        // Independent literal order: level, then Z, Y, X. Classified bits are not part of a key.
        Store(slot: 6, state: 0, x: -2, y: -3, z: -4);
        Store(slot: 1, state: SdfIndirectLayout.BrickClassified, x: -1, y: -3, z: -4);
        Store(slot: 5, state: 0, x: -99, y: -2, z: -4);
        Store(slot: 2, state: 0, x: -99, y: -99, z: -3);
        Store(slot: 4, state: 1, x: -2, y: -3, z: -4);
        var records = table.AsSpan(length: (Capacity * 16), start: 0).ToArray();

        SdfIndirectBrickTable.Index(capacity: Capacity, table: table);
        Assert.Equal(records, table.AsSpan(length: (Capacity * 16), start: 0).ToArray());
        Assert.Equal(new[] { 6, 1, 5, 2, 4, -1, -1, -1 }, Directory());

        Store(slot: 6, state: -1, x: 0, y: 0, z: 0);
        Store(slot: 0, state: 0, x: 9, y: 8, z: -5);
        SdfIndirectBrickTable.Index(capacity: Capacity, table: table);
        Assert.Equal(new[] { 0, 1, 5, 2, 4, -1, -1, -1 }, Directory());
        for (var slot = 0; (slot < Capacity); slot++) { Store(slot: slot, state: -1, x: 0, y: 0, z: 0); }
        SdfIndirectBrickTable.Index(capacity: Capacity, table: table);
        Assert.All(Directory(), slot => Assert.Equal(actual: slot, expected: -1));

        void Store(int slot, int x, int y, int z, int state) {
            var row = table.AsSpan(length: 16, start: (slot * 16));

            BinaryPrimitives.WriteInt32LittleEndian(destination: row, value: x);
            BinaryPrimitives.WriteInt32LittleEndian(destination: row[4..], value: y);
            BinaryPrimitives.WriteInt32LittleEndian(destination: row[8..], value: z);
            BinaryPrimitives.WriteInt32LittleEndian(destination: row[12..], value: state);
        }
        int[] Directory() => Enumerable.Range(count: Capacity, start: 0).Select(selector: index =>
            BinaryPrimitives.ReadInt32LittleEndian(source: table.AsSpan(start: ((Capacity * 16) + (index * 4))))).ToArray();
    }
}
