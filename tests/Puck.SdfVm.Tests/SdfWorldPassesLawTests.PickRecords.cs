using System.Buffers.Binary;
using System.Numerics;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    // A pick reads the record its copy took and the dispatch box of the frame that wrote it: a pixel outside the box is
    // an earlier frame's record and answers nothing, and a winning slot resolves against the transform table of the frame
    // the record was rendered from, never a later one.
    [Fact]
    public void APickAnswersFromItsFramesDispatchBoxAndItsFramesTransformTable() {
        var gpu = new UploadModelGpu();
        var pipelines = SdfTestPipelines.Cache(regionCopy: UploadModelGpu.RegionCopyBytecode);
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        builder.ResetPoint().TransformDynamic(slot: 2).Sphere(material: material, radius: 1f);
        DynamicTransform[] rendered = [
            new(Orientation: Quaternion.Identity, Position: Vector3.Zero),
            new(Orientation: Quaternion.Identity, Position: Vector3.UnitX),
            new(Lanes: new Vector4(value: 4f), Orientation: Quaternion.Identity, Position: new Vector3(x: 1f, y: 2f, z: 3f)),
        ];
        var current = Frame() with { DynamicTransforms = rendered, Program = builder.Build() };
        using var view = new SdfTestView(device: gpu, extent: Extent, hostsOnDirectX: false, pipelines: pipelines,
            residency: new SdfWorldResidency(brickPoolVoxelCapacity: 0,
                frameSource: new CapturingFrameSource(capture: () => current), height: Extent,
                kernels: SdfTestPipelines.Kernels(), name: SdfTestView.Instance, pipelines: pipelines, width: Extent));
        var context = ContextOf(gpu: gpu);

        TestLiveness.Until(step: () => view.Produce(context: in context),
            reason: () => (view.Runtime.Node(instance: 0).LastSwapError?.ToString() ?? view.NotReadyReason), wait: view.Residency.WaitPipelineBuilds);
        var records = gpu.DeviceLocal(part: "sdf.world$visibility", sizeBytes: ((((ulong)Extent) * Extent) * 96));
        var box = gpu.DeviceLocal(part: "sdf.world$cullBounds", sizeBytes: BoxBytes);
        var picker = view.Passes.PickerOf(instance: SdfTestView.Instance);
        // (0.25, 0.75) samples pixel (8, 24).
        var record = ((((24 * ((int)Extent)) + 8)) * 96);

        BinaryPrimitives.WriteSingleLittleEndian(destination: records.AsSpan(start: record), value: 5f);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: records.AsSpan(start: (record + 4)), value: SdfVisibility.IdentityOf(kind: SdfVisibilityKind.Sdf, source: 1));
        BinaryPrimitives.WriteInt32LittleEndian(destination: records.AsSpan(start: (record + 8)), value: material);

        SdfPickResult Pick(uint[] boxWords, int slot, DynamicTransform[]? movedBeforeTheAnswer = null) {
            for (var word = 0; (word < boxWords.Length); word++) {
                BinaryPrimitives.WriteUInt32LittleEndian(destination: box.AsSpan(start: (word * sizeof(uint))), value: boxWords[word]);
            }
            BinaryPrimitives.WriteInt32LittleEndian(destination: records.AsSpan(start: (record + 28)), value: slot);
            var request = picker.Request(x: 0.25f, y: 0.75f);

            _ = view.Produce(context: in context);
            if (movedBeforeTheAnswer is not null) {
                current = current with { DynamicTransforms = movedBeforeTheAnswer };
            }
            _ = view.Produce(context: in context);
            var result = picker.Result!.Value;

            Assert.Equal(expected: request, actual: result.Request);
            return result;
        }

        DynamicTransform[] moved = [rendered[0], rendered[1], rendered[2] with { Position = new Vector3(x: 9f, y: 9f, z: 9f) }];
        var hit = Pick(boxWords: [0, 0, 4, 4], movedBeforeTheAnswer: moved, slot: 2);

        Assert.Equal(expected: SdfVisibilityKind.Sdf, actual: hit.Kind);
        Assert.Equal(expected: 1U, actual: hit.Source);
        Assert.Equal(expected: 5f, actual: hit.Distance);
        Assert.Equal(expected: 2, actual: hit.TransformSlot);
        Assert.Equal(expected: rendered[2], actual: hit.Transform);
        // The next pick records against the moved table.
        Assert.Equal(expected: moved[2], actual: Pick(boxWords: [0, 0, 4, 4], slot: 2).Transform);
        var still = Pick(boxWords: [0, 0, 4, 4], slot: SdfProgram.NoDynamicTransformSlot);

        Assert.True(condition: still.Hit);
        Assert.Null(@object: still.TransformSlot);
        Assert.Null(@object: still.Transform);
        // The box's lower edges are inclusive and its upper edges exclusive, in hit-pass groups of eight pixels.
        Assert.True(condition: Pick(boxWords: [1, 3, 2, 4], slot: 2).Hit);
        foreach (var outside in new uint[][] { [0, 0, 1, 4], [0, 0, 4, 3], [2, 0, 4, 4], [0, 4, 4, 4], [0, 0, 0, 0] }) {
            var stale = Pick(boxWords: outside, slot: 2);

            Assert.Equal(expected: SdfVisibilityKind.Background, actual: stale.Kind);
            Assert.False(condition: stale.Hit);
            Assert.Null(@object: stale.TransformSlot);
            Assert.Null(@object: stale.Transform);
        }
    }
}
