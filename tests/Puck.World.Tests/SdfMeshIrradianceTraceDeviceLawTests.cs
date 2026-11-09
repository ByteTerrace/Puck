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
        Run(gpu, extension, OverdeepScene(), [0], [new(0, 0, -2, 10)], extraCapacityWords: 64, expectFault: true);
        output.WriteLine($"{extension}: malformed overdeep hierarchy counted by the stack assertion.");
        foreach (var geometry in new[] { false, true }) {
            Run(gpu, extension, CyclicScene(geometry), [0], [new(0, 0, -2, 10)], expectFault: true);
            output.WriteLine($"{extension}: single-child {(geometry ? "geometry" : "top")} cycle terminated and counted.");
        }
    }

    private static uint[] CyclicScene(bool geometry) {
        var words = new uint[64];
        words[0] = 1;
        Box(1, -1, 1);
        if (geometry) {
            words[7] = (8u << SdfMeshProbeTrace.NodeCountBits) | 1u;
            words[8] = 32; words[9] = 16;
            words[32 + SdfMeshProbeTrace.IdentityWord] = 1;
            Box(16, -1, 1);
            Box(23, 1000, 1001);
            words[22] = 16u << SdfMeshProbeTrace.NodeCountBits;
        } else {
            words[7] = 1u << SdfMeshProbeTrace.NodeCountBits;
            Box(8, 1000, 1001);
        }
        return words;

        void Box(int node, float low, float high) {
            for (var axis = 0; axis < 3; axis++) {
                words[node + axis] = BitConverter.SingleToUInt32Bits(low);
                words[node + 3 + axis] = BitConverter.SingleToUInt32Bits(high);
            }
        }
    }

    // Bypass the builder only in this negative control. Equal child boxes keep both children pending; near-first
    // ties descend the left spine until a push would overflow. No leaf or mesh entry is reached.
    private static uint[] OverdeepScene() {
        const int Levels = SdfMeshProbeTrace.StackDepth + 1;
        var nodes = 2 * Levels + 1;
        var words = new uint[1 + nodes * SdfMeshProbeTrace.NodeWords];
        words[0] = 1;
        for (var node = 0; node < nodes; node++) {
            var at = 1 + node * SdfMeshProbeTrace.NodeWords;
            for (var axis = 0; axis < 3; axis++) {
                words[at + axis] = BitConverter.SingleToUInt32Bits(-1);
                words[at + 3 + axis] = BitConverter.SingleToUInt32Bits(1);
            }
            words[at + 6] = 1;
        }
        for (var level = 0; level < Levels; level++) {
            var node = level == 0 ? 0 : 2 * level - 1;
            var child = 1 + (2 * level + 1) * SdfMeshProbeTrace.NodeWords;
            words[1 + node * SdfMeshProbeTrace.NodeWords + 6] = (uint)child << SdfMeshProbeTrace.NodeCountBits;
        }
        return words;
    }

    private void Compare(GpuDeviceServices gpu, string extension, SdfMeshDraw[] draws, Vector4[] origins, bool skipNode = false) {
        using var scene = SdfMeshIrradianceScene.Build(draws);
        var placements = new Dictionary<SdfMesh, SdfMeshRegionMesh>();
        var layout = SdfMeshRegion.Plan(draws, placements);
        var meshes = new uint[Math.Max(1, layout.Words)];
        SdfMeshRegion.Write(draws, placements, layout, Enumerable.Repeat(1u, draws.Length).ToArray(), meshes);
        var hierarchy = scene.ExportGpu(placements);
        if (!skipNode) { CompareLightBits(gpu, extension, scene, hierarchy, meshes, origins); }
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

    // Exercise the production any-hit door through the relight kernel's actual directional-light bits.
    // Miss-only cached hits suppress shading: each bit is solely an exact segment visibility query.
    private void CompareLightBits(GpuDeviceServices gpu, string extension, SdfMeshIrradianceScene scene, uint[] hierarchy, uint[] meshes, Vector4[] origins) {
        const int LightCount = 32;
        const float Reach = 30;
        var inputs = new uint[origins.Length * SdfMeshProbeRelight.InputWords];
        var cache = new uint[origins.Length * SdfMeshProbeRelight.CacheWords];
        var work = new uint[3 + origins.Length];
        for (var probe = 0; probe < origins.Length; probe++) {
            inputs[probe * SdfMeshProbeRelight.InputWords] = 1;
            inputs[probe * SdfMeshProbeRelight.InputWords + 3] = (uint)probe;
            work[3 + probe] = (uint)probe;
            for (var ray = 0; ray < SdfMeshProbeDepth.HitCount; ray++) {
                cache[probe * SdfMeshProbeRelight.CacheWords + 2 + 2 * ray] = 0x7f800000u;
            }
        }
        var rows = new Vector4[SdfMeshProbeRelight.HeaderRows + SdfMeshProbeRelight.MaterialRows + LightCount * SdfMeshProbeRelight.LightRows];
        rows[0] = new(1, LightCount, 0, Reach);
        rows[9] = rows[10] = Vector4.One;
        for (var light = 0; light < LightCount; light++) {
            var row = SdfMeshProbeRelight.HeaderRows + SdfMeshProbeRelight.MaterialRows + light * SdfMeshProbeRelight.LightRows;
            rows[row] = new(SdfMeshProbeDepth.RayDirections[light], 0);
            rows[row + 3] = new(1, light, 1, 0);
        }
        using var owned = new GpuCreationScope();
        var layout = SdfWorldInterfaces.ProbeRelightLayout;
        var description = new GpuComputePipelineDescription(Bindings: [], Name: "sdf-probe-relight", PushConstantBinding: null,
            Layout: layout.PipelineLayout(GpuShaderStage.Compute));
        var module = owned.Own(gpu.ShaderModuleFactory.Create(stage: GpuShaderStage.Compute,
            bytecode: File.ReadAllBytes(Path.Combine(SdfKernelSet.DefaultDirectory, "sdf-probe-relight.comp" + extension))));
        var pipeline = owned.Own(gpu.PipelineFactory.Create(computeShaderModule: module, description: description, name: default));
        var combineModule = owned.Own(gpu.ShaderModuleFactory.Create(stage: GpuShaderStage.Compute,
            bytecode: File.ReadAllBytes(Path.Combine(SdfKernelSet.DefaultDirectory, "sdf-probe-combine.comp" + extension))));
        var combine = owned.Own(gpu.PipelineFactory.Create(computeShaderModule: combineModule, description: description with { Name = "sdf-probe-combine" }, name: default));
        var cacheBuffer = Host(cache);
        var workBuffer = Host(work);
        var payload = origins.Length * SdfMeshProbeRelight.ResultWords;
        var resultReset = Host(new uint[payload + 1 + (origins.Length * SdfMeshProbeRelight.Slices * SdfMeshProbeRelight.PartialWords)]);
        var resultBuffer = owned.Own(gpu.BufferFactory.CreateDeviceLocal(name: default, sizeBytes: resultReset.SizeBytes, usage: GpuBufferUsage.Storage));
        var buffers = new Dictionary<string, IGpuBuffer> {
            [SdfKernelInterfaces.ProbeScene] = Host(hierarchy),
            [SdfWorldPackage.MeshRegion] = Host(meshes),
            [SdfKernelInterfaces.ProbeOrigins] = Host(origins),
            [SdfKernelInterfaces.ProbeDirections] = Host(SdfMeshProbeDepth.RayDirections.ToArray().Select(static ray => new Vector4(ray, 0)).ToArray()),
            [SdfKernelInterfaces.ProbeLighting] = Host(rows),
            [SdfKernelInterfaces.ProbeInput] = Host(inputs),
            [SdfWorldPackage.MeshIrradiance] = Host(new uint[1]),
            [SdfKernelInterfaces.ProbeHitsWritten] = resultBuffer,
            [SdfKernelInterfaces.ProbeWorkWritten] = owned.Own(gpu.BufferFactory.CreateDeviceLocal(name: default, sizeBytes: workBuffer.SizeBytes, usage: GpuBufferUsage.Storage)),
            [SdfKernelInterfaces.ProbeWork] = workBuffer,
            [SdfKernelInterfaces.ProbeCacheWritten] = owned.Own(gpu.BufferFactory.CreateDeviceLocal(name: default, sizeBytes: cacheBuffer.SizeBytes, usage: GpuBufferUsage.Storage)),
            [SdfKernelInterfaces.ProbeCache] = cacheBuffer,
        };
        var pool = owned.Own(gpu.Bindings.CreatePool(name: default, sizes: GpuDescriptorPoolSizes.ForGroups(description.Layout!.Groups)), gpu.Bindings.DestroyPool);
        var set = gpu.Bindings.AllocateSet(descriptorSetLayoutHandle: pipeline.GroupLayoutHandles[(int)ShaderInterfaceGroup.Pass], name: default, poolHandle: pool);
        foreach (var (name, buffer) in buffers) {
            var resource = SdfKernelInterfaces.ResourceOf(layout, name);
            gpu.Bindings.WriteBuffer(set, resource.Binding, buffer.BufferHandle, buffer.SizeBytes,
                name is SdfKernelInterfaces.ProbeHitsWritten or SdfKernelInterfaces.ProbeWorkWritten or SdfKernelInterfaces.ProbeCacheWritten ? GpuBindingKind.ReadWriteBuffer : GpuBindingKind.ReadOnlyBuffer,
                name is SdfKernelInterfaces.ProbeOrigins or SdfKernelInterfaces.ProbeDirections or SdfKernelInterfaces.ProbeLighting ? 16u : 4u);
        }
        var readback = owned.Own(gpu.BufferFactory.CreateReadback(name: default, sizeBytes: resultBuffer.SizeBytes));
        var commands = owned.Own(gpu.CommandPoolFactory.Create(name: default));
        var recorder = gpu.Recorder;
        var command = commands.CommandBufferHandle;
        recorder.BeginCommandBuffer(command);
        recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: resultBuffer.BufferHandle,
            sourceAccessMask: GpuAccess.None, sourceStageMask: GpuStage.TopOfPipe,
            destinationAccessMask: GpuAccess.CopyWrite, destinationStageMask: GpuStage.Transfer);
        recorder.CopyBuffer(command, resultReset.BufferHandle, resultBuffer.BufferHandle, resultReset.SizeBytes);
        recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: resultBuffer.BufferHandle,
            sourceAccessMask: GpuAccess.CopyWrite, sourceStageMask: GpuStage.Transfer,
            destinationAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
        recorder.BindPipeline(commandBufferHandle: command, bindPoint: GpuBindPoint.Compute, pipelineHandle: pipeline.Handle);
        recorder.BindDescriptorSet(commandBufferHandle: command, bindPoint: GpuBindPoint.Compute, descriptorSetHandle: set,
            group: (uint)ShaderInterfaceGroup.Pass, pipelineLayoutHandle: pipeline.LayoutHandle);
        recorder.Dispatch(command, (uint)(origins.Length * SdfMeshProbeRelight.Slices), 1, 1);
        recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: resultBuffer.BufferHandle,
            sourceAccessMask: GpuAccess.ShaderWrite, sourceStageMask: GpuStage.ComputeShader,
            destinationAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
        recorder.BindPipeline(commandBufferHandle: command, bindPoint: GpuBindPoint.Compute, pipelineHandle: combine.Handle);
        recorder.BindDescriptorSet(commandBufferHandle: command, bindPoint: GpuBindPoint.Compute, descriptorSetHandle: set,
            group: (uint)ShaderInterfaceGroup.Pass, pipelineLayoutHandle: combine.LayoutHandle);
        recorder.Dispatch(command, (uint)origins.Length, 1, 1);
        recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: resultBuffer.BufferHandle,
            sourceAccessMask: GpuAccess.ShaderWrite, sourceStageMask: GpuStage.ComputeShader,
            destinationAccessMask: GpuAccess.TransferRead, destinationStageMask: GpuStage.Transfer);
        recorder.CopyBuffer(command, resultBuffer.BufferHandle, readback.BufferHandle, resultBuffer.SizeBytes);
        recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: readback.BufferHandle,
            sourceAccessMask: GpuAccess.CopyWrite, sourceStageMask: GpuStage.Transfer,
            destinationAccessMask: GpuAccess.HostRead, destinationStageMask: GpuStage.Host);
        recorder.EndCommandBuffer(command);
        gpu.QueueSubmitter.SubmitAndWait([command]);
        var result = new uint[resultReset.SizeBytes / 4];
        readback.Read(MemoryMarshal.AsBytes(result.AsSpan()));
        Assert.Equal(0u, result[payload]);
        for (var probe = 0; probe < origins.Length; probe++) {
            var position = new Vector3(origins[probe].X, origins[probe].Y, origins[probe].Z);
            for (var light = 0; light < LightCount; light++) {
                var visible = (result[probe * SdfMeshProbeRelight.ResultWords + 36] & (1u << light)) != 0;
                Assert.Equal(scene.Unblocked(position, SdfMeshProbeDepth.RayDirections[light], Reach), visible);
            }
        }
        output.WriteLine($"{extension}: {origins.Length * LightCount} any-hit light bits match CPU visibility exactly; zero stack overflow.");

        IGpuStorageBuffer Host<T>(T[] values) where T : unmanaged {
            var buffer = owned.Own(gpu.BufferFactory.CreateHostVisible(name: default,
                sizeBytes: (ulong)(values.Length * Marshal.SizeOf<T>()), usage: GpuBufferUsage.Storage));
            buffer.Write<T>(values);
            return buffer;
        }
    }

    private static (uint[] Hits, uint[] Identities) Run(GpuDeviceServices gpu, string extension, uint[] scene, uint[] meshes, Vector4[] origins, int extraCapacityWords = 0, bool expectFault = false) {
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
        using var output = gpu.BufferFactory.CreateDeviceLocal(name: default, sizeBytes: bytes + 4 + (ulong)extraCapacityWords * 4, usage: GpuBufferUsage.Storage);
        using var zero = gpu.BufferFactory.CreateHostVisible(name: default, sizeBytes: 4, usage: GpuBufferUsage.Storage);
        zero.Write<uint>([0]);
        using var identities = gpu.BufferFactory.CreateDeviceLocal(name: default, sizeBytes: bytes, usage: GpuBufferUsage.Storage);
        using var readback = gpu.BufferFactory.CreateReadback(name: default, sizeBytes: 2 * bytes + 4);
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
                destinationAccessMask: GpuAccess.CopyWrite, destinationStageMask: GpuStage.Transfer);
            recorder.CopyBuffer(command, zero.BufferHandle, output.BufferHandle, 4, destinationOffsetBytes: bytes);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: output.BufferHandle,
                sourceAccessMask: GpuAccess.CopyWrite, sourceStageMask: GpuStage.Transfer,
                destinationAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: identities.BufferHandle,
                sourceAccessMask: GpuAccess.None, sourceStageMask: GpuStage.TopOfPipe,
                destinationAccessMask: GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
            recorder.BindPipeline(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, pipelineHandle: pipeline.Handle);
            recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, descriptorSetHandle: set, group: (uint)ShaderInterfaceGroup.Pass, pipelineLayoutHandle: pipeline.LayoutHandle);
            recorder.Dispatch(commandBufferHandle: command, groupCountX: (uint)((origins.Length * SdfMeshProbeDepth.HitCount + SdfMeshProbeTrace.GroupSize - 1) / SdfMeshProbeTrace.GroupSize), groupCountY: 1, groupCountZ: 1);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: output.BufferHandle,
                sourceAccessMask: GpuAccess.ShaderWrite, sourceStageMask: GpuStage.ComputeShader,
                destinationAccessMask: GpuAccess.TransferRead, destinationStageMask: GpuStage.Transfer);
            recorder.CopyBuffer(commandBufferHandle: command, sourceBufferHandle: output.BufferHandle, destinationBufferHandle: readback.BufferHandle, sizeBytes: bytes + 4);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: identities.BufferHandle,
                sourceAccessMask: GpuAccess.ShaderWrite, sourceStageMask: GpuStage.ComputeShader,
                destinationAccessMask: GpuAccess.TransferRead, destinationStageMask: GpuStage.Transfer);
            recorder.CopyBuffer(commandBufferHandle: command, sourceBufferHandle: identities.BufferHandle, destinationBufferHandle: readback.BufferHandle, sizeBytes: bytes, destinationOffsetBytes: bytes + 4);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: readback.BufferHandle,
                sourceAccessMask: GpuAccess.CopyWrite, sourceStageMask: GpuStage.Transfer,
                destinationAccessMask: GpuAccess.HostRead, destinationStageMask: GpuStage.Host);
            recorder.EndCommandBuffer(command);
            gpu.QueueSubmitter.SubmitAndWait([command]);
            var words = origins.Length * SdfMeshProbeDepth.HitWords;
            var result = new uint[words * 2 + 1];
            readback.Read(MemoryMarshal.AsBytes(result.AsSpan()));
            if (expectFault) { Assert.InRange(result[words], 1u, (uint)(origins.Length * SdfMeshProbeDepth.HitCount)); }
            else { Assert.Equal(0u, result[words]); }
            return (result[..words], result[(words + 1)..]);

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
