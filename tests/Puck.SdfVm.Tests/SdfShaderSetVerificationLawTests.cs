using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Laws for the once-per-device-and-kernel-set ISA handshake an <see cref="SdfWorldEngine"/> runs at construction, over
/// <see cref="FakeGpuDevice"/>: an engine built on a device that already verified the same kernel set submits no
/// handshake, and changing any one of the fourteen kernels makes the next engine verify again. The kernel set is
/// identified by <see cref="SdfWorldKernels.ContentKey"/>, which hashes every kernel with its length.
/// </summary>
public sealed class SdfShaderSetVerificationLawTests {
    private const uint Extent = 16;

    // One variant per kernel, each differing from the base set in that kernel alone.
    private static readonly Func<SdfWorldKernels, SdfWorldKernels>[] OneKernelChanged = [
        static kernels => kernels with { Ambient = Changed },
        static kernels => kernels with { Beam = Changed },
        static kernels => kernels with { BrickBake = Changed },
        static kernels => kernels with { BrickUpload = Changed },
        static kernels => kernels with { Composite = Changed },
        static kernels => kernels with { CullArgs = Changed },
        static kernels => kernels with { FrameUpload = Changed },
        static kernels => kernels with { InstanceCull = Changed },
        static kernels => kernels with { Primary = Changed },
        static kernels => kernels with { Sky = Changed },
        static kernels => kernels with { Surface = Changed },
        static kernels => kernels with { Views = Changed },
        static kernels => kernels with { ViewsCore = Changed },
        static kernels => kernels with { ViewsFolds = Changed },
    ];
    private static readonly ReadOnlyMemory<byte> Changed = new byte[] { 2 };

    [Fact]
    public void ChangingAnyOneKernelChangesTheContentKey() {
        var baseline = SdfTestPipelines.Kernels();
        var keys = OneKernelChanged.Select(selector: change => change(arg: baseline).ContentKey()).ToHashSet(comparer: StringComparer.Ordinal);

        Assert.Equal(expected: 14, actual: OneKernelChanged.Length);
        Assert.Equal(expected: OneKernelChanged.Length, actual: keys.Count);
        Assert.DoesNotContain(collection: keys, expected: baseline.ContentKey());
    }
    [Fact]
    public void ChangingAnyOneKernelMakesTheNextEngineVerifyAgain() {
        var baseline = SdfTestPipelines.Kernels();

        foreach (var change in OneKernelChanged) {
            var gpu = new FakeGpuDevice(reportVersion: SdfIsa.Version);
            var handshake = Construct(gpu: gpu, kernels: baseline);

            Assert.True(condition: (handshake > 0));
            Assert.Equal(expected: 0, actual: Construct(gpu: gpu, kernels: baseline));
            Assert.Equal(expected: handshake, actual: Construct(gpu: gpu, kernels: change(arg: baseline)));
        }
    }

    // Builds an engine and its pipelines on the device and returns the submissions its construction made.
    private static int Construct(FakeGpuDevice gpu, SdfWorldKernels kernels) {
        var builder = new SdfProgramBuilder();
        var ledger = new GpuWorkLedger(
            framesInFlight: SdfWorldEngine.FrameRingSize,
            name: "gpu.sdf-engine"
        );

        builder.Sphere(
            material: builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)),
            radius: 1f
        );

        using var pipelines = SdfTestPipelines.Build(
            device: gpu,
            gpu: gpu,
            kernels: kernels,
            ledger: ledger
        );
        var before = gpu.Submissions;

        using var engine = new SdfWorldEngine(
            device: gpu,
            gpu: gpu,
            height: Extent,
            options: new SdfWorldEngineOptions(
                BrickPoolVoxelCapacity: 0,
                Program: builder.Build(),
                ViewportCapacity: 1,
                WorkLedger: ledger
            ),
            pipelines: pipelines,
            width: Extent
        );

        return (gpu.Submissions - before);
    }
}
