using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Visibility reprojection agrees with ViewProjection for camera motion over a still mesh and for a nonuniformly
/// scaled mesh that moves. Invalid history, background, and a point behind the prior camera are refused. Both hardware
/// backends read the shipped shader module.</summary>
[SupportedOSPlatform("windows10.0.15063")]
[Trait("Category", "Gpu")]
public sealed class SdfReprojectionDeviceLawTests {
    private const string Kernel = "sdf-reprojection.comp";
    private const uint Width = 320;
    private const uint Height = 180;

    private static readonly CameraSnapshot Camera = new(
        Position: new Vector3(x: -0.5f, y: 0.25f, z: -5f), Right: Vector3.UnitX, Up: Vector3.UnitY,
        Forward: Vector3.UnitZ, TanHalfFieldOfView: 0.5f, AspectRatio: (16f / 9f)) {
        FrustumOffset = new Vector2(x: 0.1f, y: -0.05f),
        Near = 0.02f,
    };
    private static readonly Vector2 Jitter = new(x: 0.25f, y: -0.125f);
    private static readonly Matrix4x4 CurrentMesh = ((Matrix4x4.CreateScale(xScale: 2f, yScale: 0.5f, zScale: 1f) *
        Matrix4x4.CreateRotationY(radians: 0.4f)) * Matrix4x4.CreateTranslation(xPosition: 0.5f, yPosition: 0.25f, zPosition: 1f));
    private static readonly Matrix4x4 PreviousMesh = ((Matrix4x4.CreateScale(xScale: 1.25f, yScale: 0.75f, zScale: 1f) *
        Matrix4x4.CreateRotationZ(radians: -0.2f)) * Matrix4x4.CreateTranslation(xPosition: -0.5f, yPosition: 0.5f, zPosition: 0f));
    // A draw that does not move, in front of the cameras, and one behind the prior camera.
    private static readonly Matrix4x4 StillMesh = Matrix4x4.CreateTranslation(xPosition: 0.5f, yPosition: 0.25f, zPosition: 1f);
    private static readonly Matrix4x4 BehindMesh = Matrix4x4.CreateTranslation(xPosition: 0f, yPosition: 0f, zPosition: -10f);
    private static readonly SdfMesh Mesh = new(positions: new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY },
        indices: new uint[] { 0, 1, 2 });

    private readonly record struct MotionCase(Vector3 Current, Vector3 Previous, uint Identity, bool History, bool Valid);

    private static MotionCase[] Cases() {
        var point = new Vector3(x: 0.3f, y: 0.5f, z: 0f);
        var still = Vector3.Transform(matrix: StillMesh, position: point);
        var behind = Vector3.Transform(matrix: BehindMesh, position: point);
        uint Draw(int draw) => (SdfVisibility.IdentityOf(kind: SdfVisibilityKind.Mesh, source: ((uint)draw)));

        return [
            new(Current: still, Previous: still, Identity: Draw(draw: 1), History: true, Valid: true),
            new(Current: Vector3.Transform(matrix: CurrentMesh, position: point), Previous: Vector3.Transform(matrix: PreviousMesh, position: point),
                Identity: Draw(draw: 0), History: true, Valid: true),
            new(Current: still, Previous: still, Identity: Draw(draw: 1), History: false, Valid: false),
            new(Current: still, Previous: still, Identity: 0, History: true, Valid: false),
            new(Current: behind, Previous: behind, Identity: Draw(draw: 2), History: true, Valid: false),
        ];
    }

    [Fact]
    public void VulkanReprojectsVisibilityAsTheCameraReferenceDoes() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfReprojectionDeviceLawTests));

        Verify(services: device.Services, extension: ".spv");
    }
    [Fact]
    public void DirectXReprojectsVisibilityAsTheCameraReferenceDoes() {
        using var device = DirectXTestDevices.Hardware();

        Verify(services: device.Services, extension: ".dxil");
    }

    private static void Verify(GpuDeviceServices services, string extension) {
        var cases = Cases();
        var results = Run(cases: cases, extension: extension, services: services);
        var projection = ViewProjection.Create(camera: Camera, near: Camera.Near,
            jitter: new Vector2(x: ((2f * Jitter.X) / Width), y: ((-2f * Jitter.Y) / Height)));

        for (var index = 0; (index < cases.Length); index++) {
            var item = cases[index];

            Assert.Equal(expected: (item.Valid ? 1f : 0f), actual: results[index].W);
            if (!item.Valid) {
                continue;
            }
            var clip = projection.ToClip(world: item.Previous);
            var ndc = new Vector2(x: (clip.X / clip.W), y: (clip.Y / clip.W));
            var pixel = new Vector2(x: (((ndc.X + 1f) * 0.5f) * Width), y: (((1f - ndc.Y) * 0.5f) * Height));
            var t = projection.RayParameter(depth: (clip.Z / clip.W), ndc: ndc);

            Assert.True(condition: (Vector2.Distance(value1: pixel, value2: new Vector2(x: results[index].X, y: results[index].Y)) < 0.002f),
                userMessage: $"case {index}: expected pixel {pixel}, got {results[index]}");
            Assert.InRange(actual: MathF.Abs(x: (t - results[index].Z)), low: 0f, high: 0.00002f);
        }
    }
    private static Vector4[] Run(GpuDeviceServices services, string extension, MotionCase[] cases) {
        SdfMeshDraw[] draws = [
            new(Identity: "moving", Material: 0, Mesh: Mesh, ObjectToWorld: CurrentMesh),
            new(Identity: "still", Material: 0, Mesh: Mesh, ObjectToWorld: StillMesh),
            new(Identity: "behind", Material: 0, Mesh: Mesh, ObjectToWorld: BehindMesh),
        ];
        var meshes = new Dictionary<SdfMesh, SdfMeshRegionMesh>(comparer: ReferenceEqualityComparer.Instance);
        var meshLayout = SdfMeshRegion.Plan(draws: draws, meshes: meshes);
        var meshWords = new uint[meshLayout.Words];

        SdfMeshRegion.Write(draws: draws, meshes: meshes, layout: meshLayout, destination: meshWords);
        var recordWords = ((int)(SdfWorldPackage.VisibilityRecordByteLength / sizeof(uint)));
        var records = new uint[(cases.Length * recordWords)];
        var probes = new Vector4[(cases.Length * 7)];
        var block = new byte[SdfFrameBlock.SizeBytes];

        for (var index = 0; (index < cases.Length); index++) {
            var item = cases[index];

            // Every mesh has the one triangle, triangle 0.
            records[((index * recordWords) + 1)] = item.Identity;
            probes[(index * 7)] = new Vector4(value: item.Current, w: 0f);
            SdfFrameBlock.WritePreviousView(block: block, view: new SdfReprojectionView(Camera: Camera, Height: Height, Jitter: Jitter, Width: Width), valid: item.History);
            var offset = ((int)SdfWorldInterfaces.WorldParameters.BlockOffsetOf(member: SdfWorldPackage.PreviousView));

            MemoryMarshal.Cast<byte, Vector4>(span: block.AsSpan(length: 96, start: offset)).CopyTo(destination: probes.AsSpan(length: 6, start: ((index * 7) + 1)));
        }
        // No draw carries a frame slot, so the dynamic transforms are never read; the binding needs one row.
        var previousMatrices = new[] { PreviousMesh, StillMesh, BehindMesh };
        byte[][] contents = [
            MemoryMarshal.AsBytes(span: meshWords.AsSpan()).ToArray(),
            MemoryMarshal.AsBytes(span: new Vector4[3].AsSpan()).ToArray(),
            MemoryMarshal.AsBytes(span: previousMatrices.AsSpan()).ToArray(),
            MemoryMarshal.AsBytes(span: records.AsSpan()).ToArray(),
            MemoryMarshal.AsBytes(span: probes.AsSpan()).ToArray(),
        ];
        var description = new GpuComputePipelineDescription(Bindings: [], Name: Kernel, PushConstantBinding: null,
            Layout: new GpuPipelineLayoutDescription(groups: [new GpuGroupLayoutDescription(ordinal: 3,
                bindings: [.. Enumerable.Range(count: 5, start: 0).Select(selector: index =>
                    new GpuGroupBinding(binding: ((uint)index), kind: GpuBindingKind.ReadOnlyBuffer)),
                    new GpuGroupBinding(binding: 5, kind: GpuBindingKind.StorageImage)])], pushesIndex: false, stages: GpuShaderStage.Compute));
        using var module = services.ShaderModuleFactory.Create(stage: GpuShaderStage.Compute,
            bytecode: File.ReadAllBytes(path: Path.Combine(path1: AppContext.BaseDirectory, path2: "Assets", path3: "Shaders", path4: (Kernel + extension))));
        using var pipeline = services.PipelineFactory.Create(computeShaderModule: module, description: description, name: default);
        using var output = services.ImageFactory.Create(format: GpuPixelFormat.R32G32B32A32Float, height: 1, width: ((uint)cases.Length),
            usage: GpuImageUsage.Storage, name: default);
        using var readback = services.SurfaceTransferFactory.CreateReadback();
        using var commands = services.CommandPoolFactory.Create(name: default);
        var buffers = new List<IGpuStorageBuffer>();
        var pool = services.Bindings.CreatePool(name: default, sizes: GpuDescriptorPoolSizes.ForGroups(groups: description.Layout!.Groups));

        try {
            var set = services.Bindings.AllocateSet(descriptorSetLayoutHandle: pipeline.GroupLayoutHandles[3], name: default, poolHandle: pool);

            for (var index = 0; (index < contents.Length); index++) {
                var buffer = services.BufferFactory.CreateHostVisible(sizeBytes: ((ulong)contents[index].Length), usage: GpuBufferUsage.Storage, name: default);

                buffers.Add(item: buffer);
                buffer.Write<byte>(data: contents[index]);
                services.Bindings.WriteBuffer(binding: ((uint)index), bufferHandle: buffer.BufferHandle, bufferSize: buffer.SizeBytes,
                    descriptorSetHandle: set, elementStride: ((index is 0 or 3) ? 4u : 16u), kind: GpuBindingKind.ReadOnlyBuffer);
            }
            services.Bindings.WriteStorageImage(arrayElement: 0, binding: 5, descriptorSetHandle: set, imageViewHandle: output.ImageViewHandle);
            var recorder = services.Recorder;
            var command = commands.CommandBufferHandle;

            recorder.BeginCommandBuffer(commandBufferHandle: command);
            recorder.TransitionImageLayout(commandBufferHandle: command, imageHandle: output.ImageHandle,
                sourceAccessMask: GpuAccess.None, sourceStageMask: GpuStage.TopOfPipe, oldLayout: GpuImageLayout.Undefined,
                destinationAccessMask: GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader, newLayout: GpuImageLayout.General);
            recorder.BindPipeline(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, pipelineHandle: pipeline.Handle);
            recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, descriptorSetHandle: set, group: 3, pipelineLayoutHandle: pipeline.LayoutHandle);
            recorder.Dispatch(commandBufferHandle: command, groupCountX: ((uint)cases.Length), groupCountY: 1, groupCountZ: 1);
            recorder.EndCommandBuffer(commandBufferHandle: command);
            services.QueueSubmitter.SubmitAndWait(commandBufferHandles: [command]);
            var result = readback.Read(bytesPerPixel: 16, format: GpuPixelFormat.R32G32B32A32Float, height: 1, width: ((uint)cases.Length),
                sourceImageHandle: output.ImageHandle, sourceLayout: GpuImageLayout.General);

            return MemoryMarshal.Cast<byte, Vector4>(span: result.Span).ToArray();
        } finally {
            services.Bindings.DestroyPool(poolHandle: pool);
            foreach (var buffer in buffers) {
                buffer.Dispose();
            }
        }
    }
}
