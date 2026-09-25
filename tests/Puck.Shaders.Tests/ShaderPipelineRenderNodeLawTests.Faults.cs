using Puck.Abstractions.Gpu;

namespace Puck.Shaders.Tests;

/// <summary>
/// The partial-allocation law driven through <see cref="GpuCreationFaults"/>, the decorator the backends wrap their
/// services with, rather than the fake's own failure: every creation a replacement makes, of every kind the decorator
/// can fail, is failed in turn, and the node disposes exactly what the candidate created, keeps the installed graph
/// presenting, and installs the same replacement when it is tried again.
/// </summary>
public sealed partial class ShaderPipelineRenderNodeLawTests {
    // The fake as a device context whose services pass through creation faults, as a backend's do.
    private sealed class FaultingDevice(FakePipelineGpu gpu, GpuCreationFaults faults) : IGpuDeviceContext {
        public long AdapterLuid => gpu.AdapterLuid;
        public GpuDeviceCapabilities? Capabilities => gpu.Capabilities;
        public GpuDeviceIdentity? Identity => gpu.Identity;
        public GpuMemoryProfile MemoryProfile => gpu.MemoryProfile;
        public GpuDeviceServices Services { get; } = GpuCreationFaults.Wrap(
            faults: faults,
            services: gpu.Services
        );

        public void WaitIdle() => gpu.WaitIdle();
    }

    // A node over the faulting device with the feedback graph installed and every frame slot warm, and the faults'
    // counts cleared, so the next replacement's creations count from one.
    private static ShaderPipelineRenderNode InstalledFaultingNode(FakePipelineGpu gpu, GpuCreationFaults faults) {
        var node = new ShaderPipelineRenderNode(
            deviceContext: new FaultingDevice(
                faults: faults,
                gpu: gpu
            ),
            height: Extent,
            hostsOnDirectX: false,
            inFlightFrames: InFlight,
            name: "feedback",
            width: Extent
        );

        node.Swap(pipeline: Feedback());
        _ = node.ProduceUntilInstalled();
        Produce(
            frames: (WarmFrames - 1),
            node: node
        );
        Assert.True(condition: node.IsReady);
        faults.Disarm();

        return node;
    }

    [Fact]
    public void EveryCreationOfACandidateFaultedInTurnByTheDecoratorDisposesExactlyWhatItCreated() {
        // A replacement whose history cannot be carried creates, per kind: three images per slot (history, gray, and the
        // image the fullscreen pass draws into); two compute modules and the fullscreen pass's two; two compute
        // pipelines and a graphics one; one descriptor pool for the graph's three passes; per slot a command pool
        // for each compute pass and the fullscreen pass's barrier and draw pools; one framebuffer per slot; one render
        // pass; and no buffer. Samplers are the one creation the fake counts that the decorator cannot fail.
        var expected = new Dictionary<GpuCreationKind, long> {
            [GpuCreationKind.Pipeline] = 3L,
            [GpuCreationKind.Buffer] = 0L,
            [GpuCreationKind.Image] = (3L * InFlight),
            [GpuCreationKind.RenderPass] = 1L,
            [GpuCreationKind.Framebuffer] = InFlight,
            [GpuCreationKind.ShaderModule] = 4L,
            [GpuCreationKind.CommandPool] = (4L * InFlight),
            [GpuCreationKind.BindingsPool] = 1L,
        };

        var measuredGpu = new FakePipelineGpu();
        var measuredFaults = new GpuCreationFaults();

        using (var probe = InstalledFaultingNode(
            faults: measuredFaults,
            gpu: measuredGpu
        )) {
            var before = measuredGpu.CreationCount;

            _ = SwapAndProduce(
                gpu: measuredGpu,
                node: probe,
                pipeline: Feedback(historyFormat: "R32G32B32A32Float")
            );
            Assert.Null(@object: probe.LastSwapError);

            foreach (var kind in GpuCreationFaults.Kinds) {
                Assert.Equal(
                    actual: measuredFaults.SeenOf(kind: kind),
                    expected: expected[kind]
                );
            }

            var created = measuredGpu.CreatedObjects.Skip(count: before).ToArray();

            Assert.Equal(
                actual: expected.Values.Sum(),
                expected: created.Count(predicate: static item => (item.Kind != "sampler"))
            );
        }

        var faulted = 0;

        foreach (var kind in GpuCreationFaults.Kinds) {
            for (var nth = 1; (nth <= expected[kind]); nth++) {
                var gpu = new FakePipelineGpu();
                var faults = new GpuCreationFaults();
                using var node = InstalledFaultingNode(
                    faults: faults,
                    gpu: gpu
                );
                var installedPlan = node.Plan;
                var installed = gpu.CreatedObjects.ToArray();
                var downstream = Produce(node: node);

                faults.Arm(
                    kind: kind,
                    nth: nth
                );

                var afterRefusal = SwapAndProduce(
                    gpu: gpu,
                    node: node,
                    pipeline: Feedback(historyFormat: "R32G32B32A32Float")
                );

                // Refused by the fault, which names the creation it failed and fired once.
                var fault = Assert.IsType<GpuCreationFaultException>(@object: node.LastSwapError);

                Assert.Equal(
                    actual: fault.Kind,
                    expected: kind
                );
                Assert.Equal(
                    actual: fault.Creation,
                    expected: nth
                );
                Assert.False(condition: faults.TryGetArmed(
                    kind: kind,
                    remaining: out _
                ));
                Assert.Same(
                    actual: node.Plan,
                    expected: installedPlan
                );
                Assert.True(condition: node.IsReady);

                // Ownership: everything the candidate created before the fault was disposed exactly once, and nothing the
                // installed graph owns was touched.
                var candidate = gpu.CreatedObjects.Skip(count: installed.Length).ToArray();

                Assert.All(
                    action: static created => Assert.Equal(
                        actual: created.DisposeCount,
                        expected: 1
                    ),
                    collection: candidate
                );
                Assert.All(
                    action: static created => Assert.Equal(
                        actual: created.DisposeCount,
                        expected: 0
                    ),
                    collection: installed
                );

                // The installed graph keeps presenting, into the images a downstream reader already holds, and creates
                // nothing.
                Produce(
                    frames: WarmFrames,
                    node: node
                );
                Assert.Contains(
                    collection: installed,
                    filter: created => (created.Handle == downstream.ImageHandle)
                );
                Assert.Contains(
                    collection: installed,
                    filter: created => (created.Handle == afterRefusal.ImageHandle)
                );
                Assert.Equal(
                    actual: gpu.CreationCount,
                    expected: (installed.Length + candidate.Length)
                );

                // The same replacement tried again installs.
                _ = SwapAndProduce(
                    gpu: gpu,
                    node: node,
                    pipeline: Feedback(historyFormat: "R32G32B32A32Float")
                );
                Assert.Null(@object: node.LastSwapError);
                Assert.NotSame(
                    actual: node.Plan,
                    expected: installedPlan
                );
                faulted++;
            }
        }

        Assert.Equal(
            actual: faulted,
            expected: expected.Values.Sum()
        );
    }
}
