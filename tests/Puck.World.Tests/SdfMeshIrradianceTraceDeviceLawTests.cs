using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.Testing;
using Xunit;
using Xunit.Sdk;

namespace Puck.World.Tests;

/// <summary>THE LAW: software GPU traversal reads the CPU scene's hierarchy and writes the same hit identities and
/// packed normals, with signed distances within a scene-scaled float tolerance. Near-tied valid triangles may differ;
/// hit/miss and all flags remain exact. Identity, mirrored and nonuniform instances share geometry; empty scenes,
/// back faces, open space, materials and near/far rays are all compared.</summary>
[SupportedOSPlatform("windows10.0.15063")]
[Trait("Category", "Gpu")]
public sealed class SdfMeshIrradianceTraceDeviceLawTests(ITestOutputHelper output) {
    // 64 rounding units covers the affine transforms, cross/dot products and division in this fixture's
    // well-conditioned transforms (maximum scale ratio 4.3). The plane intersection amplifies coordinate
    // error by 1/abs(n.d), so grazing rays get that explicit conditioning factor, not a distance-relative bound.
    private const float Roundoff = 64 * (1f / 8388608);

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

    private void Verify(GpuDeviceServices gpu, string extension) {
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
        Assert.ThrowsAny<XunitException>(() => Compare(gpu, extension, draws, origins.ToArray(), skipNode: true));
        output.WriteLine($"{extension}: skipped top-level subtree rejected by the parity law.");
    }

