using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;

namespace Puck.World.Tests;

internal static class SdfIndirectDeviceProbe {
    public static Vector4[] Run(GpuDeviceServices services, string extension, string kernel, int resultRows,
        IReadOnlyList<SdfProgram> programs, Vector4[] rows) {
        var world = new GpuGroupLayoutDescription(ordinal: 1, bindings: [
            new GpuGroupBinding(binding: 0, kind: GpuBindingKind.ReadOnlyBuffer),
            new GpuGroupBinding(binding: 1, kind: GpuBindingKind.ReadOnlyBuffer),
        ]);
        var pass = new GpuGroupLayoutDescription(ordinal: 3, bindings: [
            new GpuGroupBinding(binding: 5, kind: GpuBindingKind.ReadWriteBuffer),
            new GpuGroupBinding(binding: 60, kind: GpuBindingKind.ReadOnlyBuffer),
            new GpuGroupBinding(binding: 61, kind: GpuBindingKind.StorageImage),
        ]);
        var description = new GpuComputePipelineDescription(Bindings: [], Layout: new GpuPipelineLayoutDescription(groups: [world, pass], pushesIndex: true, stages: GpuShaderStage.Compute),
            Name: kernel, PushConstantBinding: null);
        using var module = services.ShaderModuleFactory.Create(
            bytecode: File.ReadAllBytes(path: Path.Combine(path1: AppContext.BaseDirectory, path2: "Assets", path3: "Shaders", path4: (kernel + extension))),
            stage: GpuShaderStage.Compute
        );
        using var pipeline = services.PipelineFactory.Create(computeShaderModule: module, description: description, name: default);
        using var output = services.ImageFactory.Create(format: GpuPixelFormat.R32G32B32A32Float, height: ((uint)resultRows), name: default, usage: GpuImageUsage.Storage, width: ((uint)programs.Count));
        using var readback = services.SurfaceTransferFactory.CreateReadback();
        using var commands = services.CommandPoolFactory.Create(name: default);
        using var inputs = services.BufferFactory.CreateHostVisible(data: MemoryMarshal.AsBytes(span: rows.AsSpan()), name: default, usage: GpuBufferUsage.Storage);
        var cacheWords = new uint[128];
        using var cache = services.BufferFactory.CreateHostVisible(data: MemoryMarshal.AsBytes(span: cacheWords.AsSpan()), name: default, usage: GpuBufferUsage.Storage);
        Vector4[] identity = [Vector4.Zero, new Vector4(w: 1, x: 0, y: 0, z: 0), Vector4.Zero];
        using var transforms = services.BufferFactory.CreateHostVisible(data: MemoryMarshal.AsBytes(span: identity.AsSpan()), name: default, usage: GpuBufferUsage.Storage);
        var buffers = new List<IGpuStorageBuffer>();
        var pool = services.Bindings.CreatePool(
            name: default,
            sizes: Enumerable.Repeat(element: GpuDescriptorPoolSizes.ForGroups(groups: [world]), count: programs.Count)
                .Aggregate(seed: GpuDescriptorPoolSizes.ForGroups(groups: [pass]), func: static (sum, sizes) => (sum + sizes))
        );

        try {
            var set = services.Bindings.AllocateSet(descriptorSetLayoutHandle: pipeline.GroupLayoutHandles[3], name: default, poolHandle: pool);

            services.Bindings.WriteBuffer(binding: 5, bufferHandle: cache.BufferHandle, bufferSize: cache.SizeBytes, descriptorSetHandle: set, elementStride: 4, kind: GpuBindingKind.ReadWriteBuffer);
            services.Bindings.WriteBuffer(binding: 60, bufferHandle: inputs.BufferHandle, bufferSize: inputs.SizeBytes, descriptorSetHandle: set, elementStride: 16, kind: GpuBindingKind.ReadOnlyBuffer);
            services.Bindings.WriteStorageImage(arrayElement: 0, binding: 61, descriptorSetHandle: set, imageViewHandle: output.ImageViewHandle);
            var recorder = services.Recorder;
            var command = commands.CommandBufferHandle;

            recorder.BeginCommandBuffer(commandBufferHandle: command);
            recorder.TransitionImageLayout(commandBufferHandle: command, destinationAccessMask: GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader,
                imageHandle: output.ImageHandle, newLayout: GpuImageLayout.General, oldLayout: GpuImageLayout.Undefined, sourceAccessMask: GpuAccess.None,
                sourceStageMask: GpuStage.TopOfPipe);
            recorder.BindPipeline(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, pipelineHandle: pipeline.Handle);
            recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, descriptorSetHandle: set, group: 3, pipelineLayoutHandle: pipeline.LayoutHandle);

            for (var index = 0; (index < programs.Count); index++) {
                var buffer = services.BufferFactory.CreateHostVisible(data: MemoryMarshal.AsBytes(span: programs[index].Words), name: default, usage: GpuBufferUsage.Storage);

                buffers.Add(item: buffer);
                var worldSet = services.Bindings.AllocateSet(descriptorSetLayoutHandle: pipeline.GroupLayoutHandles[1], name: default, poolHandle: pool);

                services.Bindings.WriteBuffer(binding: 0, bufferHandle: buffer.BufferHandle, bufferSize: buffer.SizeBytes, descriptorSetHandle: worldSet, elementStride: 16, kind: GpuBindingKind.ReadOnlyBuffer);
                services.Bindings.WriteBuffer(binding: 1, bufferHandle: transforms.BufferHandle, bufferSize: transforms.SizeBytes, descriptorSetHandle: worldSet, elementStride: 16, kind: GpuBindingKind.ReadOnlyBuffer);
                recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, descriptorSetHandle: worldSet, group: 1, pipelineLayoutHandle: pipeline.LayoutHandle);
                ReadOnlySpan<uint> pushed = [((uint)index)];

                recorder.PushConstants(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, data: MemoryMarshal.AsBytes(span: pushed), offset: 0,
                    pipelineLayoutHandle: pipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
                recorder.Dispatch(commandBufferHandle: command, groupCountX: 1, groupCountY: 1, groupCountZ: 1);
                recorder.MemoryBarrier(commandBufferHandle: command, destinationAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader,
                    sourceAccessMask: GpuAccess.ShaderWrite, sourceStageMask: GpuStage.ComputeShader);
            }
            recorder.EndCommandBuffer(commandBufferHandle: command);
            services.QueueSubmitter.SubmitAndWait(commandBufferHandles: [command]);
            var result = readback.Read(bytesPerPixel: 16, format: GpuPixelFormat.R32G32B32A32Float, height: ((uint)resultRows), sourceImageHandle: output.ImageHandle,
                sourceLayout: GpuImageLayout.General, width: ((uint)programs.Count));

            return MemoryMarshal.Cast<byte, Vector4>(span: result.Span).ToArray();
        } finally {
            services.Bindings.DestroyPool(poolHandle: pool);
            foreach (var buffer in buffers) {
                buffer.Dispose();
            }
        }
    }
}
