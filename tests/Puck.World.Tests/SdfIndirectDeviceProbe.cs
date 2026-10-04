using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.World.Tests;

internal static class SdfIndirectDeviceProbe {
    public static Vector4[] Run(GpuDeviceServices services, string extension, string kernel, int resultRows,
        IReadOnlyList<SdfProgram> programs, Vector4[] rows, Vector4[]? transforms = null, uint cacheWords = 128,
        ReadOnlyMemory<byte> passValues = default, ReadOnlyMemory<byte> environment = default) {
        var skyBinding = SdfKernelInterfaces.BindingOf(SdfWorldInterfaces.IndirectParameters.Layout, SdfKernelInterfaces.SkyEnvironment);
        var counterBinding = SdfKernelInterfaces.BindingOf(SdfWorldInterfaces.IndirectParameters.Layout, ShaderWorkCounters.Buffer);
        var world = new GpuGroupLayoutDescription(ordinal: 1, bindings: [
            new GpuGroupBinding(binding: 0, kind: GpuBindingKind.ReadOnlyBuffer),
            new GpuGroupBinding(binding: 1, kind: GpuBindingKind.ReadOnlyBuffer),
            .. (environment.IsEmpty ? Array.Empty<GpuGroupBinding>() : [new GpuGroupBinding(binding: skyBinding, kind: GpuBindingKind.ReadOnlyBuffer)]),
        ]);
        var pass = new GpuGroupLayoutDescription(ordinal: 3, bindings: [
            .. (passValues.IsEmpty ? Array.Empty<GpuGroupBinding>() : [new GpuGroupBinding(binding: 0, kind: GpuBindingKind.ConstantBuffer)]),
            new GpuGroupBinding(binding: 5, kind: GpuBindingKind.ReadWriteBuffer),
            .. (environment.IsEmpty ? Array.Empty<GpuGroupBinding>() : [new GpuGroupBinding(binding: counterBinding, kind: GpuBindingKind.ReadWriteBuffer)]),
            new GpuGroupBinding(binding: 60, kind: GpuBindingKind.ReadOnlyBuffer),
            new GpuGroupBinding(binding: 61, kind: GpuBindingKind.StorageImage),
        ]);
        var description = new GpuComputePipelineDescription(Bindings: [], Layout: new GpuPipelineLayoutDescription(groups: [world, pass], pushesIndex: true, stages: GpuShaderStage.Compute),
            Name: kernel, PushConstantBinding: null);
        using var module = services.ShaderModuleFactory.Create(
            bytecode: SdfIndirectProbeBytecode.Read(extension: extension, kernel: kernel),
            stage: GpuShaderStage.Compute
        );
        using var pipeline = services.PipelineFactory.Create(computeShaderModule: module, description: description, name: default);
        using var output = services.ImageFactory.Create(format: GpuPixelFormat.R32G32B32A32Float, height: ((uint)resultRows), name: default, usage: GpuImageUsage.Storage, width: ((uint)programs.Count));
        using var readback = services.SurfaceTransferFactory.CreateReadback();
        using var commands = services.CommandPoolFactory.Create(name: default);
        using var inputs = services.BufferFactory.CreateHostVisible(data: MemoryMarshal.AsBytes(span: rows.AsSpan()), name: default, usage: GpuBufferUsage.Storage);
        var padded = new byte[((((((ulong)passValues.Length) + IGpuBindings.ConstantBufferAlignment) - 1UL) / IGpuBindings.ConstantBufferAlignment) * IGpuBindings.ConstantBufferAlignment)];
        passValues.Span.CopyTo(destination: padded);
        using var constants = (passValues.IsEmpty ? null : services.BufferFactory.CreateHostVisible(data: padded, name: default, usage: GpuBufferUsage.Uniform));
        // The shader writes this cache; it needs device-local storage, cleared before the first dispatch.
        // A host-visible upload buffer cannot supply Direct3D's unordered-access view.
        using var cache = services.BufferFactory.CreateDeviceLocal(name: default, sizeBytes: (cacheWords * sizeof(uint)), usage: GpuBufferUsage.Storage);
        using var sky = environment.IsEmpty ? null : services.BufferFactory.CreateHostVisible(data: environment.Span, name: default, usage: GpuBufferUsage.Storage);
        using var counters = environment.IsEmpty ? null : services.BufferFactory.CreateDeviceLocal(name: default,
            sizeBytes: (ulong)GpuWork.KernelKinds.Length * sizeof(ulong), usage: GpuBufferUsage.Storage);
        // Three rows per dynamic slot (position, orientation, lanes); slot zero is the identity unless the caller supplies a table.
        var slots = (transforms ?? [Vector4.Zero, new Vector4(w: 1, x: 0, y: 0, z: 0), Vector4.Zero]);
        using var transformTable = services.BufferFactory.CreateHostVisible(data: MemoryMarshal.AsBytes(span: slots.AsSpan()), name: default, usage: GpuBufferUsage.Storage);
        var buffers = new List<IGpuStorageBuffer>();
        var pool = services.Bindings.CreatePool(
            name: default,
            sizes: Enumerable.Repeat(element: GpuDescriptorPoolSizes.ForGroups(groups: [world]), count: programs.Count)
                .Aggregate(seed: GpuDescriptorPoolSizes.ForGroups(groups: [pass]), func: static (sum, sizes) => (sum + sizes))
        );

        try {
            var set = services.Bindings.AllocateSet(descriptorSetLayoutHandle: pipeline.GroupLayoutHandles[3], name: default, poolHandle: pool);

            if (constants is not null) {
                services.Bindings.WriteConstantBuffer(arrayElement: 0, binding: 0, bufferHandle: constants.BufferHandle,
                    bufferSize: constants.SizeBytes, descriptorSetHandle: set);
            }
            services.Bindings.WriteBuffer(binding: 5, bufferHandle: cache.BufferHandle, bufferSize: cache.SizeBytes, descriptorSetHandle: set, elementStride: 4, kind: GpuBindingKind.ReadWriteBuffer);
            if (counters is not null) {
                services.Bindings.WriteBuffer(binding: counterBinding, bufferHandle: counters.BufferHandle, bufferSize: counters.SizeBytes,
                    descriptorSetHandle: set, elementStride: sizeof(uint), kind: GpuBindingKind.ReadWriteBuffer);
            }
            services.Bindings.WriteBuffer(binding: 60, bufferHandle: inputs.BufferHandle, bufferSize: inputs.SizeBytes, descriptorSetHandle: set, elementStride: 16, kind: GpuBindingKind.ReadOnlyBuffer);
            services.Bindings.WriteStorageImage(arrayElement: 0, binding: 61, descriptorSetHandle: set, imageViewHandle: output.ImageViewHandle);
            var recorder = services.Recorder;
            var command = commands.CommandBufferHandle;

            recorder.BeginCommandBuffer(commandBufferHandle: command);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: cache.BufferHandle,
                sourceAccessMask: GpuAccess.None, sourceStageMask: GpuStage.TopOfPipe, destinationAccessMask: GpuAccess.TransferWrite, destinationStageMask: GpuStage.Transfer);
            recorder.ClearStorageBuffer(commandBufferHandle: command, bufferHandle: cache.BufferHandle, sizeBytes: cache.SizeBytes);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: cache.BufferHandle,
                sourceAccessMask: GpuAccess.TransferWrite, sourceStageMask: GpuStage.Transfer, destinationAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
            if (counters is not null) {
                recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: counters.BufferHandle,
                    sourceAccessMask: GpuAccess.None, sourceStageMask: GpuStage.TopOfPipe, destinationAccessMask: GpuAccess.TransferWrite, destinationStageMask: GpuStage.Transfer);
                recorder.ClearStorageBuffer(commandBufferHandle: command, bufferHandle: counters.BufferHandle, sizeBytes: counters.SizeBytes);
                recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: counters.BufferHandle,
                    sourceAccessMask: GpuAccess.TransferWrite, sourceStageMask: GpuStage.Transfer, destinationAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
            }
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
                services.Bindings.WriteBuffer(binding: 1, bufferHandle: transformTable.BufferHandle, bufferSize: transformTable.SizeBytes, descriptorSetHandle: worldSet, elementStride: 16, kind: GpuBindingKind.ReadOnlyBuffer);
                if (sky is not null) {
                    services.Bindings.WriteBuffer(binding: skyBinding, bufferHandle: sky.BufferHandle, bufferSize: sky.SizeBytes,
                        descriptorSetHandle: worldSet, elementStride: sizeof(uint) * 2, kind: GpuBindingKind.ReadOnlyBuffer);
                }
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