    private void Compare(GpuDeviceServices gpu, string extension, SdfMeshDraw[] draws, Vector4[] origins, bool skipNode = false) {
        using var scene = SdfMeshIrradianceScene.Build(draws);
        var placements = new Dictionary<SdfMesh, SdfMeshRegionMesh>();
        var layout = SdfMeshRegion.Plan(draws, placements);
        var meshes = new uint[Math.Max(1, layout.Words)];
        SdfMeshRegion.Write(draws, placements, layout, Enumerable.Repeat(1u, draws.Length).ToArray(), meshes);
        var hierarchy = scene.ExportGpu(placements);
        if (skipNode) { hierarchy[0] = hierarchy[hierarchy[0] + 6] >> SdfMeshProbeTrace.NodeCountBits; }
        var (result, identities) = Run(gpu, extension, hierarchy, meshes, origins);
        var expected = new uint[SdfMeshProbeDepth.HitWords];
        var hits = 0;
        var backFaces = 0;
        var ties = 0;
        var maximumError = 0f;
        var maximumBound = 0f;
        var sceneScale = draws.SelectMany(draw => draw.Mesh.Positions.ToArray().Select(p => Vector3.Transform(p, draw.ObjectToWorld).Length())).DefaultIfEmpty(1).Max();
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
                var point = new Vector3(origin.X, origin.Y, origin.Z);
                var direction = SdfMeshProbeDepth.RayDirections[texel];
                scene.Intersect(point, direction, origin.W, out _, out var normal, out _, out _, out var instance, out var triangle);
                var gpuInstance = unchecked((int)identities[at]);
                var gpuTriangle = unchecked((int)identities[at + 1]);
                if (cpuMiss || gpuMiss) {
                    Assert.True(cpuMiss == gpuMiss, label);
                    Assert.Equal(instance, gpuInstance);
                    Assert.Equal(triangle, gpuTriangle);
                    Assert.Equal(expected[word + 1], result[at + 1]);
                    continue;
                }
                hits++;
                var scale = MathF.Max(sceneScale, MathF.Max(origin.W, point.Length()));
                var tolerance = Roundoff * scale / MathF.Abs(Vector3.Dot(normal, direction));
                maximumBound = MathF.Max(maximumBound, tolerance);
                maximumError = MathF.Max(maximumError, MathF.Abs(cpu - device));
                Assert.True(MathF.Abs(cpu - device) <= tolerance, $"{label}, bound {tolerance}");
                Assert.True(Vector3.Distance(point + direction * MathF.Abs(cpu), point + direction * MathF.Abs(device)) <= tolerance, label);
                var packed = expected[word + 1];
                var signed = cpu;
                if (instance != gpuInstance || triangle != gpuTriangle) {
                    Assert.InRange(gpuInstance, 0, draws.Length - 1);
                    var draw = draws[gpuInstance];
                    Assert.InRange(gpuTriangle, 0, draw.Mesh.TriangleCount - 1);
                    // Reuse the CPU tracer on the claimed triangle alone: a close distance cannot excuse an
                    // unrelated identity, a missed triangle or an incorrect normal/material word.
                    var candidateMesh = new SdfMesh(positions: draw.Mesh.Positions.ToArray(), indices: draw.Mesh.Indices.Slice(3 * gpuTriangle, 3).ToArray());
                    using var candidateScene = SdfMeshIrradianceScene.Build([draw with { Mesh = candidateMesh }]);
                    var candidate = new uint[SdfMeshProbeDepth.HitWords];
                    SdfMeshProbeDepth.TraceHits(candidateScene, point, origin.W, candidate, out _);
                    signed = BitConverter.UInt32BitsToSingle(candidate[word]);
                    Assert.True(float.IsFinite(signed) && MathF.Abs(MathF.Abs(signed) - MathF.Abs(cpu)) <= tolerance, $"{label}: invalid tie {instance}/{triangle} vs {gpuInstance}/{gpuTriangle}");
                    packed = candidate[word + 1];
                    ties++;
                }
                Assert.Equal(signed < 0, device < 0);
                Assert.True(packed == result[at + 1], $"{label}: packed CPU {packed:x8}, GPU {result[at + 1]:x8}");
            }
        }
        if (draws.Length > 0) { Assert.True(hits > 0 && backFaces > 0); }
        Assert.True(ties <= origins.Length * SdfMeshProbeDepth.HitCount / 1000, $"Near ties must be below 0.1% of rays: {ties}");
        output.WriteLine($"{extension}: {origins.Length * SdfMeshProbeDepth.HitCount} rays, {hits} hits, {backFaces} backs, {ties} ties, max error {maximumError:R} m, max bound {maximumBound:R} m.");
    }

    private static (uint[] Hits, uint[] Identities) Run(GpuDeviceServices gpu, string extension, uint[] scene, uint[] meshes, Vector4[] origins) {
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
        using var identities = gpu.BufferFactory.CreateDeviceLocal(name: default, sizeBytes: bytes, usage: GpuBufferUsage.Storage);
        using var readback = gpu.BufferFactory.CreateReadback(name: default, sizeBytes: 2 * bytes);
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
            Bind(SdfKernelInterfaces.ProbeIdentitiesWritten, identities);
            var recorder = gpu.Recorder;
            var command = commands.CommandBufferHandle;
            recorder.BeginCommandBuffer(command);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: output.BufferHandle,
                sourceAccessMask: GpuAccess.None, sourceStageMask: GpuStage.TopOfPipe,
                destinationAccessMask: GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: identities.BufferHandle,
                sourceAccessMask: GpuAccess.None, sourceStageMask: GpuStage.TopOfPipe,
                destinationAccessMask: GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
            recorder.BindPipeline(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, pipelineHandle: pipeline.Handle);
            recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, descriptorSetHandle: set, group: (uint)ShaderInterfaceGroup.Pass, pipelineLayoutHandle: pipeline.LayoutHandle);
            recorder.Dispatch(commandBufferHandle: command, groupCountX: (uint)((origins.Length * SdfMeshProbeDepth.HitCount + SdfMeshProbeTrace.GroupSize - 1) / SdfMeshProbeTrace.GroupSize), groupCountY: 1, groupCountZ: 1);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: output.BufferHandle,
                sourceAccessMask: GpuAccess.ShaderWrite, sourceStageMask: GpuStage.ComputeShader,
                destinationAccessMask: GpuAccess.TransferRead, destinationStageMask: GpuStage.Transfer);
            recorder.CopyBuffer(commandBufferHandle: command, sourceBufferHandle: output.BufferHandle, destinationBufferHandle: readback.BufferHandle, sizeBytes: bytes);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: identities.BufferHandle,
                sourceAccessMask: GpuAccess.ShaderWrite, sourceStageMask: GpuStage.ComputeShader,
                destinationAccessMask: GpuAccess.TransferRead, destinationStageMask: GpuStage.Transfer);
            recorder.CopyBuffer(commandBufferHandle: command, sourceBufferHandle: identities.BufferHandle, destinationBufferHandle: readback.BufferHandle, sizeBytes: bytes, destinationOffsetBytes: bytes);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: readback.BufferHandle,
                sourceAccessMask: GpuAccess.TransferWrite, sourceStageMask: GpuStage.Transfer,
                destinationAccessMask: GpuAccess.HostRead, destinationStageMask: GpuStage.Host);
            recorder.EndCommandBuffer(command);
            gpu.QueueSubmitter.SubmitAndWait([command]);
            var result = new uint[origins.Length * SdfMeshProbeDepth.HitWords * 2];
            readback.Read(MemoryMarshal.AsBytes(result.AsSpan()));
            return (result[..(result.Length / 2)], result[(result.Length / 2)..]);

            void Bind(string name, IGpuBuffer buffer) {
                var resource = SdfKernelInterfaces.ResourceOf(layout, name);
                gpu.Bindings.WriteBuffer(binding: resource.Binding, bufferHandle: buffer.BufferHandle, bufferSize: buffer.SizeBytes,
                    descriptorSetHandle: set, elementStride: resource.Member.Name is SdfKernelInterfaces.ProbeOrigins or SdfKernelInterfaces.ProbeDirections ? 16u : name == SdfKernelInterfaces.ProbeIdentitiesWritten ? 8u : 4u,
                    kind: name is SdfKernelInterfaces.ProbeHitsWritten or SdfKernelInterfaces.ProbeIdentitiesWritten ? GpuBindingKind.ReadWriteBuffer : GpuBindingKind.ReadOnlyBuffer);
            }
        } finally { gpu.Bindings.DestroyPool(pool); }
    }

    private static SdfMesh Box() {
        Vector3[] positions = [new(-1,-1,-1),new(1,-1,-1),new(1,1,-1),new(-1,1,-1),new(-1,-1,1),new(1,-1,1),new(1,1,1),new(-1,1,1)];
        uint[] indices = [0,2,1,0,3,2,4,5,6,4,6,7,0,1,5,0,5,4,3,7,6,3,6,2,0,4,7,0,7,3,1,2,6,1,6,5];
        return new SdfMesh(indices: indices, positions: positions);
    }
}
