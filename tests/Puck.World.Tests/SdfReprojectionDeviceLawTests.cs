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

/// <summary>Visibility reprojection agrees with ViewProjection for camera motion, an articulated rigid slot and a
/// nonuniformly scaled mesh. Source identity deliberately differs from the rigid slot. Invalid history, background,
/// and a point behind the prior camera are refused. Both hardware backends read the shipped shader module.</summary>
[SupportedOSPlatform("windows10.0.15063")]
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
    private static readonly Quaternion CurrentRotation = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: 0.6f);
    private static readonly Quaternion PreviousRotation = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitZ, angle: -0.3f);
    private static readonly Vector3 CurrentPosition = new(x: 0.75f, y: -0.25f, z: 0.5f);
    private static readonly Vector3 PreviousPosition = new(x: -0.25f, y: 0.5f, z: 0f);
    private static readonly Matrix4x4 CurrentMesh = Matrix4x4.CreateScale(xScale: 2f, yScale: 0.5f, zScale: 1f) *
        Matrix4x4.CreateRotationY(radians: 0.4f) * Matrix4x4.CreateTranslation(xPosition: 0.5f, yPosition: 0.25f, zPosition: 1f);
    private static readonly Matrix4x4 PreviousMesh = Matrix4x4.CreateScale(xScale: 1.25f, yScale: 0.75f, zScale: 1f) *
        Matrix4x4.CreateRotationZ(radians: -0.2f) * Matrix4x4.CreateTranslation(xPosition: -0.5f, yPosition: 0.5f, zPosition: 0f);
    private static readonly SdfMesh Mesh = new(positions: new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY },
        indices: new uint[] { 0, 1, 2 });
    private readonly record struct MotionCase(Vector3 Current, Vector3 Previous, uint Identity, int Slot, bool History, bool Valid);
    private static MotionCase[] Cases() {
        var rigid = new Vector3(x: 0.125f, y: 0.375f, z: 0.25f);
        var mesh = new Vector3(x: 0.3f, y: 0.5f, z: 0f);
        return [
            new(Current: Vector3.One, Previous: Vector3.One, Identity: (1u << 30), Slot: -1, History: true, Valid: true),
            new(Current: (Vector3.Transform(value: rigid, rotation: CurrentRotation) + CurrentPosition),
                Previous: (Vector3.Transform(value: rigid, rotation: PreviousRotation) + PreviousPosition),
                Identity: ((1u << 30) | 10u), Slot: 1, History: true, Valid: true),
            new(Current: Vector3.Transform(position: mesh, matrix: CurrentMesh), Previous: Vector3.Transform(position: mesh, matrix: PreviousMesh),
                Identity: (2u << 30), Slot: 0, History: true, Valid: true),
            new(Current: Vector3.One, Previous: Vector3.One, Identity: (1u << 30), Slot: -1, History: false, Valid: false),
            new(Current: Vector3.One, Previous: Vector3.One, Identity: 0, Slot: -1, History: true, Valid: false),
            new(Current: new Vector3(x: 0f, y: 0f, z: -10f), Previous: default, Identity: (1u << 30), Slot: -1, History: true, Valid: false),
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
        var results = Run(services: services, extension: extension, cases: cases);
        var projection = ViewProjection.Create(camera: Camera, near: Camera.Near,
            jitter: new Vector2(x: (2f * Jitter.X / Width), y: (-2f * Jitter.Y / Height)));
        for (var index = 0; index < cases.Length; index++) {
            var item = cases[index];
            Assert.Equal(expected: (item.Valid ? 1f : 0f), actual: results[index].W);
            if (!item.Valid) {
                continue;
            }
            var clip = projection.ToClip(world: item.Previous);
            var ndc = new Vector2(x: (clip.X / clip.W), y: (clip.Y / clip.W));
            var pixel = new Vector2(x: ((ndc.X + 1f) * 0.5f * Width), y: ((1f - ndc.Y) * 0.5f * Height));
            var t = projection.RayParameter(ndc: ndc, depth: (clip.Z / clip.W));
            Assert.True(condition: (Vector2.Distance(value1: pixel, value2: new Vector2(x: results[index].X, y: results[index].Y)) < 0.002f),
                userMessage: $"case {index}: expected pixel {pixel}, got {results[index]}");
            Assert.InRange(actual: MathF.Abs(x: (t - results[index].Z)), low: 0f, high: 0.00002f);
        }
    }
    private static Vector4[] Run(GpuDeviceServices services, string extension, MotionCase[] cases) {
        SdfMeshDraw[] draws = [new(Mesh: Mesh, ObjectToWorld: CurrentMesh, Material: 0)];
        var meshes = new Dictionary<SdfMesh, SdfMeshRegionMesh>(comparer: ReferenceEqualityComparer.Instance);
        var meshLayout = SdfMeshRegion.Plan(draws: draws, meshes: meshes);
        var meshWords = new uint[meshLayout.Words];
        SdfMeshRegion.Write(draws: draws, meshes: meshes, layout: meshLayout, destination: meshWords);
        var records = new uint[(cases.Length * 16)];
        var probes = new Vector4[(cases.Length * 7)];
        var block = new byte[SdfFrameBlock.SizeBytes];
        for (var index = 0; index < cases.Length; index++) {
            var item = cases[index];
            records[(index * 16) + 1] = item.Identity;
            records[(index * 16) + 7] = unchecked((uint)item.Slot);
            probes[(index * 7)] = new Vector4(value: item.Current, w: 0f);
            SdfFrameBlock.WritePreviousView(block: block, view: new SdfReprojectionView(Camera: Camera, Jitter: Jitter, Width: Width, Height: Height), valid: item.History);
            var offset = ((int)SdfWorldInterfaces.WorldParameters.BlockOffsetOf(member: SdfWorldPackage.PreviousView));
            MemoryMarshal.Cast<byte, Vector4>(span: block.AsSpan(start: offset, length: 96)).CopyTo(destination: probes.AsSpan(start: ((index * 7) + 1), length: 6));
        }
        Vector4[] CurrentTransforms(Vector3 position, Quaternion rotation) => [
            new(x: 50f, y: 50f, z: 50f, w: 0f), new(x: 0f, y: 0f, z: 0f, w: 1f), Vector4.Zero,
            new(value: position, w: 0f), new(x: rotation.X, y: rotation.Y, z: rotation.Z, w: rotation.W), Vector4.Zero,
        ];
        var previousMatrices = new[] { PreviousMesh };
        byte[][] contents = [
            MemoryMarshal.AsBytes(span: meshWords.AsSpan()).ToArray(),
            MemoryMarshal.AsBytes(span: CurrentTransforms(position: CurrentPosition, rotation: CurrentRotation).AsSpan()).ToArray(),
            MemoryMarshal.AsBytes(span: CurrentTransforms(position: PreviousPosition, rotation: PreviousRotation).AsSpan()).ToArray(),
            MemoryMarshal.AsBytes(span: previousMatrices.AsSpan()).ToArray(),
            MemoryMarshal.AsBytes(span: records.AsSpan()).ToArray(),
            MemoryMarshal.AsBytes(span: probes.AsSpan()).ToArray(),
        ];
        var description = new GpuComputePipelineDescription(Bindings: [], Name: Kernel, PushConstantBinding: null,
            Layout: new GpuPipelineLayoutDescription(groups: [new GpuGroupLayoutDescription(ordinal: 3,
                bindings: [.. Enumerable.Range(start: 0, count: 6).Select(selector: index =>
                    new GpuGroupBinding(binding: ((uint)index), kind: GpuBindingKind.ReadOnlyBuffer)),
                    new GpuGroupBinding(binding: 6, kind: GpuBindingKind.StorageImage)])], pushesIndex: false, stages: GpuShaderStage.Compute));
        using var module = services.ShaderModuleFactory.Create(stage: GpuShaderStage.Compute,
            bytecode: File.ReadAllBytes(path: Path.Combine(AppContext.BaseDirectory, "Assets", "Shaders", Kernel + extension)));
        using var pipeline = services.PipelineFactory.Create(computeShaderModule: module, description: description, name: default);
        using var output = services.ImageFactory.Create(format: GpuPixelFormat.R32G32B32A32Float, height: 1, width: ((uint)cases.Length),
            usage: GpuImageUsage.Storage, name: default);
        using var readback = services.SurfaceTransferFactory.CreateReadback();
        using var commands = services.CommandPoolFactory.Create(name: default);
        var buffers = new List<IGpuStorageBuffer>();
        var pool = services.Bindings.CreatePool(name: default, sizes: GpuDescriptorPoolSizes.ForGroups(groups: description.Layout!.Groups));
        try {
            var set = services.Bindings.AllocateSet(descriptorSetLayoutHandle: pipeline.GroupLayoutHandles[3], name: default, poolHandle: pool);
            for (var index = 0; index < contents.Length; index++) {
                var buffer = services.BufferFactory.CreateHostVisible(sizeBytes: ((ulong)contents[index].Length), usage: GpuBufferUsage.Storage, name: default);
                buffers.Add(item: buffer);
                buffer.Write<byte>(data: contents[index]);
                services.Bindings.WriteBuffer(binding: ((uint)index), bufferHandle: buffer.BufferHandle, bufferSize: buffer.SizeBytes,
                    descriptorSetHandle: set, elementStride: ((index is 0 or 4) ? 4u : 16u), kind: GpuBindingKind.ReadOnlyBuffer);
            }
            services.Bindings.WriteStorageImage(arrayElement: 0, binding: 6, descriptorSetHandle: set, imageViewHandle: output.ImageViewHandle);
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
