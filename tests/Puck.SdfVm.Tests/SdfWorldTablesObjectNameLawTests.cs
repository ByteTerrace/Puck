using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// The naming law for <see cref="SdfWorldTables"/> and its pipeline set: with naming on, every object they create
/// reaches the device named from the engine's own identity (<c>sdf.world</c>, its tables, sets
/// and pipelines by role, and each host-written table's region by its table), and two identical constructions name the
/// same objects alike; with naming off, the device's naming is never called.
/// </summary>
public sealed class SdfWorldTablesObjectNameLawTests {
    private const uint Extent = 16;

    // The regions construction creates, by the part their objects are named under: the host-written tables, then the
    // brick staging.
    private static readonly string[] RegionParts = ["program", "dynamic-transforms", "instance-grid", "screen-surfaces", "volumes", "decals", "screen-mappings", "mesh-region", "brick-staging"];

    private static IReadOnlyList<string> NamesOfOneConstruction(bool naming) {
        var recording = new RecordingGpuObjectNaming(isEnabled: naming);
        var gpu = new FakeGpuDevice(
            naming: recording
        );
        var ledger = new GpuWorkLedger(
            framesInFlight: SdfWorldTables.FrameRingSize,
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
        using var meshRaster = SdfTestPipelines.MeshRaster(
            device: gpu,
            ledger: ledger
        );
        using var impostorRaster = SdfTestPipelines.ImpostorRaster(
            device: gpu,
            ledger: ledger
        );
        using var pipelines = SdfTestPipelines.Build(
            device: gpu,
            includeBrickPipelines: true,
            // A brick pool's engine needs the carve baker, which the fake kernel set leaves out.
            kernels: SdfTestPipelines.Kernels().With(bytecode: new byte[] { 1 }, kernel: SdfKernel.BrickBake),
            cache: new GpuPassPipelineCache()
        );
        using var engine = new SdfWorldTables(
            device: gpu,
            options: new SdfWorldTablesOptions(
                BrickPoolVoxelCapacity: SdfBrickPoolLayout.VoxelsPerBrick,
                Program: builder.Build(),
                WorkLedger: ledger
            ),
            pipelines: pipelines,
            impostorRaster: impostorRaster,
            meshRaster: meshRaster,
            regionCopy: regionCopy.Compute!
        );

        // The pipeline set builds on the thread pool, so its names arrive in completion order; the rest in creation order.
        return [.. recording.Applied.Select(selector: static applied => $"{applied.Kind} {applied.Name}").Order(comparer: StringComparer.Ordinal)];
    }

    [Fact]
    public void EveryObjectIsNamedFromTheTablesIdentity() {
        var names = NamesOfOneConstruction(naming: true);

        Assert.NotEmpty(collection: names);
        Assert.All(
            action: static name => Assert.True(condition: (name.Contains(comparisonType: StringComparison.Ordinal, value: " sdf.world/") || name.Contains(comparisonType: StringComparison.Ordinal, value: " gpu.pass-pipelines/")), userMessage: name),
            collection: names
        );
        Assert.Contains(collection: names, expected: "Buffer sdf.world/brick-pool");
        Assert.Contains(collection: names, expected: "Buffer sdf.world/unused-member");
        Assert.Contains(collection: names, expected: "Buffer sdf.world/screen-emission");
        Assert.Contains(collection: names, expected: "Image sdf.world/sampled-filler");
        Assert.Contains(collection: names, expected: "Image sdf.world/storage-filler");
        Assert.Contains(collection: names, expected: "DescriptorPool sdf.world/descriptors");
        Assert.Contains(collection: names, expected: "DescriptorSet sdf.world/tables/world group[0]");
        Assert.Contains(collection: names, expected: "CommandPool sdf.world/commands[1]");
        // The tables own the sky map and coefficients; the graph owns their recording sets and kernel counters.
        Assert.Equal(
            actual: names.Where(predicate: static name => name.Contains(comparisonType: StringComparison.Ordinal, value: " sdf.world/sky-environment")),
            expected: [
                "Buffer sdf.world/sky-environment/coefficients",
                "Buffer sdf.world/sky-environment/map",
            ]
        );
        Assert.Contains(
            collection: names,
            filter: static name => name.StartsWith(comparisonType: StringComparison.Ordinal, value: "Pipeline gpu.pass-pipelines/sdf-beam/")
        );
    }
    [Fact]
    public void EveryRegionIsNamedByItsTable() {
        var names = NamesOfOneConstruction(naming: true);

        // The fake's default memory profile stages every region: one staging buffer and copy set per ring slot, and a
        // device-local destination except where the region stages into the brick pool. Every region's copy sets, the
        // mesh region's before any frame draws a mesh, come from the tables' one copy pool, named bare beside their own.
        Assert.Equal(
            actual: names.Where(predicate: static name => name.StartsWith(comparisonType: StringComparison.Ordinal, value: "DescriptorPool ")),
            expected: ["DescriptorPool sdf.world/descriptors", "DescriptorPool sdf.world/region-copies"]
        );

        foreach (var part in RegionParts) {
            for (var slot = 0; (slot < SdfWorldTables.FrameRingSize); slot++) {
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
