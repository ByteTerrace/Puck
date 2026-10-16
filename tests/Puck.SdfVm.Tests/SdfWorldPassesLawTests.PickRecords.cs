using System.Buffers.Binary;
using System.Numerics;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    // A pick reads the record its copy took: the surface pass writes every record of the render extent each frame, so the
    // record under the sample is the frame's own, and the answer carries its ray parameter, identity and material.
    [Fact]
    public void APickAnswersFromTheRecordItsCopyTook() {
        var gpu = new UploadModelGpu();
        var pipelines = SdfTestPipelines.Cache(regionCopy: UploadModelGpu.RegionCopyBytecode);
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        builder.ResetPoint().Sphere(material: material, radius: 1f);
        var current = Frame() with { Program = builder.Build() };
        using var view = new SdfTestView(device: gpu, extent: Extent, hostsOnDirectX: false, pipelines: pipelines,
            residency: new SdfWorldResidency(brickPoolVoxelCapacity: 0,
                frameSource: new CapturingFrameSource(capture: () => current), height: Extent,
                kernels: SdfTestPipelines.Kernels(), name: SdfTestView.Instance, pipelines: pipelines, width: Extent));
        var context = ContextOf(gpu: gpu);

        TestLiveness.Until(step: () => view.Produce(context: in context),
            reason: () => (view.Runtime.Node(instance: 0).LastSwapError?.ToString() ?? view.NotReadyReason), wait: view.Residency.WaitPipelineBuilds);
        var records = gpu.DeviceLocal(part: "sdf.world$visibility", sizeBytes: ((((ulong)Extent) * Extent) * SdfWorldPackage.VisibilityRecordByteLength));
        var picker = view.Passes.PickerOf(instance: SdfTestView.Instance);
        // (0.25, 0.75) samples pixel (8, 24).
        var record = ((((24 * ((int)Extent)) + 8)) * SdfWorldPackage.VisibilityRecordByteLength);

        BinaryPrimitives.WriteSingleLittleEndian(destination: records.AsSpan(start: record), value: 5f);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: records.AsSpan(start: (record + 4)), value: SdfVisibility.IdentityOf(kind: SdfVisibilityKind.Mesh, source: 1));
        BinaryPrimitives.WriteInt32LittleEndian(destination: records.AsSpan(start: (record + 8)), value: material);

        var request = picker.Request(x: 0.25f, y: 0.75f);

        // The copy is taken in one frame and answered from its readback in the next.
        _ = view.Produce(context: in context);
        _ = view.Produce(context: in context);
        var hit = picker.Result!.Value;

        Assert.Equal(expected: request, actual: hit.Request);
        Assert.True(condition: hit.Hit);
        Assert.Equal(expected: SdfVisibilityKind.Mesh, actual: hit.Kind);
        Assert.Equal(expected: 1U, actual: hit.Source);
        Assert.Equal(expected: 5f, actual: hit.Distance);
        Assert.Equal(expected: material, actual: hit.Material);
    }
}
