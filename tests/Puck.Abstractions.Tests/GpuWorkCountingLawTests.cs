using System.Reflection;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;

namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws for <see cref="GpuWorkCounting"/>: every member of every wrapped interface is passed through exactly once and
/// counted exactly once, into the kind it names and no other; the wrapped fence never reaches the backend; and a
/// wrapper keeps exactly the optional capabilities of what it wraps.
/// </summary>
public sealed class GpuWorkCountingLawTests {
    private static readonly Type[] WrappedInterfaces = [
        typeof(IGpuBufferInitializationRecorder),
        typeof(IGpuCommandRecorder),
        typeof(IGpuComputePipelineFactory),
        typeof(IGpuComputeRecorder),
        typeof(IGpuDescriptorAllocator),
        typeof(IGpuImageInitializationRecorder),
        typeof(IGpuPipelineFactory),
        typeof(IGpuQueueSubmitter),
        typeof(IGpuShaderModuleFactory),
        typeof(IGpuStorageBuffer),
        typeof(IGpuStorageBufferFactory),
        typeof(IGpuImageFactory),
        typeof(IGpuSubmissionFence),
    ];

    public static TheoryData<string> CaseKeys() {
        var data = new TheoryData<string>();

        foreach (var key in Cases().Keys) {
            data.Add(row: key);
        }

        return data;
    }
    [MemberData(memberName: nameof(CaseKeys))]
    [Theory]
    public void EachCallIsForwardedOnceAndCountedOnce(string key) {
        var (act, expected) = Cases()[key];
        var rig = Rig.Create();

        act(obj: rig);
        rig.Services.QueueSubmitter.SubmitAndWait(
            commandBufferHandles: [],
            deviceContext: rig.Gpu
        );

        Assert.Equal(
            expected: 1,
            actual: rig.Gpu.Count(key: key)
        );

        var sample = new GpuWorkSample();

        Assert.True(condition: rig.Ledger.TryReadCompleted(sample: sample));

        var submissionKinds = GpuWork.SubmissionKinds;

        for (var column = 0; (column < submissionKinds.Length); column++) {
            Assert.Equal(
                expected: Expected(expected: expected, kind: submissionKinds[column]),
                actual: sample.GetOutsidePassCount(column: column)
            );
        }

        foreach (var kind in GpuWork.LifetimeKinds) {
            Assert.True(condition: rig.Ledger.TryRead(kind: kind, value: out var value));
            Assert.Equal(
                expected: Expected(expected: expected, kind: kind),
                actual: value
            );
        }
    }
    [Fact]
    public void EveryMemberOfEveryWrappedInterfaceHasACase() {
        var keys = Cases().Keys;

        foreach (var type in WrappedInterfaces) {
            foreach (var group in type.GetMethods(bindingAttr: BindingFlags.Instance | BindingFlags.Public).GroupBy(keySelector: method => MemberName(method: method))) {
                var prefix = $"{type.Name}.{group.Key}";
                var covered = keys.Count(predicate: key => ((key == prefix) || key.StartsWith(comparisonType: StringComparison.Ordinal, value: $"{prefix}(")));

                Assert.True(
                    condition: (covered == group.Count()),
                    userMessage: $"{prefix} has {group.Count()} overloads and {covered} cases."
                );
            }
        }
    }
    [Fact]
    public void FenceIsUnwrappedBeforeItReachesTheBackend() {
        var rig = Rig.Create();
        var fence = rig.Services.QueueSubmitter.CreateSubmissionFence(deviceContext: rig.Gpu);

        Assert.IsNotType<FakeGpu.FakeFence>(@object: fence);
        rig.Services.QueueSubmitter.Submit(
            commandBufferHandles: [],
            deviceContext: rig.Gpu,
            fence: fence
        );
        Assert.IsType<FakeGpu.FakeFence>(@object: rig.Gpu.LastSubmittedFence);
        Assert.True(condition: ((FakeGpu.FakeFence)rig.Gpu.LastSubmittedFence!).Armed);
    }
    [Fact]
    public void CountingServicesPassUncountedMembersThrough() {
        var rig = Rig.Create();

        Assert.Same(
            expected: rig.Gpu,
            actual: rig.Services.CommandPoolFactory
        );
        Assert.Same(
            expected: rig.Gpu,
            actual: rig.Services.SurfaceTransferFactory
        );
    }
    [Fact]
    public void WrappingACountingWrapperIsRefused() {
        var rig = Rig.Create();

        _ = Assert.Throws<ArgumentException>(testCode: () => GpuWorkCounting.Wrap(ledger: rig.Ledger, services: rig.Services));
        _ = Assert.Throws<ArgumentException>(testCode: () => GpuWorkCounting.Wrap(ledger: rig.Ledger, recorder: rig.Services.ComputeRecorder));
        _ = Assert.Throws<ArgumentException>(testCode: () => GpuWorkCounting.Wrap(ledger: rig.Ledger, recorder: rig.Graphics));
        _ = Assert.Throws<ArgumentException>(testCode: () => GpuWorkCounting.Wrap(allocator: rig.Services.DescriptorAllocator, ledger: rig.Ledger));
        _ = Assert.Throws<ArgumentException>(testCode: () => GpuWorkCounting.Wrap(ledger: rig.Ledger, submitter: rig.Services.QueueSubmitter));
        _ = Assert.Throws<ArgumentException>(testCode: () => GpuWorkCounting.Wrap(factory: rig.Services.StorageBufferFactory, ledger: rig.Ledger));
        _ = Assert.Throws<ArgumentException>(testCode: () => GpuWorkCounting.Wrap(factory: rig.Pipelines, ledger: rig.Ledger));
    }
    [Fact]
    public void ComputeRecorderWrapperKeepsExactlyTheClearCapabilitiesOfItsInner() {
        var ledger = new GpuWorkLedger(
            framesInFlight: 1,
            name: "gpu.test"
        );
        IGpuComputeRecorder[] inners = [new BareRecorder(), new ImageClearRecorder(), new BufferClearRecorder(), new BothClearRecorder()];

        foreach (var inner in inners) {
            var wrapped = GpuWorkCounting.Wrap(
                ledger: ledger,
                recorder: inner
            );

            Assert.Equal(
                actual: (wrapped is IGpuImageInitializationRecorder),
                expected: (inner is IGpuImageInitializationRecorder)
            );
            Assert.Equal(
                actual: (wrapped is IGpuBufferInitializationRecorder),
                expected: (inner is IGpuBufferInitializationRecorder)
            );
        }
    }

