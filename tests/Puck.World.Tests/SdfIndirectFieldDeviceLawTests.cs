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

/// <summary>Separates full-field VM evaluation, its Views wrapper, and real admission on the same packed geometry.</summary>
[Collection(DebugLayerCollection.Name)]
[SupportedOSPlatform("windows10.0.15063")]
[Trait("Category", "Gpu")]
public sealed class SdfIndirectFieldDeviceLawTests {
    [Fact]
    public void VulkanEvaluatesTheDirectFieldAtReceiverVolume() => Vulkan(1);
    [Fact]
    public void DirectXEvaluatesTheDirectFieldAtReceiverVolume() => DirectX(1);
    [Fact]
    public void VulkanEvaluatesTheWrappedFieldAtReceiverVolume() => Vulkan(2);
    [Fact]
    public void DirectXEvaluatesTheWrappedFieldAtReceiverVolume() => DirectX(2);
    [Fact]
    public void VulkanCombinesRealAdmissionWithFullFieldEvaluation() => Vulkan(3);
    [Fact]
    public void DirectXCombinesRealAdmissionWithFullFieldEvaluation() => DirectX(3);

    private static void Vulkan(uint mode) {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfIndirectFieldDeviceLawTests));

        Verify(device.Services, ".spv", mode);
    }
    private static void DirectX(uint mode) {
        using var output = new StringWriter();

        using (var device = DirectXTestDevices.Debug(output: output)) { Verify(device.Services, ".dxil", mode); }
        Assert.DoesNotContain("[d3d12-debug]", output.ToString(), StringComparison.Ordinal);
    }
    private static void Verify(GpuDeviceServices services, string extension, uint mode) {
        const uint Limit = 32_768;
        const uint Queries = Limit * 40;
        const uint Requests = 4_147_200;
        var rows = new List<Vector4>();
        var groups = new List<(uint X, uint Y, uint Z)>();

        // A separate 256-call direct control measures actual bound-culling work for each input once.
        // It is not counted as part of the subsequent 1,310,720-call workload.
        Add(0, 0, 0, 0);
        Add(1, 32, 1, 1, 4, 1);
        var controlIndex = Snapshot();

        Add(0, 0, 0, 0);
        if (mode == 3) {
            Add(mode, 960, 40, 2, 120, 135);
            Add(mode, 960, 40, 2, 120, 135);
        } else {
            Add(mode, 256, 40, 1, 32, 16);
        }
        var resultIndex = Snapshot();
        var inputs = new List<Vector4> { new(256, rows.Count, 0, 0) };

        inputs.AddRange(rows);
        for (var index = 0; index < 256; index++) {
            var step = index / 4;
            var offset = new Vector3((step & 7) * 0.001f, ((step >> 3) & 7) * 0.001f, (step % 5) * 0.001f);
            var point = offset + ((index % 4) switch {
                0 => new Vector3(-0.4f, 0.008f, -0.3f),
                1 => new Vector3(0.228f, 0.08f, -0.04f),
                2 => new Vector3(0.34f, 0.09f, 0.26f),
                _ => new Vector3(-0.095f, 0.07f, 0.165f),
            });
            var expected = Expected(point);

            Assert.Equal(index % 4, expected.Material);
            inputs.Add(new Vector4(point, expected.Distance));
            inputs.Add(new Vector4(expected.Material, 0, 0, 0));
        }
        var parameters = SdfWorldInterfaces.WorldParameters;
        var values = new byte[parameters.SizeBytes];

        Write(SdfWorldPackage.IndirectTier, (uint)SdfIndirectTier.Medium);
        Write(SdfWorldPackage.IndirectBodies, (uint)SdfIndirectParticipation.Cast);
        Write(SdfWorldPackage.IndirectReceiverProofs, Limit);
        var program = Program();
        // The actual dynamic body is stationary and indirect-casting, with direct-shadow suppression deliberately
        // set. The Views helper must use its own participation policy and restore the outer scope after each call.
        Vector4[] transforms = [new(-0.12f, 0, 0.15f, 1), new(0, 0, 0, 1), Vector4.Zero];
        var result = SdfIndirectDeviceProbe.Run(services, extension, "sdf-indirect-field-proof.comp", 9,
            Enumerable.Repeat(program, rows.Count).ToArray(), inputs.ToArray(), transforms,
            passValues: values, worldParameters: true, dispatchGroups: groups);
        var control = Read(result, rows.Count, controlIndex);
        var actual = Read(result, rows.Count, resultIndex);

        AssertField(control, 256, false);
        Assert.Equal(64u, control.Floor);
        Assert.Equal(64u, control.Wall);
        Assert.Equal(64u, control.Sphere);
        Assert.Equal(64u, control.Body);
        if (mode == 3) {
            Assert.InRange(actual.Accepted, 1u, Limit);
            Assert.Equal(actual.Accepted, actual.Counter);
            Assert.Equal(Requests, actual.Requests);
            Assert.Equal(Requests, actual.Accepted + actual.Deferred);
            Assert.Equal(Requests, actual.Loads);
            AssertField(actual, actual.Accepted * 40, true);
        } else {
            Assert.Equal(0u, actual.Counter);
            Assert.Equal(0u, actual.Requests);
            Assert.Equal(0u, actual.Accepted);
            Assert.Equal(0u, actual.Deferred);
            Assert.Equal(0u, actual.Loads);
            AssertField(actual, Queries, mode == 2);
            Assert.Equal(control.Shapes * (Queries / 256), actual.Shapes);
            Assert.Equal(Queries / 4, actual.Floor);
            Assert.Equal(Queries / 4, actual.Wall);
            Assert.Equal(Queries / 4, actual.Sphere);
            Assert.Equal(Queries / 4, actual.Body);
        }
        TestContext.Current.TestOutputHelper?.WriteLine($"field {extension} mode={mode}: control={control}; workload={actual}");

        void Write(string member, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(values.AsSpan((int)parameters.BlockOffsetOf(member)), value);
        void Add(uint kind, uint width, uint queries, uint requests, uint x = 1, uint y = 1) {
            rows.Add(new Vector4(kind, width, queries, requests));
            groups.Add((x, y, 1));
        }
        int Snapshot() {
            var index = rows.Count;

            Add(4, 0, 0, 0);
            return index;
        }
    }
    private static void AssertField(FieldResult result, uint queries, bool wrapper) {
        Assert.Equal(queries, result.Calls);
        Assert.InRange(result.Shapes, queries, queries * 4);
        Assert.Equal(0u, result.ValueErrors);
        Assert.Equal(0u, result.ScopeErrors);
        Assert.Equal(0u, result.AdmissionErrors);
        Assert.Equal(wrapper ? queries : 0u, result.HelperQueries);
        Assert.Equal(wrapper ? queries : 0u, result.Steps);
        Assert.Equal(0u, result.Reserved);
        Assert.Equal(queries, result.Floor + result.Wall + result.Sphere + result.Body);
    }
    private static SdfProgram Program() {
        var builder = new SdfProgramBuilder();

        for (var material = 0; material < 4; material++) { builder.AddMaterial(new SdfMaterial(Vector3.One)); }
        // Geometry-equivalent to the local indirect-comparison study and body, with distinct material IDs as
        // witnesses. Distant furnace/room placements, lighting, cache, visibility and frame reset are excluded.
        builder.BeginInstance(Vector3.Zero, 1)
            .ResetPoint().Translate(new Vector3(0, -0.025f, 0)).Box(new Vector3(0.6f, 0.025f, 0.6f), 0, 0)
            .ResetPoint().Translate(new Vector3(0.21f, 0.1f, 0)).Box(new Vector3(0.01f, 0.1f, 0.1f), 0, 1)
            .ResetPoint().Translate(new Vector3(0.3f, 0.06f, 0.25f)).Sphere(0.06f, 2).EndInstance()
            .BeginInstanceDynamic(0, new Vector3(0, 0.06f, 0), 0.06f, indirect: SdfIndirectParticipation.Cast)
            .ResetPoint().TransformDynamic(0).Translate(new Vector3(0, 0.06f, 0)).Sphere(0.06f, 3).EndInstance();
        return builder.Build();
    }
    private static (float Distance, int Material) Expected(Vector3 point) {
        // Independent primitive equations, not SdfFieldEvaluator, packed bytecode or the shader's bound tests.
        var distances = new[] {
            Box(point - new Vector3(0, -0.025f, 0), new Vector3(0.6f, 0.025f, 0.6f)),
            Box(point - new Vector3(0.21f, 0.1f, 0), new Vector3(0.01f, 0.1f, 0.1f)),
            (point - new Vector3(0.3f, 0.06f, 0.25f)).Length() - 0.06f,
            (point - new Vector3(-0.12f, 0.06f, 0.15f)).Length() - 0.06f,
        };
        var material = 0;

        for (var index = 1; index < distances.Length; index++) { if (distances[index] < distances[material]) { material = index; } }
        return (distances[material], material);

        static float Box(Vector3 position, Vector3 halfExtents) {
            var outside = Vector3.Abs(position) - halfExtents;

            return Vector3.Max(outside, Vector3.Zero).Length() + MathF.Min(MathF.Max(outside.X, MathF.Max(outside.Y, outside.Z)), 0);
        }
    }
    private static FieldResult Read(Vector4[] values, int width, int index) {
        var counts = new uint[17];

        for (var count = 0; count < counts.Length; count++) {
            var packed = values[(count / 2) * width + index];
            var low = (count & 1) == 0 ? packed.X : packed.Z;
            var high = (count & 1) == 0 ? packed.Y : packed.W;

            Assert.InRange(low, 0f, 65535f);
            Assert.InRange(high, 0f, 65535f);
            Assert.Equal(MathF.Truncate(low), low);
            Assert.Equal(MathF.Truncate(high), high);
            counts[count] = (uint)low | ((uint)high << 16);
        }
        return new(counts[0], counts[1], counts[2], counts[3], counts[4], counts[5], counts[6], counts[7], counts[8],
            counts[9], counts[10], counts[11], counts[12], counts[13], counts[14], counts[15], counts[16]);
    }

    private readonly record struct FieldResult(uint Counter, uint Requests, uint Accepted, uint Deferred, uint AdmissionErrors,
        uint Calls, uint Shapes, uint ValueErrors, uint ScopeErrors, uint HelperQueries, uint Steps, uint Loads, uint Reserved,
        uint Floor, uint Wall, uint Sphere, uint Body);
}
