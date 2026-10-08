using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The production brick lookup preserves exact pool identities with logarithmic counted reads.</summary>
[SupportedOSPlatform("windows10.0.15063")]
[Trait("Category", "Gpu")]
public sealed class SdfIndirectBrickDeviceLawTests {
    [Fact]
    public void VulkanFindsExactBrickKeysWithinTheDirectoryReadBound() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfIndirectBrickDeviceLawTests));

        Verify(device.Services, ".spv");
    }
    [Fact]
    public void DirectXFindsExactBrickKeysWithinTheDirectoryReadBound() {
        using var output = new StringWriter();

        using (var device = DirectXTestDevices.Debug(output: output)) { Verify(device.Services, ".dxil"); }
        Assert.DoesNotContain("[d3d12-debug]", output.ToString(), StringComparison.Ordinal);
    }

    private static void Verify(GpuDeviceServices services, string extension) {
        foreach (var tier in new[] { SdfIndirectTier.Medium, SdfIndirectTier.High }) {
            var layout = new SdfIndirectLayout(tier: tier);
            var capacity = layout.BrickCapacity;
            var table = new byte[SdfIndirectBrickTable.ByteLength(capacity: capacity)];
            var slots = new Dictionary<IrradianceBrickKey, int>();

            for (var ordinal = 0; (ordinal < capacity); ordinal++) {
                var key = ((ordinal < 2) ? new IrradianceBrickKey(Level: ordinal, X: -1, Y: -2, Z: -1000)
                    : new IrradianceBrickKey(Level: (ordinal % layout.Levels.Count), X: ((ordinal % 7) - 3), Y: ((ordinal % 11) - 5), Z: (ordinal - 256)));
                var slot = ((ordinal * 73) % capacity);

                slots.Add(key: key, value: slot);
                var row = table.AsSpan(length: 16, start: (slot * 16));

                BinaryPrimitives.WriteInt32LittleEndian(destination: row, value: key.X);
                BinaryPrimitives.WriteInt32LittleEndian(destination: row[4..], value: key.Y);
                BinaryPrimitives.WriteInt32LittleEndian(destination: row[8..], value: key.Z);
                BinaryPrimitives.WriteInt32LittleEndian(destination: row[12..], value: key.Level | (((ordinal % 2) == 0) ? SdfIndirectLayout.BrickClassified : 0));
            }
            var queries = slots.Keys.Where(predicate: (_, index) => ((index < 4) || (index == 17) || (index == (capacity / 2)) || (index == (capacity - 1)))).ToList();

            queries.Add(item: new IrradianceBrickKey(Level: 0, X: -999, Y: 3, Z: -1000));
            queries.Add(item: new IrradianceBrickKey(Level: 0, X: 999, Y: 3, Z: 1000));
            queries.Add(item: new IrradianceBrickKey(Level: 3, X: -1, Y: -2, Z: -1000));
            var builder = new SdfProgramBuilder();
            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
            var program = builder.Sphere(1, material).Build();
            var parameters = new byte[16];

            BinaryPrimitives.WriteUInt32LittleEndian(destination: parameters, value: ((uint)tier));

            // Full, evicted and reset tables use the same queries; missing keys must never alias a retained slot.
            for (var state = 0; (state < 3); state++) {
                if (state > 0) {
                    foreach (var (key, slot) in slots.ToArray()) {
                        if ((state == 2) || ((slot % 2) == 0)) {
                            BinaryPrimitives.WriteInt32LittleEndian(destination: table.AsSpan(start: ((slot * 16) + 12)), value: -1);
                            slots.Remove(key: key);
                        }
                    }
                }
                SdfIndirectBrickTable.Index(capacity: capacity, table: table);
                var rows = new Vector4[((table.Length / 16) + queries.Count)];

                table.AsSpan().CopyTo(destination: MemoryMarshal.AsBytes(span: rows.AsSpan()));
                for (var index = 0; (index < queries.Count); index++) {
                    var key = queries[index];
                    var row = MemoryMarshal.AsBytes(span: rows.AsSpan(((table.Length / 16) + index), 1));

                    BinaryPrimitives.WriteInt32LittleEndian(destination: row, value: ((key.X * 4) + 3));
                    BinaryPrimitives.WriteInt32LittleEndian(destination: row[4..], value: ((key.Y * 4) + 2));
                    BinaryPrimitives.WriteInt32LittleEndian(destination: row[8..], value: ((key.Z * 4) + 1));
                    BinaryPrimitives.WriteInt32LittleEndian(destination: row[12..], value: key.Level);
                }
                var results = SdfIndirectDeviceProbe.Run(services, extension, "sdf-indirect-bricks-proof.comp", 1,
                    Enumerable.Repeat(program, queries.Count).ToArray(), rows, passValues: parameters);

                for (var index = 0; (index < queries.Count); index++) {
                    var expected = (slots.TryGetValue(key: queries[index], value: out var slot) ? ((slot * 64) + 27) : -1);

                    Assert.Equal(((float)expected), results[index].X);
                    // A level the tier does not keep is refused before the directory is read; every other key searches it.
                    if (queries[index].Level >= layout.Levels.Count) {
                        Assert.Equal(0f, results[index].Y);
                    } else {
                        Assert.InRange(results[index].Y, 1f, ((tier == SdfIndirectTier.Medium) ? 18f : 20f));
                    }
                }
            }
        }
    }
}
