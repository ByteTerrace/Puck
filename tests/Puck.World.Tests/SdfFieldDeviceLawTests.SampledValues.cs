using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class SdfFieldDeviceLawTests {
    // A sampled field's voxels: X fastest, then Y, then Z, each a distinct value so a voxel read from the wrong word, or
    // from the wrong one of a vector's four, shows.
    private const int SampledX = 3;
    private const int SampledY = 2;
    private const int SampledZ = 2;
    private const float SampledCell = 0.5f;
    private const float SampledFloor = 0.25f;
    private static readonly Vector3 SampledMin = new(x: -0.75f, y: -0.5f, z: -0.5f);
    private static readonly float[] SampledVoxels = [.. Enumerable.Range(start: 0, count: (SampledX * SampledY * SampledZ)).Select(selector: static index => ((0.0625f * index) - 0.375f))];

    [Fact]
    public void VulkanReadsSampledValuesFromTheirProgramWords() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfFieldDeviceLawTests));

        VerifySampledValues(services: device.Services, extension: ".spv");
    }
    [Fact]
    public void DirectXReadsSampledValuesFromTheirProgramWords() {
        using var device = DirectXTestDevices.Hardware();

        VerifySampledValues(services: device.Services, extension: ".dxil");
    }

    // The interpreter's trilinear read (field/sdf-shapes.hlsli, sdfSampledRegion): voxel i is centred at i + 0.5 cells
    // from the box's minimum, the stencil clamps to the edge voxels, and a point outside the box reads its distance to
    // the box plus the boundary floor.
    private static float SampledReference(Vector3 point) {
        var extent = (new Vector3(x: SampledX, y: SampledY, z: SampledZ) * SampledCell);
        var local = ((point - SampledMin) / SampledCell);

        if ((local.X < 0f) || (local.Y < 0f) || (local.Z < 0f) || (local.X > SampledX) || (local.Y > SampledY) || (local.Z > SampledZ)) {
            return (Vector3.Max(value1: Vector3.Max(value1: (SampledMin - point), value2: (point - (SampledMin + extent))), value2: Vector3.Zero).Length() + SampledFloor);
        }
        var sample = (local - new Vector3(value: 0.5f));
        var floor = new Vector3(x: MathF.Floor(x: sample.X), y: MathF.Floor(x: sample.Y), z: MathF.Floor(x: sample.Z));
        var fraction = (sample - floor);
        float Voxel(int x, int y, int z) => SampledVoxels[(Math.Clamp(value: x, min: 0, max: (SampledX - 1)) +
            (Math.Clamp(value: y, min: 0, max: (SampledY - 1)) * SampledX) + (Math.Clamp(value: z, min: 0, max: (SampledZ - 1)) * SampledX * SampledY))];
        float Lerp(float left, float right, float weight) => (left + (weight * (right - left)));
        var (bx, by, bz) = ((int)floor.X, (int)floor.Y, (int)floor.Z);
        var c00 = Lerp(left: Voxel(x: bx, y: by, z: bz), right: Voxel(x: (bx + 1), y: by, z: bz), weight: fraction.X);
        var c10 = Lerp(left: Voxel(x: bx, y: (by + 1), z: bz), right: Voxel(x: (bx + 1), y: (by + 1), z: bz), weight: fraction.X);
        var c01 = Lerp(left: Voxel(x: bx, y: by, z: (bz + 1)), right: Voxel(x: (bx + 1), y: by, z: (bz + 1)), weight: fraction.X);
        var c11 = Lerp(left: Voxel(x: bx, y: (by + 1), z: (bz + 1)), right: Voxel(x: (bx + 1), y: (by + 1), z: (bz + 1)), weight: fraction.X);

        return Lerp(left: Lerp(left: c00, right: c10, weight: fraction.Y), right: Lerp(left: c01, right: c11, weight: fraction.Y), weight: fraction.Z);
    }
    private static void VerifySampledValues(GpuDeviceServices services, string extension) {
        // A far path ahead of the sampled field moves its values off the first side-table word.
        var program = Pack(emit: static (builder, material) => builder.ResetPoint()
            .Translate(offset: new Vector3(x: -20f, y: 0f, z: 0f))
            .Path(new([new(new(x: -0.5f, y: -0.5f), [new(new(x: 0.5f, y: -0.5f)), new(new(x: 0.5f, y: 0.5f)), new(new(x: -0.5f, y: 0.5f))])]), Vector2.One, 0.04f, material)
            .ResetPoint()
            .SampledValues(boxMin: SampledMin, cellSize: SampledCell, dimX: SampledX, dimY: SampledY, dimZ: SampledZ,
                boundaryFloor: SampledFloor, material: material, values: SampledVoxels));
        var points = new List<Vector3>();

        // Every voxel centre, every midpoint between neighbouring centres, the clamped border and points outside the box.
        for (var z = 0; (z < (2 * SampledZ)); z++) {
            for (var y = 0; (y < (2 * SampledY)); y++) {
                for (var x = 0; (x < (2 * SampledX)); x++) {
                    points.Add(item: (SampledMin + (new Vector3(x: (0.5f + (0.5f * x)), y: (0.5f + (0.5f * y)), z: (0.5f + (0.5f * z))) * SampledCell)));
                }
            }
        }
        points.Add(item: (SampledMin + new Vector3(x: 0.0625f, y: 0.0625f, z: 0.0625f)));
        points.Add(item: (SampledMin - new Vector3(x: 0.5f, y: 0f, z: 0f)));
        points.Add(item: (SampledMin + new Vector3(x: 2f, y: 1.25f, z: 0.5f)));
        var results = Run(kernel: Kernel(extension: extension), services: services, points: [.. points],
            legs: [new SdfFieldLeg(Name: "sampled values", Words: program.Words.ToArray(), Distances: [], Materials: [])]);

        Assert.Equal(expected: points.Count, actual: results.Length);
        for (var index = 0; (index < points.Count); index++) {
            var point = points[index];
            var expected = SampledReference(point: point);
            var actual = (results[index].X / results[index].Z);

            Assert.True(condition: (MathF.Abs(x: (actual - expected)) <= 0.000002f),
                userMessage: $"point {point}: the device read {actual}, the sampled reference {expected}.");
        }
    }
}