    private static Dictionary<string, (Action<Rig> Act, (WorkKind Kind, long Amount)[] Expected)> Cases() {
        (WorkKind, long)[] none = [];

        return new(comparer: StringComparer.Ordinal) {
            ["IGpuBufferInitializationRecorder.ClearStorageBuffer"] = (rig => ((IGpuBufferInitializationRecorder)rig.Services.ComputeRecorder).ClearStorageBuffer(bufferHandle: 3, commandBufferHandle: 2, deviceHandle: 1, sizeBytes: 16UL), [(GpuWork.Clears, 1L)]),
            ["IGpuCommandRecorder.BeginCommandBuffer"] = (rig => rig.Graphics.BeginCommandBuffer(commandBufferHandle: 2, deviceHandle: 1), [(GpuWork.CommandBuffers, 1L)]),
            ["IGpuCommandRecorder.BeginDebugGroup"] = (rig => rig.Graphics.BeginDebugGroup(commandBufferHandle: 2, deviceHandle: 1, label: "pass"), none),
            ["IGpuCommandRecorder.BeginRenderPass"] = (rig => rig.Graphics.BeginRenderPass(commandBufferHandle: 2, deviceHandle: 1, framebuffer: null!), [(GpuWork.RenderPasses, 1L)]),
            ["IGpuCommandRecorder.BindDescriptorSet"] = (rig => rig.Graphics.BindDescriptorSet(commandBufferHandle: 2, descriptorSetHandle: 4, deviceHandle: 1, pipelineLayoutHandle: 3), [(GpuWork.DescriptorSetBinds, 1L)]),
            ["IGpuCommandRecorder.BindGraphicsPipeline"] = (rig => rig.Graphics.BindGraphicsPipeline(commandBufferHandle: 2, deviceHandle: 1, pipelineHandle: 3), [(GpuWork.PipelineBinds, 1L)]),
            ["IGpuCommandRecorder.BindIndexBuffer"] = (rig => rig.Graphics.BindIndexBuffer(bufferHandle: 3, commandBufferHandle: 2, deviceHandle: 1, format: GpuIndexFormat.UInt16, offsetBytes: 24, sizeBytes: 12), none),
            ["IGpuCommandRecorder.BindVertexBuffer"] = (rig => rig.Graphics.BindVertexBuffer(bufferHandle: 3, commandBufferHandle: 2, deviceHandle: 1, sizeBytes: 24, strideBytes: 8), none),
            ["IGpuCommandRecorder.Draw"] = (rig => rig.Graphics.Draw(commandBufferHandle: 2, deviceHandle: 1, parameters: new GpuDrawParameters(instanceCount: 1, vertexCount: 3)), [(GpuWork.Draws, 1L)]),
            ["IGpuCommandRecorder.DrawIndexed"] = (rig => rig.Graphics.DrawIndexed(commandBufferHandle: 2, deviceHandle: 1, indexCount: 6), [(GpuWork.Draws, 1L)]),
            ["IGpuCommandRecorder.EndCommandBuffer"] = (rig => rig.Graphics.EndCommandBuffer(commandBufferHandle: 2, deviceHandle: 1), none),
            ["IGpuCommandRecorder.EndDebugGroup"] = (rig => rig.Graphics.EndDebugGroup(commandBufferHandle: 2, deviceHandle: 1), none),
            ["IGpuCommandRecorder.EndRenderPass"] = (rig => rig.Graphics.EndRenderPass(commandBufferHandle: 2, deviceHandle: 1), none),
            ["IGpuCommandRecorder.PushConstants"] = (rig => rig.Graphics.PushConstants(commandBufferHandle: 2, data: new byte[12], deviceHandle: 1, offset: 0, pipelineLayoutHandle: 3, stageFlags: GpuShaderStage.Fragment), [(GpuWork.PushConstantBytes, 12L)]),
            ["IGpuCommandRecorder.SetScissor"] = (rig => rig.Graphics.SetScissor(commandBufferHandle: 2, deviceHandle: 1, height: 4, width: 4, x: 0, y: 0), none),
            ["IGpuComputePipelineFactory.Create"] = (rig => rig.Services.ComputePipelineFactory.Create(computeShaderModule: null!, description: null!, deviceContext: rig.Gpu).Dispose(), [(GpuWork.PipelinesCreated, 1L)]),
            ["IGpuComputeRecorder.BeginCommandBuffer"] = (rig => rig.Services.ComputeRecorder.BeginCommandBuffer(commandBufferHandle: 2, deviceHandle: 1), [(GpuWork.CommandBuffers, 1L)]),
            ["IGpuComputeRecorder.BeginDebugGroup"] = (rig => rig.Services.ComputeRecorder.BeginDebugGroup(commandBufferHandle: 2, deviceHandle: 1, label: "pass"), none),
            ["IGpuComputeRecorder.BindComputeDescriptorSet"] = (rig => rig.Services.ComputeRecorder.BindComputeDescriptorSet(commandBufferHandle: 2, descriptorSetHandle: 4, deviceHandle: 1, pipelineLayoutHandle: 3), [(GpuWork.DescriptorSetBinds, 1L)]),
            ["IGpuComputeRecorder.BindComputePipeline"] = (rig => rig.Services.ComputeRecorder.BindComputePipeline(commandBufferHandle: 2, deviceHandle: 1, pipelineHandle: 3), [(GpuWork.PipelineBinds, 1L)]),
            ["IGpuComputeRecorder.Dispatch"] = (rig => rig.Services.ComputeRecorder.Dispatch(commandBufferHandle: 2, deviceHandle: 1, groupCountX: 8, groupCountY: 8, groupCountZ: 1), [(GpuWork.Dispatches, 1L)]),
            ["IGpuComputeRecorder.DispatchIndirect"] = (rig => rig.Services.ComputeRecorder.DispatchIndirect(argumentBufferHandle: 3, argumentBufferOffset: 0UL, commandBufferHandle: 2, deviceHandle: 1), [(GpuWork.IndirectDispatches, 1L)]),
            ["IGpuComputeRecorder.EndCommandBuffer"] = (rig => rig.Services.ComputeRecorder.EndCommandBuffer(commandBufferHandle: 2, deviceHandle: 1), none),
            ["IGpuComputeRecorder.EndDebugGroup"] = (rig => rig.Services.ComputeRecorder.EndDebugGroup(commandBufferHandle: 2, deviceHandle: 1), none),
            ["IGpuComputeRecorder.MemoryBarrier"] = (rig => rig.Services.ComputeRecorder.MemoryBarrier(commandBufferHandle: 2, destinationAccessMask: GpuComputeAccess.ShaderRead, destinationStageMask: GpuComputeStage.ComputeShader, deviceHandle: 1, sourceAccessMask: GpuComputeAccess.ShaderWrite, sourceStageMask: GpuComputeStage.ComputeShader), [(GpuWork.MemoryBarriers, 1L)]),
            ["IGpuComputeRecorder.PushConstants"] = (rig => rig.Services.ComputeRecorder.PushConstants(commandBufferHandle: 2, data: new byte[8], deviceHandle: 1, offset: 0, pipelineLayoutHandle: 3, stageFlags: GpuShaderStage.Compute), [(GpuWork.PushConstantBytes, 8L)]),
            ["IGpuComputeRecorder.TransitionBuffer"] = (rig => rig.Services.ComputeRecorder.TransitionBuffer(bufferHandle: 3, commandBufferHandle: 2, destinationAccessMask: GpuComputeAccess.ShaderRead, destinationStageMask: GpuComputeStage.ComputeShader, deviceHandle: 1, sourceAccessMask: GpuComputeAccess.ShaderWrite, sourceStageMask: GpuComputeStage.ComputeShader), [(GpuWork.BufferBarriers, 1L)]),
            ["IGpuComputeRecorder.TransitionImageLayout"] = (rig => rig.Services.ComputeRecorder.TransitionImageLayout(commandBufferHandle: 2, destinationAccessMask: GpuComputeAccess.ShaderRead, destinationStageMask: GpuComputeStage.ComputeShader, deviceHandle: 1, imageHandle: 3, newLayout: GpuImageLayout.General, oldLayout: GpuImageLayout.Undefined, sourceAccessMask: GpuComputeAccess.None, sourceStageMask: GpuComputeStage.TopOfPipe), [(GpuWork.ImageBarriers, 1L)]),
            ["IGpuDescriptorAllocator.AllocateSet"] = (rig => rig.Services.DescriptorAllocator.AllocateSet(descriptorSetLayoutHandle: 3, deviceHandle: 1, poolHandle: 2), [(GpuWork.DescriptorSetsCreated, 1L)]),
            ["IGpuDescriptorAllocator.CreatePool"] = (rig => rig.Services.DescriptorAllocator.CreatePool(deviceHandle: 1, sizes: new GpuDescriptorPoolSizes(CombinedImageSamplerCount: 0, MaxSets: 1, StorageBufferCount: 1, StorageImageCount: 0)), [(GpuWork.DescriptorPoolsCreated, 1L)]),
            ["IGpuDescriptorAllocator.CreateSampler"] = (rig => rig.Services.DescriptorAllocator.CreateSampler(deviceHandle: 1, filter: GpuSamplerFilter.Nearest), none),
            ["IGpuDescriptorAllocator.DestroyPool"] = (rig => rig.Services.DescriptorAllocator.DestroyPool(deviceHandle: 1, poolHandle: 2), none),
            ["IGpuDescriptorAllocator.DestroySampler"] = (rig => rig.Services.DescriptorAllocator.DestroySampler(deviceHandle: 1, samplerHandle: 2), none),
            ["IGpuDescriptorAllocator.WriteCombinedImageSampler"] = (rig => rig.Services.DescriptorAllocator.WriteCombinedImageSampler(arrayElement: 0, binding: 0, descriptorSetHandle: 4, deviceHandle: 1, imageViewHandle: 5, samplerHandle: 6), [(GpuWork.DescriptorWrites, 1L)]),
            ["IGpuDescriptorAllocator.WriteRawBuffer"] = (rig => rig.Services.DescriptorAllocator.WriteRawBuffer(binding: 0, bufferHandle: 5, bufferSize: 16UL, descriptorSetHandle: 4, deviceHandle: 1, writable: true), [(GpuWork.DescriptorWrites, 1L)]),
            ["IGpuDescriptorAllocator.WriteStorageBuffer"] = (rig => rig.Services.DescriptorAllocator.WriteStorageBuffer(binding: 0, bufferHandle: 5, bufferSize: 16UL, descriptorSetHandle: 4, deviceHandle: 1), [(GpuWork.DescriptorWrites, 1L)]),
            ["IGpuDescriptorAllocator.WriteStorageBufferReadOnly"] = (rig => rig.Services.DescriptorAllocator.WriteStorageBufferReadOnly(binding: 0, bufferHandle: 5, bufferSize: 16UL, descriptorSetHandle: 4, deviceHandle: 1), [(GpuWork.DescriptorWrites, 1L)]),
            ["IGpuDescriptorAllocator.WriteStorageBufferReadWrite"] = (rig => rig.Services.DescriptorAllocator.WriteStorageBufferReadWrite(binding: 0, bufferHandle: 5, bufferSize: 16UL, descriptorSetHandle: 4, deviceHandle: 1), [(GpuWork.DescriptorWrites, 1L)]),
            ["IGpuDescriptorAllocator.WriteStorageImage"] = (rig => rig.Services.DescriptorAllocator.WriteStorageImage(arrayElement: 0, binding: 0, descriptorSetHandle: 4, deviceHandle: 1, imageViewHandle: 5), [(GpuWork.DescriptorWrites, 1L)]),
            ["IGpuImageInitializationRecorder.ClearStorageImage"] = (rig => ((IGpuImageInitializationRecorder)rig.Services.ComputeRecorder).ClearStorageImage(commandBufferHandle: 2, deviceHandle: 1, format: GpuPixelFormat.R8G8B8A8Unorm, imageHandle: 3), [(GpuWork.Clears, 1L)]),
            ["IGpuPipelineFactory.Create"] = (rig => rig.Pipelines.Create(description: null!, deviceContext: rig.Gpu, fragmentShaderModule: null!, height: 4, renderPass: null!, vertexShaderModule: null!, width: 4).Dispose(), [(GpuWork.PipelinesCreated, 1L)]),
            ["IGpuQueueSubmitter.CreateSubmissionFence"] = (rig => rig.Services.QueueSubmitter.CreateSubmissionFence(deviceContext: rig.Gpu), none),
            ["IGpuQueueSubmitter.Submit"] = (rig => rig.Services.QueueSubmitter.Submit(commandBufferHandles: [], deviceContext: rig.Gpu), none),
            ["IGpuQueueSubmitter.Submit(fence)"] = (rig => rig.Services.QueueSubmitter.Submit(commandBufferHandles: [], deviceContext: rig.Gpu, fence: rig.Services.QueueSubmitter.CreateSubmissionFence(deviceContext: rig.Gpu)), none),
            ["IGpuQueueSubmitter.SubmitAndWait"] = (_ => { }, none),
            ["IGpuShaderModuleFactory.Create"] = (rig => rig.Services.ShaderModuleFactory.Create(bytecode: ReadOnlyMemory<byte>.Empty, deviceContext: rig.Gpu, stage: GpuShaderStage.Compute).Dispose(), [(GpuWork.ShaderModulesCreated, 1L)]),
            ["IGpuStorageBuffer.Write"] = (rig => rig.Services.StorageBufferFactory.Create(deviceContext: rig.Gpu, sizeBytes: 64UL).Write<float>(data: [1f, 2f, 3f]), [(GpuWork.BuffersCreated, 1L), (GpuWork.HostVisibleUploadBytes, 12L)]),
            ["IGpuStorageBuffer.Write(offset)"] = (rig => rig.Services.StorageBufferFactory.CreateIndirectArgs(deviceContext: rig.Gpu, sizeBytes: 64UL).Write<uint>(data: [1u, 2u], destinationOffsetBytes: 8UL), [(GpuWork.BuffersCreated, 1L), (GpuWork.HostVisibleUploadBytes, 8L)]),
            ["IGpuStorageBufferFactory.Create"] = (rig => rig.Services.StorageBufferFactory.Create(deviceContext: rig.Gpu, sizeBytes: 64UL), [(GpuWork.BuffersCreated, 1L)]),
            ["IGpuStorageBufferFactory.CreateDeviceLocal"] = (rig => rig.Services.StorageBufferFactory.CreateDeviceLocal(deviceContext: rig.Gpu, sizeBytes: 64UL), [(GpuWork.BuffersCreated, 1L)]),
            ["IGpuStorageBufferFactory.CreateDeviceLocalIndirectArgs"] = (rig => rig.Services.StorageBufferFactory.CreateDeviceLocalIndirectArgs(deviceContext: rig.Gpu, sizeBytes: 64UL), [(GpuWork.BuffersCreated, 1L)]),
            ["IGpuStorageBufferFactory.CreateIndirectArgs"] = (rig => rig.Services.StorageBufferFactory.CreateIndirectArgs(deviceContext: rig.Gpu, sizeBytes: 64UL), [(GpuWork.BuffersCreated, 1L)]),
            ["IGpuImageFactory.Create"] = (rig => rig.Services.ImageFactory.Create(deviceContext: rig.Gpu, format: GpuPixelFormat.R8G8B8A8Unorm, height: 4, usage: GpuImageUsage.Storage, width: 4), [(GpuWork.ImagesCreated, 1L)]),
            ["IGpuSubmissionFence.Dispose"] = (rig => rig.Services.QueueSubmitter.CreateSubmissionFence(deviceContext: rig.Gpu).Dispose(), none),
            ["IGpuSubmissionFence.IsSignaled"] = (rig => _ = rig.Services.QueueSubmitter.CreateSubmissionFence(deviceContext: rig.Gpu).IsSignaled, none),
            ["IGpuSubmissionFence.Wait"] = (rig => rig.Services.QueueSubmitter.CreateSubmissionFence(deviceContext: rig.Gpu).Wait(), none),
        };
    }
    private static long Expected((WorkKind Kind, long Amount)[] expected, WorkKind kind) {
        foreach (var (candidate, amount) in expected) {
            if (ReferenceEquals(objA: candidate, objB: kind)) {
                return amount;
            }
        }

        return 0L;
    }
    // A property's accessor is keyed by the property name, as the fake records it.
    private static string MemberName(MethodInfo method) =>
        ((method.IsSpecialName && method.Name.StartsWith(comparisonType: StringComparison.Ordinal, value: "get_"))
            ? method.Name[4..]
            : method.Name);

