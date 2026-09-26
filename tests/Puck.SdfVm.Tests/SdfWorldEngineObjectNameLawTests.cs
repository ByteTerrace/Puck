using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// The naming law for <see cref="SdfWorldEngine"/> and its pipeline set: with naming on, every object they create
/// reaches the device named from the engine's own identity (<c>sdf.world</c>, its tables, sets
/// and pipelines by role, and each host-written table's region by its table), and two identical constructions name the
/// same objects alike; with naming off, the device's naming is never called.
/// </summary>
public sealed class SdfWorldEngineObjectNameLawTests {
    private const uint Extent = 16;

    // The regions construction creates, by the part their objects are named under: the host-written tables, then the
    // brick staging.
    private static readonly string[] RegionParts = ["program", "viewports", "dynamic-transforms", "instance-grid", "screen-surfaces", "screen-lights", "volumes", "decals", "brick-staging"];

    private static IReadOnlyList<string> NamesOfOneConstruction(bool naming) {
        var recording = new RecordingGpuObjectNaming(isEnabled: naming);
        var gpu = new FakeGpuDevice(
            naming: recording,
            reportVersion: SdfIsa.Version
        );
        var ledger = new GpuWorkLedger(
            framesInFlight: SdfWorldEngine.FrameRingSize,
            name: "gpu.sdf-engine"
        );
        var builder = new SdfProgramBuilder();

        builder.Sphere(
            material: builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)),
            radius: 1f
        );

        using var regionCopy = SdfTestPipelines.RegionCopy(
            device: gpu,
            ledger: ledger
        );
        using var pipelines = SdfWorldPipelines.Build(
            cancellationToken: CancellationToken.None,
            device: gpu,
            includeBrickPipelines: true,
            // A brick pool's engine needs the carve baker, which the fake kernel set leaves out.
            kernels: (SdfTestPipelines.Kernels() with {
                BrickBake = new byte[] { 1 },
            }),
            ledger: ledger
        );
        using var engine = new SdfWorldEngine(
            device: gpu,
            height: Extent,
            options: new SdfWorldEngineOptions(
                BrickPoolVoxelCapacity: SdfBrickPoolLayout.VoxelsPerBrick,
                Program: builder.Build(),
                ViewportCapacity: 2,
                WorkLedger: ledger
            ),
            pipelines: pipelines,
            regionCopy: regionCopy,
            width: Extent
        );

        // The pipeline set builds on the thread pool, so its names arrive in completion order; the rest in creation order.
        return [.. recording.Applied.Select(selector: static applied => $"{applied.Kind} {applied.Name}").Order(comparer: StringComparer.Ordinal)];
    }

    [Fact]
    public void EveryObjectIsNamedFromTheEnginesIdentity() {
        var names = NamesOfOneConstruction(naming: true);

        Assert.NotEmpty(collection: names);
        Assert.All(
            action: static name => Assert.True(condition: (name.Contains(comparisonType: StringComparison.Ordinal, value: " sdf.world/") || name.Contains(comparisonType: StringComparison.Ordinal, value: " gpu.region-copy/")), userMessage: name),
            collection: names
        );
        Assert.Contains(collection: names, expected: "Buffer sdf.world/tiles");
        Assert.Contains(collection: names, expected: "Buffer sdf.world/brick-pool");
        Assert.Contains(collection: names, expected: "Image sdf.world/output");
        Assert.Contains(collection: names, expected: "DescriptorPool sdf.world/descriptors");
        Assert.Contains(collection: names, expected: "DescriptorSet sdf.world/views[1]");
        Assert.Contains(collection: names, expected: "CommandPool sdf.world/commands[1]");
        Assert.Contains(
            collection: names,
            filter: static name => name.StartsWith(comparisonType: StringComparison.Ordinal, value: "Pipeline sdf.world/")
        );
    }
    [Fact]
    public void EveryRegionIsNamedByItsTable() {
        var names = NamesOfOneConstruction(naming: true);

        // The fake's default memory profile stages every region: one staging buffer and copy set per ring slot, and a
        // device-local destination except where the region stages into the brick pool. Every region's copy sets, the
        // mesh region's before any frame draws a mesh, come from the engine's one copy pool, named bare beside its own.
        Assert.Equal(
            actual: names.Where(predicate: static name => name.StartsWith(comparisonType: StringComparison.Ordinal, value: "DescriptorPool ")),
            expected: ["DescriptorPool sdf.world/descriptors", "DescriptorPool sdf.world/region-copies"]
        );

        for (var slot = 0; (slot < SdfWorldEngine.FrameRingSize); slot++) {
            Assert.Contains(collection: names, expected: $"DescriptorSet sdf.world/mesh-region[{slot}]");
        }

        foreach (var part in RegionParts) {
            for (var slot = 0; (slot < SdfWorldEngine.FrameRingSize); slot++) {
                Assert.Contains(collection: names, expected: $"Buffer sdf.world/{part}[{slot}]");
                Assert.Contains(collection: names, expected: $"DescriptorSet sdf.world/{part}[{slot}]");
            }

            Assert.DoesNotContain(collection: names, expected: $"DescriptorPool sdf.world/{part}");

            if (part == "brick-staging") {
                Assert.DoesNotContain(collection: names, expected: $"Buffer sdf.world/{part}");
            } else {
                Assert.Contains(collection: names, expected: $"Buffer sdf.world/{part}");
            }
        }
    }
    [Fact]
    public void TwoIdenticalConstructionsNameTheSameObjectsAlike() =>
        Assert.Equal(
            actual: NamesOfOneConstruction(naming: true),
            expected: NamesOfOneConstruction(naming: true)
        );
    [Fact]
    public void NothingIsNamedWhenNamingIsOff() =>
        Assert.Empty(collection: NamesOfOneConstruction(naming: false));
}
