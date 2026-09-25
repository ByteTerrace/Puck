using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// The naming law for <see cref="SdfWorldEngine"/> and its pipeline set: with naming on, every object they create
/// reaches the device named from the engine's own identity (<c>sdf.world</c>, its tables, sets
/// and pipelines by role), and two identical constructions name the same objects alike; with naming off, the device's
/// naming is never called.
/// </summary>
public sealed class SdfWorldEngineObjectNameLawTests {
    private const uint Extent = 16;

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
            includeBrickPipelines: false,
            kernels: SdfTestPipelines.Kernels(),
            ledger: ledger
        );
        using var engine = new SdfWorldEngine(
            device: gpu,
            height: Extent,
            options: new SdfWorldEngineOptions(
                BrickPoolVoxelCapacity: 0,
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
        Assert.Contains(collection: names, expected: "Buffer sdf.world/viewports/host[0]");
        Assert.Contains(collection: names, expected: "Buffer sdf.world/viewports");
        Assert.Contains(collection: names, expected: "Image sdf.world/output");
        Assert.Contains(collection: names, expected: "DescriptorPool sdf.world/descriptors");
        Assert.Contains(collection: names, expected: "DescriptorSet sdf.world/views[1]");
        Assert.Contains(collection: names, expected: "DescriptorSet sdf.world/dynamic-transforms/upload[0]");
        Assert.Contains(collection: names, expected: "CommandPool sdf.world/commands[1]");
        Assert.Contains(
            collection: names,
            filter: static name => name.StartsWith(comparisonType: StringComparison.Ordinal, value: "Pipeline sdf.world/")
        );
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
