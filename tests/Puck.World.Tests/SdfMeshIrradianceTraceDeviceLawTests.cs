using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>THE LAW: software GPU traversal reads the CPU scene's hierarchy and writes the same hit identities and
/// packed normals, with signed distances within a relative float tolerance. A hit/miss disagreement is permitted only
/// within that tolerance of the ray's reach. Identity, mirrored and nonuniform instances share geometry; empty scenes,
/// back faces, open space, materials and near/far rays are all compared.</summary>
[SupportedOSPlatform("windows10.0.15063")]
[Trait("Category", "Gpu")]
public sealed class SdfMeshIrradianceTraceDeviceLawTests {
    private const float RelativeTolerance = 0.00002f;

    [Fact]
    public void DirectXTracesTheCpuScene() {
        using var device = DirectXTestDevices.Hardware();
        Verify(device.Services, ".dxil");
    }

    [Fact]
    public void VulkanTracesTheCpuScene() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfMeshIrradianceTraceDeviceLawTests));
        Verify(device.Services, ".spv");
    }

    private static void Verify(GpuDeviceServices gpu, string extension) {
        var mesh = Box();
        SdfMeshDraw[] draws = [
            new(Identity: new object(), Material: 2, Mesh: mesh, ObjectToWorld: Matrix4x4.Identity),
            new(Identity: new object(), Material: 7, Mesh: mesh, ObjectToWorld: Matrix4x4.CreateScale(2, 0.7f, 3) * Matrix4x4.CreateRotationY(0.37f) * Matrix4x4.CreateTranslation(5, 2, 1)),
            new(Identity: new object(), Material: 13, Mesh: mesh, ObjectToWorld: Matrix4x4.CreateScale(-1, 3, 2) * Matrix4x4.CreateRotationZ(0.23f) * Matrix4x4.CreateTranslation(-4, 0, -2)),
        ];
        var origins = new List<Vector4>();
        for (var x = -7; x <= 7; x += 2) {
            for (var y = -3; y <= 5; y += 2) {
                for (var z = -7; z <= 7; z += 2) { origins.Add(new Vector4(x + 0.137f, y + 0.271f, z + 0.319f, 30)); }
            }
        }
        origins.Add(new Vector4(0, 0, 0, 30));
        origins.Add(new Vector4(0, 3, 0, 0.1f));
        origins.Add(new Vector4(100, 100, 100, 1));
        Compare(gpu, extension, draws, origins.ToArray());
        Compare(gpu, extension, [], origins.ToArray());
    }

    private static void Compare(GpuDeviceServices gpu, string extension, SdfMeshDraw[] draws, Vector4[] origins) {
        using var scene = SdfMeshIrradianceScene.Build(draws);
        var placements = new Dictionary<SdfMesh, SdfMeshRegionMesh>();
        var layout = SdfMeshRegion.Plan(draws, placements);
        var meshes = new uint[Math.Max(1, layout.Words)];
        SdfMeshRegion.Write(draws, placements, layout, Enumerable.Repeat(1u, draws.Length).ToArray(), meshes);
        var result = Run(gpu, extension, scene.ExportGpu(placements), meshes, origins);
        var expected = new uint[SdfMeshProbeDepth.HitWords];
        var hits = 0;
        var backFaces = 0;
        for (var probe = 0; probe < origins.Length; probe++) {
            var origin = origins[probe];
            SdfMeshProbeDepth.TraceHits(scene, new Vector3(origin.X, origin.Y, origin.Z), origin.W, expected, out var backs);
            backFaces += backs;
            for (var texel = 0; texel < SdfMeshProbeDepth.HitCount; texel++) {
                var word = 2 * texel;
                var at = probe * SdfMeshProbeDepth.HitWords + word;
                var cpu = BitConverter.UInt32BitsToSingle(expected[word]);
                var device = BitConverter.UInt32BitsToSingle(result[at]);
                var label = $"{extension} probe {probe} texel {texel}: CPU {cpu}, GPU {device}";
                var cpuMiss = float.IsPositiveInfinity(cpu);
                var gpuMiss = float.IsPositiveInfinity(device);
                if (cpuMiss || gpuMiss) {
                    Assert.True(cpuMiss == gpuMiss || MathF.Abs(MathF.Abs(cpuMiss ? device : cpu) - origin.W) <= RelativeTolerance * origin.W, label);
                    if (cpuMiss && gpuMiss) { Assert.Equal(expected[word + 1], result[at + 1]); }
                    continue;
                }
                hits++;
                Assert.True(MathF.Abs(cpu - device) <= RelativeTolerance * MathF.Max(1, MathF.Abs(cpu)), label);
                Assert.True(expected[word + 1] == result[at + 1], $"{label}: packed CPU {expected[word + 1]:x8}, GPU {result[at + 1]:x8}");
            }
        }
        if (draws.Length > 0) { Assert.True(hits > 0 && backFaces > 0); }
    }

    private static uint[] Run(GpuDeviceServices gpu, string extension, uint[] scene, uint[] meshes, Vector4[] origins) {
        var layout = SdfWorldInterfaces.ProbeTraceLayout;
        var description = new GpuComputePipelineDescription(Bindings: [], Name: "sdf-probe-trace", PushConstantBinding: null,
            Layout: layout.PipelineLayout(GpuShaderStage.Compute));
        using var module = gpu.ShaderModuleFactory.Create(stage: GpuShaderStage.Compute,
            bytecode: File.ReadAllBytes(Path.Combine(SdfKernelSet.DefaultDirectory, "sdf-probe-trace.comp" + extension)));
        using var pipeline = gpu.PipelineFactory.Create(computeShaderModule: module, description: description, name: default);
        using var sceneBuffer = gpu.BufferFactory.CreateHostVisible(name: default, sizeBytes: (ulong)scene.Length * 4, usage: GpuBufferUsage.Storage);
        using var meshBuffer = gpu.BufferFactory.CreateHostVisible(name: default, sizeBytes: (ulong)meshes.Length * 4, usage: GpuBufferUsage.Storage);
        using var originBuffer = gpu.BufferFactory.CreateHostVisible(name: default, sizeBytes: (ulong)origins.Length * 16, usage: GpuBufferUsage.Storage);
        using var directionBuffer = gpu.BufferFactory.CreateHostVisible(name: default, sizeBytes: (ulong)SdfMeshProbeDepth.HitCount * 16, usage: GpuBufferUsage.Storage);
        var bytes = (ulong)(origins.Length * SdfMeshProbeDepth.HitWords * 4);
        using var output = gpu.BufferFactory.CreateDeviceLocal(name: default, sizeBytes: bytes, usage: GpuBufferUsage.Storage);
        using var readback = gpu.BufferFactory.CreateReadback(name: default, sizeBytes: bytes);
        using var commands = gpu.CommandPoolFactory.Create(name: default);
        var pool = gpu.Bindings.CreatePool(name: default, sizes: GpuDescriptorPoolSizes.ForGroups(description.Layout!.Groups));
        try {
            sceneBuffer.Write<uint>(scene);
            meshBuffer.Write<uint>(meshes);
            originBuffer.Write<Vector4>(origins);
            var directions = SdfMeshProbeDepth.RayDirections.ToArray().Select(v => new Vector4(v, 0)).ToArray();
            directionBuffer.Write<Vector4>(directions);
            var set = gpu.Bindings.AllocateSet(descriptorSetLayoutHandle: pipeline.GroupLayoutHandles[(int)ShaderInterfaceGroup.Pass], name: default, poolHandle: pool);
            Bind(SdfKernelInterfaces.ProbeScene, sceneBuffer);
            Bind(SdfWorldPackage.MeshRegion, meshBuffer);
            Bind(SdfKernelInterfaces.ProbeOrigins, originBuffer);
            Bind(SdfKernelInterfaces.ProbeDirections, directionBuffer);
            Bind(SdfKernelInterfaces.ProbeHitsWritten, output);
            var recorder = gpu.Recorder;
            var command = commands.CommandBufferHandle;
            recorder.BeginCommandBuffer(command);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: output.BufferHandle,
                sourceAccessMask: GpuAccess.None, sourceStageMask: GpuStage.TopOfPipe,
                destinationAccessMask: GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
            recorder.BindPipeline(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, pipelineHandle: pipeline.Handle);
            recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, descriptorSetHandle: set, group: (uint)ShaderInterfaceGroup.Pass, pipelineLayoutHandle: pipeline.LayoutHandle);
            recorder.Dispatch(commandBufferHandle: command, groupCountX: (uint)((origins.Length * SdfMeshProbeDepth.HitCount + SdfMeshProbeTrace.GroupSize - 1) / SdfMeshProbeTrace.GroupSize), groupCountY: 1, groupCountZ: 1);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: output.BufferHandle,
                sourceAccessMask: GpuAccess.ShaderWrite, sourceStageMask: GpuStage.ComputeShader,
                destinationAccessMask: GpuAccess.TransferRead, destinationStageMask: GpuStage.Transfer);
            recorder.CopyBuffer(commandBufferHandle: command, sourceBufferHandle: output.BufferHandle, destinationBufferHandle: readback.BufferHandle, sizeBytes: bytes);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: readback.BufferHandle,
                sourceAccessMask: GpuAccess.TransferWrite, sourceStageMask: GpuStage.Transfer,
                destinationAccessMask: GpuAccess.HostRead, destinationStageMask: GpuStage.Host);
            recorder.EndCommandBuffer(command);
            gpu.QueueSubmitter.SubmitAndWait([command]);
            var result = new uint[origins.Length * SdfMeshProbeDepth.HitWords];
            readback.Read(MemoryMarshal.AsBytes(result.AsSpan()));
            return result;

            void Bind(string name, IGpuBuffer buffer) {
                var resource = SdfKernelInterfaces.ResourceOf(layout, name);
                gpu.Bindings.WriteBuffer(binding: resource.Binding, bufferHandle: buffer.BufferHandle, bufferSize: buffer.SizeBytes,
                    descriptorSetHandle: set, elementStride: resource.Member.Name is SdfKernelInterfaces.ProbeOrigins or SdfKernelInterfaces.ProbeDirections ? 16u : 4u,
                    kind: name == SdfKernelInterfaces.ProbeHitsWritten ? GpuBindingKind.ReadWriteBuffer : GpuBindingKind.ReadOnlyBuffer);
            }
        } finally { gpu.Bindings.DestroyPool(pool); }
    }

    private static SdfMesh Box() {
        Vector3[] positions = [new(-1,-1,-1),new(1,-1,-1),new(1,1,-1),new(-1,1,-1),new(-1,-1,1),new(1,-1,1),new(1,1,1),new(-1,1,1)];
        uint[] indices = [0,2,1,0,3,2,4,5,6,4,6,7,0,1,5,0,5,4,3,7,6,3,6,2,0,4,7,0,7,3,1,2,6,1,6,5];
        return new SdfMesh(indices: indices, positions: positions);
    }
}