    private sealed record Rig(FakeGpu Gpu, GpuWorkLedger Ledger, IGpuComputeServices Services, IGpuCommandRecorder Graphics, IGpuPipelineFactory Pipelines) {
        public static Rig Create() {
            var gpu = new FakeGpu();
            var ledger = new GpuWorkLedger(
            framesInFlight: 2,
            name: "gpu.test"
        );

            return new Rig(
                Gpu: gpu,
                Graphics: GpuWorkCounting.Wrap(ledger: ledger, recorder: ((IGpuCommandRecorder)gpu)),
                Ledger: ledger,
                Pipelines: GpuWorkCounting.Wrap(factory: ((IGpuPipelineFactory)gpu), ledger: ledger),
                Services: GpuWorkCounting.Wrap(ledger: ledger, services: ((IGpuComputeServices)gpu))
            );
        }
    }
    private class BareRecorder : IGpuComputeRecorder {
        public void BeginCommandBuffer(nint deviceHandle, nint commandBufferHandle) { }
        public void BeginDebugGroup(nint deviceHandle, nint commandBufferHandle, string label) { }
        public void BindComputeDescriptorSet(nint deviceHandle, nint commandBufferHandle, nint pipelineLayoutHandle, nint descriptorSetHandle) { }
        public void BindComputePipeline(nint deviceHandle, nint commandBufferHandle, nint pipelineHandle) { }
        public void Dispatch(nint deviceHandle, nint commandBufferHandle, uint groupCountX, uint groupCountY, uint groupCountZ) { }
        public void DispatchIndirect(nint deviceHandle, nint commandBufferHandle, nint argumentBufferHandle, ulong argumentBufferOffset) { }
        public void EndCommandBuffer(nint deviceHandle, nint commandBufferHandle) { }
        public void EndDebugGroup(nint deviceHandle, nint commandBufferHandle) { }
        public void MemoryBarrier(nint deviceHandle, nint commandBufferHandle, GpuComputeAccess sourceAccessMask, GpuComputeAccess destinationAccessMask, GpuComputeStage sourceStageMask, GpuComputeStage destinationStageMask) { }
        public void PushConstants(nint deviceHandle, nint commandBufferHandle, nint pipelineLayoutHandle, GpuShaderStage stageFlags, uint offset, ReadOnlySpan<byte> data) { }
        public void TransitionBuffer(nint deviceHandle, nint commandBufferHandle, nint bufferHandle, GpuComputeAccess sourceAccessMask, GpuComputeAccess destinationAccessMask, GpuComputeStage sourceStageMask, GpuComputeStage destinationStageMask) { }
        public void TransitionImageLayout(nint deviceHandle, nint commandBufferHandle, nint imageHandle, GpuImageLayout oldLayout, GpuImageLayout newLayout, GpuComputeAccess sourceAccessMask, GpuComputeAccess destinationAccessMask, GpuComputeStage sourceStageMask, GpuComputeStage destinationStageMask) { }
    }
    private sealed class BothClearRecorder : BareRecorder, IGpuBufferInitializationRecorder, IGpuImageInitializationRecorder {
        public void ClearStorageBuffer(nint deviceHandle, nint commandBufferHandle, nint bufferHandle, ulong sizeBytes) { }
        public void ClearStorageImage(nint deviceHandle, nint commandBufferHandle, nint imageHandle, GpuPixelFormat format) { }
    }
    private sealed class BufferClearRecorder : BareRecorder, IGpuBufferInitializationRecorder {
        public void ClearStorageBuffer(nint deviceHandle, nint commandBufferHandle, nint bufferHandle, ulong sizeBytes) { }
    }
    private sealed class ImageClearRecorder : BareRecorder, IGpuImageInitializationRecorder {
        public void ClearStorageImage(nint deviceHandle, nint commandBufferHandle, nint imageHandle, GpuPixelFormat format) { }
    }
}
