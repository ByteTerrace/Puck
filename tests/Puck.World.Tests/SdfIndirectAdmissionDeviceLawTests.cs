using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The real shared admission counter stays bounded under two viewport dispatches, independently of field evaluation.</summary>
[Collection(DebugLayerCollection.Name)]
[SupportedOSPlatform("windows10.0.15063")]
[Trait("Category", "Gpu")]
public sealed class SdfIndirectAdmissionDeviceLawTests {
    [Fact]
    public void VulkanBoundsRealAdmissionAcrossTwoViewsWithoutFieldEvaluation() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfIndirectAdmissionDeviceLawTests));

        Verify(device.Services, ".spv");
    }
    [Fact]
    public void DirectXBoundsRealAdmissionAcrossTwoViewsWithoutFieldEvaluation() {
        using var output = new StringWriter();

        using (var device = DirectXTestDevices.Debug(output: output)) { Verify(device.Services, ".dxil"); }
        Assert.DoesNotContain("[d3d12-debug]", output.ToString(), StringComparison.Ordinal);
    }

    private static void Verify(GpuDeviceServices services, string extension) {
        const uint Limit = 32_768;
        const uint Requests = 4_147_200; // Two 960 by 1080 views, each requesting both Medium levels.
        var rows = new List<Vector4>();
        var groups = new List<(uint X, uint Y, uint Z)>();

        Seed(Limit - 1u);
        Request(1u);
        var boundary = Snapshot();
        Seed(Limit);
        Request(2u);
        var full = Snapshot();
        Seed(0u);
        Request(1u);
        var reset = Snapshot();
        Seed(0u);
        Request(2u, 120u, 135u);
        Request(2u, 120u, 135u);
        var viewports = Snapshot();
        Seed(uint.MaxValue);
        Request(1u);
        var overflow = Snapshot();
        var result = Run(services, extension, Limit, rows, groups);

        Assert.Equal(new AdmissionResult(Limit, 1, 63, 64, 0, 0, 64), Read(result, rows.Count, boundary));
        Assert.Equal(new AdmissionResult(Limit, 0, 128, 128, 0, 0, 128), Read(result, rows.Count, full));
        // With 64 contenders and 64 attempts, each loser can observe at most the other 63 successful increments.
        Assert.Equal(new AdmissionResult(64, 64, 0, 64, 0, 0, 64), Read(result, rows.Count, reset));
        var pressure = Read(result, rows.Count, viewports);

        Assert.InRange(actual: pressure.Accepted, low: 1u, high: Limit);
        Assert.Equal(pressure.Accepted, pressure.Counter);
        Assert.Equal(Requests, pressure.Requests);
        Assert.Equal(Requests, pressure.Accepted + pressure.Deferred);
        Assert.Equal(0u, pressure.Errors);
        Assert.Equal(0u, pressure.FieldQueries);
        Assert.Equal(Requests, pressure.CounterReads);
        Assert.Equal(new AdmissionResult(uint.MaxValue, 0, 64, 64, 0, 0, 64), Read(result, rows.Count, overflow));
        TestContext.Current.TestOutputHelper?.WriteLine(message: $"admission {extension}: {pressure}");

        rows.Clear();
        groups.Clear();
        Seed(17u);
        Request(2u);
        var frozen = Snapshot();
        result = Run(services, extension, 0u, rows, groups);
        Assert.Equal(new AdmissionResult(17, 0, 128, 128, 0, 0, 0), Read(result, rows.Count, frozen));

        void Seed(uint value) => Add(new Vector4(0, value & 65535u, value >> 16, 0), (1, 1, 1));
        void Request(uint count, uint x = 1, uint y = 1) => Add(new Vector4(1, 0, 0, count), (x, y, 1));
        int Snapshot() {
            var index = rows.Count;

            Add(new Vector4(2, 0, 0, 0), (1, 1, 1));
            return index;
        }
        void Add(Vector4 row, (uint X, uint Y, uint Z) extent) { rows.Add(item: row); groups.Add(item: extent); }
    }

    private static Vector4[] Run(GpuDeviceServices services, string extension, uint limit, List<Vector4> rows,
        List<(uint X, uint Y, uint Z)> groups) {
        var parameters = SdfWorldInterfaces.WorldParameters;
        var values = new byte[parameters.SizeBytes];

        BinaryPrimitives.WriteUInt32LittleEndian(destination: values.AsSpan(start: ((int)parameters.BlockOffsetOf(member: SdfWorldPackage.IndirectTier))), value: ((uint)SdfIndirectTier.Medium));
        BinaryPrimitives.WriteUInt32LittleEndian(destination: values.AsSpan(start: ((int)parameters.BlockOffsetOf(member: SdfWorldPackage.IndirectReceiverProofs))), value: limit);
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var program = builder.Sphere(radius: 1, material: material).Build();

        // The program and standard descriptors satisfy the existing harness; this kernel never evaluates them.
        // Only its counter address is rebased to word zero, so this law does not qualify cache layout or VM cost.
        return SdfIndirectDeviceProbe.Run(services, extension, "sdf-indirect-admission-proof.comp", 4,
            Enumerable.Repeat(element: program, count: rows.Count).ToArray(), rows.ToArray(),
            passValues: values, worldParameters: true, dispatchGroups: groups);
    }

    private static AdmissionResult Read(Vector4[] values, int width, int index) {
        var counts = new uint[7];

        for (var count = 0; count < counts.Length; count++) {
            var packed = values[((count / 2) * width) + index];
            var low = ((count & 1) == 0 ? packed.X : packed.Z);
            var high = ((count & 1) == 0 ? packed.Y : packed.W);

            Assert.InRange(actual: low, low: 0f, high: 65535f);
            Assert.InRange(actual: high, low: 0f, high: 65535f);
            Assert.Equal(MathF.Truncate(x: low), low);
            Assert.Equal(MathF.Truncate(x: high), high);
            counts[count] = ((uint)low) | (((uint)high) << 16);
        }
        return new(counts[0], counts[1], counts[2], counts[3], counts[4], counts[5], counts[6]);
    }

    private readonly record struct AdmissionResult(uint Counter, uint Accepted, uint Deferred, uint Requests,
        uint Errors, uint FieldQueries, uint CounterReads);
}
