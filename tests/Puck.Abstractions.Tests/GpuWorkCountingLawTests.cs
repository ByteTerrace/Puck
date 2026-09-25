using System.Reflection;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Testing;

namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws for <see cref="GpuWorkCounting"/>: every member of every wrapped interface is passed through exactly once and
/// counted exactly once, into the kind it names and no other; and the wrapped fence never reaches the backend.
/// </summary>
public sealed class GpuWorkCountingLawTests {
    private static readonly Type[] WrappedInterfaces = [
        typeof(IGpuRecorder),
        typeof(IGpuPipelineFactory),
        typeof(IGpuBindings),
        typeof(IGpuQueueSubmitter),
        typeof(IGpuShaderModuleFactory),
        typeof(IGpuStorageBuffer),
        typeof(IGpuBufferFactory),
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
            commandBufferHandles: []
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
        var fence = rig.Services.QueueSubmitter.CreateSubmissionFence();

        Assert.IsNotType<FakeGpuDevice.Fence>(@object: fence);
        rig.Services.QueueSubmitter.Submit(
            commandBufferHandles: [],
            fence: fence
        );
        Assert.IsType<FakeGpuDevice.Fence>(@object: rig.Gpu.LastSubmittedFence);
        Assert.True(condition: ((FakeGpuDevice.Fence)rig.Gpu.LastSubmittedFence!).Armed);
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
            actual: rig.Services.RenderPassFactory
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
        _ = Assert.Throws<ArgumentException>(testCode: () => GpuWorkCounting.Wrap(ledger: rig.Ledger, recorder: rig.Services.Recorder));
        _ = Assert.Throws<ArgumentException>(testCode: () => GpuWorkCounting.Wrap(bindings: rig.Services.Bindings, ledger: rig.Ledger));
        _ = Assert.Throws<ArgumentException>(testCode: () => GpuWorkCounting.Wrap(ledger: rig.Ledger, submitter: rig.Services.QueueSubmitter));
        _ = Assert.Throws<ArgumentException>(testCode: () => GpuWorkCounting.Wrap(factory: rig.Services.BufferFactory, ledger: rig.Ledger));
        _ = Assert.Throws<ArgumentException>(testCode: () => GpuWorkCounting.Wrap(factory: rig.Pipelines, ledger: rig.Ledger));
    }

