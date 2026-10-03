using System.Buffers.Binary;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The generated GPU atomics carry in mixed plain and named rows; pending frames retain their own identities.</summary>
[SupportedOSPlatform("windows10.0.15063")]
[Trait("Category", "Gpu")]
public sealed class GpuWorkDetailDeviceLawTests {
    [Fact]
    public void VulkanMixedRowsCarryAndReconcileAcrossFrames() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(GpuWorkDetailDeviceLawTests));

        Verify(services: device.Services, directX: false);
    }
    [Fact]
    public void DirectXMixedRowsCarryAndReconcileAcrossFrames() {
        using var device = DirectXTestDevices.Hardware();

        Verify(services: device.Services, directX: true);
    }

    private static void Verify(GpuDeviceServices services, bool directX) {
        var shaderInterface = new ShaderInterface(name: "wd-detail", members: ShaderWorkCounters.Members);
        var interfaceLayout = shaderInterface.Layout();
        var layout = interfaceLayout.PipelineLayout(stages: GpuShaderStage.Compute);
        var source = (ShaderInterfaceHlsl.Generate(shaderInterface: shaderInterface) + """

            [numthreads(1, 1, 1)]
            void CSMain(uint3 id : SV_DispatchThreadID) {
                puckCountWork(0xFFFFFFFFu, 1u);
                puckCountWork(2u, 1u);
                puckCountDetail(0u, 0xFFFFFFFFu, 3u, 1u, 7u, 11u);
                puckCountDetail(0u, 2u, 5u, 2u, 13u, 17u);
            }
            """);
        var cache = RepositoryPaths.Resolve(relativePath: "tests/Puck.World.Tests/obj/wd-detail-device");
        var compiled = new ShaderCompiler(cacheDirectory: cache).Compile(descriptor: new ShaderCompilationRequest(
            name: "wd-detail", stages: [new ShaderStageSource(EntryPoint: "CSMain",
                Path: (cache + "/wd-detail.comp.hlsl"), Source: source, Stage: ShaderStage.Compute)]));

        Assert.True(condition: compiled.IsSuccess, userMessage: string.Join(separator: "\n", values: compiled.Diagnostics));
        var ledger = new GpuWorkLedger(framesInFlight: 2, name: "gpu.detail");

        services = GpuWorkCounting.Wrap(ledger: ledger, services: services);
        ledger.Configure(revision: 1, passLabels: ["mixed"]);
        ledger.ConfigureDetails(details: [new(Detail: "plain", Pass: 0), new(Detail: "layer", Pass: 0)]);
        using var counters = new GpuKernelCounters(buffers: services.BufferFactory, slots: 2, rows: 4, owner: "wd-detail", part: "counters");
        using var module = services.ShaderModuleFactory.Create(stage: GpuShaderStage.Compute, bytecode: (directX ? compiled.Dxil : compiled.Spirv));
        using var pipeline = services.PipelineFactory.Create(computeShaderModule: module,
            description: new GpuComputePipelineDescription(Bindings: [], Layout: layout, Name: "wd-detail", PushConstantBinding: null), name: default);
        var block = new byte[IGpuBindings.ConstantBufferAlignment];

        var passGroup = Assert.Single(collection: interfaceLayout.Groups);
        var detailOffset = passGroup.BlockMembers.Single(predicate: static member => (member.Name == ShaderWorkCounters.DetailRow)).Offset;

        BinaryPrimitives.WriteUInt32LittleEndian(destination: block.AsSpan(start: checked((int)detailOffset)), value: 2u);
        using var constants = services.BufferFactory.CreateHostVisible(data: block, name: default, usage: GpuBufferUsage.Uniform);
        using var firstCommands = services.CommandPoolFactory.Create(name: default);
        using var secondCommands = services.CommandPoolFactory.Create(name: default);
        using var firstFence = services.QueueSubmitter.CreateSubmissionFence();
        using var secondFence = services.QueueSubmitter.CreateSubmissionFence();
        var sizes = GpuDescriptorPoolSizes.ForGroups(groups: layout.Groups);
        var pool = services.Bindings.CreatePool(name: default, sizes: (sizes + sizes));

        try {
            void Frame(int slot, nint command, IGpuSubmissionFence fence) {
                var row = counters.RowOf(row: 0, slot: slot);
                var group = passGroup;
                var set = services.Bindings.AllocateSet(descriptorSetLayoutHandle: pipeline.GroupLayoutHandles[checked((int)group.Set)], poolHandle: pool, name: default);

                foreach (var binding in group.Bindings) {
                    if (binding.Kind == GpuBindingKind.ConstantBuffer) {
                        services.Bindings.WriteConstantBuffer(descriptorSetHandle: set, binding: binding.Binding, arrayElement: 0,
                            bufferHandle: constants.BufferHandle, bufferSize: constants.SizeBytes);
                    } else {
                        services.Bindings.WriteBuffer(descriptorSetHandle: set, binding: binding.Binding, bufferHandle: row.Buffer.BufferHandle,
                            bufferSize: row.Buffer.SizeBytes, kind: binding.Kind, elementStride: binding.ElementStride);
                    }
                }
                var recorder = services.Recorder;

                recorder.BeginCommandBuffer(commandBufferHandle: command);
                counters.RecordClear(commandBuffer: command, recorder: recorder, slot: slot);
                ledger.EnterPass(pass: 0);
                recorder.BindPipeline(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, pipelineHandle: pipeline.Handle);
                recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, descriptorSetHandle: set,
                    group: group.Set, pipelineLayoutHandle: pipeline.LayoutHandle);
                recorder.Dispatch(commandBufferHandle: command, groupCountX: 1, groupCountY: 1, groupCountZ: 1);
                ledger.LeavePass();
                counters.RecordCopy(commandBuffer: command, recorder: recorder, slot: slot);
                recorder.EndCommandBuffer(commandBufferHandle: command);
                ledger.ReadOnCompletion(readback: counters, slot: slot);
                services.QueueSubmitter.Submit(commandBufferHandles: [command], fence: fence);
            }
            Frame(slot: 0, command: firstCommands.CommandBufferHandle, fence: firstFence);
            ledger.ConfigureDetails(details: [new(Detail: "plain", Pass: 0), new(Detail: "layer", Pass: 0), new(Detail: "later", Pass: 0)]);
            Frame(slot: 1, command: secondCommands.CommandBufferHandle, fence: secondFence);
            firstFence.Wait();
            var sample = new GpuWorkSample();

            Assert.True(condition: ledger.TryReadCompleted(sample: sample));
            Assert.Equal(expected: 2, actual: sample.Details.Length);
            Reconciles(sample: sample);
            secondFence.Wait();
            Assert.True(condition: ledger.TryReadCompleted(sample: sample));
            Assert.Equal(expected: 3, actual: sample.Details.Length);
            Reconciles(sample: sample);
        } finally {
            firstFence.Wait();
            secondFence.Wait();
            services.Bindings.DestroyPool(poolHandle: pool);
        }
    }
    private static void Reconciles(GpuWorkSample sample) {
        for (var column = 0; (column < GpuWork.SubmissionKinds.Length); column++) {
            Assert.True(condition: sample.TryGetPassCount(column: column, pass: 0, value: out var total));
            var sum = 0L;

            for (var detail = 0; (detail < sample.Details.Length); detail++) {
                Assert.True(condition: sample.TryGetDetailCount(column: column, detail: detail, value: out var value));
                sum += value;
            }
            Assert.Equal(actual: sum, expected: total);
        }
        var stepsColumn = Array.IndexOf(array: GpuWork.SubmissionKinds.ToArray(), value: GpuWork.MarchSteps);

        Assert.True(condition: sample.TryGetPassCount(column: stepsColumn, pass: 0, value: out var steps));
        Assert.Equal(actual: steps, expected: 0x2_0000_0002L);
        Assert.True(condition: sample.TryGetDetailCount(column: stepsColumn, detail: 0, value: out var plain));
        Assert.Equal(actual: plain, expected: 0x1_0000_0001L);
        Assert.True(condition: sample.TryGetDetailCount(column: stepsColumn, detail: 1, value: out var named));
        Assert.Equal(actual: named, expected: 0x1_0000_0001L);
        foreach (var expected in new[] { (GpuWork.TexelsWritten, 10L), (GpuWork.SkyEvaluations, 3L), (GpuWork.SkyHashes, 20L), (GpuWork.SkyTextureLoads, 28L) }) {
            var column = Array.IndexOf(array: GpuWork.SubmissionKinds.ToArray(), value: expected.Item1);

            Assert.True(condition: sample.TryGetPassCount(column: column, pass: 0, value: out var value));
            Assert.Equal(actual: value, expected: expected.Item2);
        }
    }
}