    private static Dictionary<string, (Action<Rig> Act, (WorkKind Kind, long Amount)[] Expected)> Cases() {
        (WorkKind, long)[] none = [];

        return new(comparer: StringComparer.Ordinal) {
            ["IGpuRecorder.BeginCommandBuffer"] = (rig => rig.Services.Recorder.BeginCommandBuffer(commandBufferHandle: 2), [(GpuWork.CommandBuffers, 1L)]),
            ["IGpuRecorder.EndCommandBuffer"] = (rig => rig.Services.Recorder.EndCommandBuffer(commandBufferHandle: 2), none),
            ["IGpuRecorder.BeginDebugGroup"] = (rig => rig.Services.Recorder.BeginDebugGroup(commandBufferHandle: 2, label: "pass"), none),
            ["IGpuRecorder.EndDebugGroup"] = (rig => rig.Services.Recorder.EndDebugGroup(commandBufferHandle: 2), none),
            ["IGpuRecorder.BeginRenderPass"] = (rig => rig.Services.Recorder.BeginRenderPass(commandBufferHandle: 2, framebuffer: null!), [(GpuWork.RenderPasses, 1L)]),
            ["IGpuRecorder.EndRenderPass"] = (rig => rig.Services.Recorder.EndRenderPass(commandBufferHandle: 2), none),
            ["IGpuRecorder.BindPipeline"] = (rig => rig.Services.Recorder.BindPipeline(bindPoint: GpuBindPoint.Compute, commandBufferHandle: 2, pipelineHandle: 3), [(GpuWork.PipelineBinds, 1L)]),
            ["IGpuRecorder.BindDescriptorSet"] = (rig => rig.Services.Recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Graphics, commandBufferHandle: 2, descriptorSetHandle: 4, group: 1, pipelineLayoutHandle: 3), [(GpuWork.DescriptorSetBinds, 1L)]),
            ["IGpuRecorder.PushConstants"] = (rig => rig.Services.Recorder.PushConstants(bindPoint: GpuBindPoint.Graphics, commandBufferHandle: 2, data: new byte[12], offset: 0, pipelineLayoutHandle: 3, stageFlags: GpuShaderStage.Fragment), [(GpuWork.PushConstantBytes, 12L)]),
            ["IGpuRecorder.BindVertexBuffer"] = (rig => rig.Services.Recorder.BindVertexBuffer(bufferHandle: 3, commandBufferHandle: 2, sizeBytes: 24, strideBytes: 8), none),
            ["IGpuRecorder.BindIndexBuffer"] = (rig => rig.Services.Recorder.BindIndexBuffer(bufferHandle: 3, commandBufferHandle: 2, format: GpuIndexFormat.UInt16, offsetBytes: 24, sizeBytes: 12), none),
            ["IGpuRecorder.SetScissor"] = (rig => rig.Services.Recorder.SetScissor(commandBufferHandle: 2, rect: GpuPixelRect.Covering(height: 4, width: 4)), none),
            ["IGpuRecorder.Draw"] = (rig => rig.Services.Recorder.Draw(commandBufferHandle: 2, parameters: new GpuDrawParameters(instanceCount: 1, vertexCount: 3)), [(GpuWork.Draws, 1L)]),
            ["IGpuRecorder.DrawIndexed"] = (rig => rig.Services.Recorder.DrawIndexed(commandBufferHandle: 2, indexCount: 6), [(GpuWork.Draws, 1L)]),
            ["IGpuRecorder.Dispatch"] = (rig => rig.Services.Recorder.Dispatch(commandBufferHandle: 2, groupCountX: 8, groupCountY: 8, groupCountZ: 1), [(GpuWork.Dispatches, 1L)]),
            ["IGpuRecorder.DispatchIndirect"] = (rig => rig.Services.Recorder.DispatchIndirect(argumentBufferHandle: 3, argumentBufferOffset: 0UL, commandBufferHandle: 2), [(GpuWork.IndirectDispatches, 1L)]),
            ["IGpuRecorder.ClearStorageImage"] = (rig => rig.Services.Recorder.ClearStorageImage(commandBufferHandle: 2, format: GpuPixelFormat.R8G8B8A8Unorm, imageHandle: 3), [(GpuWork.Clears, 1L)]),
            ["IGpuRecorder.ClearStorageBuffer"] = (rig => rig.Services.Recorder.ClearStorageBuffer(bufferHandle: 3, commandBufferHandle: 2, sizeBytes: 16UL), [(GpuWork.Clears, 1L)]),
            ["IGpuRecorder.TransitionImageLayout"] = (rig => rig.Services.Recorder.TransitionImageLayout(commandBufferHandle: 2, destinationAccessMask: GpuAccess.ShaderRead, destinationStageMask: GpuStage.ComputeShader, imageHandle: 3, newLayout: GpuImageLayout.General, oldLayout: GpuImageLayout.Undefined, sourceAccessMask: GpuAccess.None, sourceStageMask: GpuStage.TopOfPipe), [(GpuWork.ImageBarriers, 1L)]),
            ["IGpuRecorder.MemoryBarrier"] = (rig => rig.Services.Recorder.MemoryBarrier(commandBufferHandle: 2, destinationAccessMask: GpuAccess.ShaderRead, destinationStageMask: GpuStage.ComputeShader, sourceAccessMask: GpuAccess.ShaderWrite, sourceStageMask: GpuStage.ComputeShader), [(GpuWork.MemoryBarriers, 1L)]),
            ["IGpuRecorder.TransitionBuffer"] = (rig => rig.Services.Recorder.TransitionBuffer(bufferHandle: 3, commandBufferHandle: 2, destinationAccessMask: GpuAccess.ShaderRead, destinationStageMask: GpuStage.ComputeShader, sourceAccessMask: GpuAccess.ShaderWrite, sourceStageMask: GpuStage.ComputeShader), [(GpuWork.BufferBarriers, 1L)]),
            ["IGpuPipelineFactory.Create(compute)"] = (rig => rig.Services.PipelineFactory.Create(computeShaderModule: null!, description: null!).Dispose(), [(GpuWork.PipelinesCreated, 1L)]),
            ["IGpuBindings.AllocateSet"] = (rig => rig.Services.Bindings.AllocateSet(descriptorSetLayoutHandle: 3, poolHandle: 2), [(GpuWork.DescriptorSetsCreated, 1L)]),
            ["IGpuBindings.CanAdmit"] = (rig => _ = rig.Services.Bindings.CanAdmit(owner: "candidate", pools: [], refusal: out _), none),
            ["IGpuBindings.CreatePool"] = (rig => rig.Services.Bindings.CreatePool(sizes: new GpuDescriptorPoolSizes(CombinedImageSamplerCount: 0, MaxSets: 1, StorageBufferCount: 1, StorageImageCount: 0)), [(GpuWork.DescriptorPoolsCreated, 1L)]),
            ["IGpuBindings.CreateSampler"] = (rig => rig.Services.Bindings.CreateSampler(filter: GpuSamplerFilter.Nearest), none),
            ["IGpuBindings.DestroyPool"] = (rig => rig.Services.Bindings.DestroyPool(poolHandle: 2), none),
            ["IGpuBindings.DestroySampler"] = (rig => rig.Services.Bindings.DestroySampler(samplerHandle: 2), none),
            ["IGpuBindings.WriteCombinedImageSampler"] = (rig => rig.Services.Bindings.WriteCombinedImageSampler(arrayElement: 0, binding: 0, descriptorSetHandle: 4, imageViewHandle: 5, samplerHandle: 6), [(GpuWork.DescriptorWrites, 1L)]),
            ["IGpuBindings.WriteBuffer"] = (rig => rig.Services.Bindings.WriteBuffer(binding: 0, bufferHandle: 5, bufferSize: 16UL, descriptorSetHandle: 4, elementStride: 0, kind: GpuBindingKind.ReadWriteBuffer), [(GpuWork.DescriptorWrites, 1L)]),
            ["IGpuBindings.HeapReleaseRevision"] = (rig => _ = rig.Services.Bindings.HeapReleaseRevision, none),
            ["IGpuBindings.WriteConstantBuffer"] = (rig => rig.Services.Bindings.WriteConstantBuffer(arrayElement: 0, binding: 0, bufferHandle: 5, bufferSize: 256UL, descriptorSetHandle: 4), [(GpuWork.DescriptorWrites, 1L)]),
            ["IGpuBindings.WriteSampledImage"] = (rig => rig.Services.Bindings.WriteSampledImage(arrayElement: 0, binding: 1, descriptorSetHandle: 4, imageViewHandle: 5), [(GpuWork.DescriptorWrites, 1L)]),
            ["IGpuBindings.WriteSampler"] = (rig => rig.Services.Bindings.WriteSampler(arrayElement: 0, binding: 2, descriptorSetHandle: 4, samplerHandle: 6), [(GpuWork.DescriptorWrites, 1L)]),
            ["IGpuBindings.WriteStorageImage"] = (rig => rig.Services.Bindings.WriteStorageImage(arrayElement: 0, binding: 0, descriptorSetHandle: 4, imageViewHandle: 5), [(GpuWork.DescriptorWrites, 1L)]),
            ["IGpuPipelineFactory.Create(graphics)"] = (rig => rig.Pipelines.Create(description: null!, fragmentShaderModule: null!, renderPass: null!, vertexShaderModule: null!).Dispose(), [(GpuWork.PipelinesCreated, 1L)]),
            ["IGpuQueueSubmitter.CreateSubmissionFence"] = (rig => rig.Services.QueueSubmitter.CreateSubmissionFence(), none),
            ["IGpuQueueSubmitter.Submit"] = (rig => rig.Services.QueueSubmitter.Submit(commandBufferHandles: []), none),
            ["IGpuQueueSubmitter.Submit(fence)"] = (rig => rig.Services.QueueSubmitter.Submit(commandBufferHandles: [], fence: rig.Services.QueueSubmitter.CreateSubmissionFence()), none),
            ["IGpuQueueSubmitter.SubmitAndWait"] = (_ => { }, none),
            ["IGpuShaderModuleFactory.Create"] = (rig => rig.Services.ShaderModuleFactory.Create(bytecode: ReadOnlyMemory<byte>.Empty, stage: GpuShaderStage.Compute).Dispose(), [(GpuWork.ShaderModulesCreated, 1L)]),
            ["IGpuStorageBuffer.Write"] = (rig => rig.Services.BufferFactory.CreateHostVisible(sizeBytes: 64UL, usage: GpuBufferUsage.Storage).Write<float>(data: [1f, 2f, 3f]), [(GpuWork.BuffersCreated, 1L), (GpuWork.HostVisibleUploadBytes, 12L)]),
            ["IGpuStorageBuffer.Write(offset)"] = (rig => rig.Services.BufferFactory.CreateHostVisible(sizeBytes: 64UL, usage: GpuBufferUsage.Storage | GpuBufferUsage.Indirect).Write<uint>(data: [1u, 2u], destinationOffsetBytes: 8UL), [(GpuWork.BuffersCreated, 1L), (GpuWork.HostVisibleUploadBytes, 8L)]),
            ["IGpuBufferFactory.CreateHostVisible"] = (rig => rig.Services.BufferFactory.CreateHostVisible(sizeBytes: 64UL, usage: GpuBufferUsage.Storage), [(GpuWork.BuffersCreated, 1L)]),
            ["IGpuBufferFactory.CreateHostVisibleDeviceLocal"] = (rig => rig.Services.BufferFactory.CreateHostVisibleDeviceLocal(sizeBytes: 64UL, usage: GpuBufferUsage.Storage), [(GpuWork.BuffersCreated, 1L)]),
            ["IGpuBufferFactory.CreateHostVisible(data)"] = (rig => rig.Services.BufferFactory.CreateHostVisible(data: [1, 2, 3, 4], usage: GpuBufferUsage.Vertex), [(GpuWork.BuffersCreated, 1L), (GpuWork.HostVisibleUploadBytes, 4L)]),
            ["IGpuBufferFactory.CreateDeviceLocal"] = (rig => rig.Services.BufferFactory.CreateDeviceLocal(sizeBytes: 64UL, usage: GpuBufferUsage.Storage), [(GpuWork.BuffersCreated, 1L)]),
            ["IGpuImageFactory.Create"] = (rig => rig.Services.ImageFactory.Create(format: GpuPixelFormat.R8G8B8A8Unorm, height: 4, usage: GpuImageUsage.Storage, width: 4), [(GpuWork.ImagesCreated, 1L)]),
            ["IGpuSubmissionFence.Dispose"] = (rig => rig.Services.QueueSubmitter.CreateSubmissionFence().Dispose(), none),
            ["IGpuSubmissionFence.IsSignaled"] = (rig => _ = rig.Services.QueueSubmitter.CreateSubmissionFence().IsSignaled, none),
            ["IGpuSubmissionFence.Wait"] = (rig => rig.Services.QueueSubmitter.CreateSubmissionFence().Wait(), none),
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

    private sealed record Rig(FakeGpuDevice Gpu, GpuWorkLedger Ledger, GpuDeviceServices Services, IGpuPipelineFactory Pipelines) {
        public static Rig Create() {
            var gpu = new FakeGpuDevice(countCalls: true, holdFences: true);
            var ledger = new GpuWorkLedger(
            framesInFlight: 2,
            name: "gpu.test"
        );

            return new Rig(
                Gpu: gpu,
                Ledger: ledger,
                Pipelines: GpuWorkCounting.Wrap(factory: ((IGpuPipelineFactory)gpu), ledger: ledger),
                Services: GpuWorkCounting.Wrap(ledger: ledger, services: gpu.Services)
            );
        }
    }
}
