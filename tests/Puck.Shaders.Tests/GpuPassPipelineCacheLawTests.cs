using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;

namespace Puck.Shaders.Tests;

/// <summary>
/// Laws for <see cref="GpuPassPipelineCache"/> and its <see cref="GpuPassPipelineKey"/>: a key is equal exactly when
/// what the pipeline is created from is, so the bytecode, the description's name, its layout, a render-pass attachment's
/// format and the depth test each make another pipeline, while a push range's starting data does not; two leases on one
/// device and key share one pipeline the cache creates and counts once under <see cref="GpuPassPipelineCache.WorkSourceName"/>,
/// named from the key; and the last release disposes it, so a device loss that releases every holder empties the
/// device's entries and the next lease creates afresh.
/// </summary>
public sealed class GpuPassPipelineCacheLawTests {
    private static readonly ReadOnlyMemory<byte> Kernel = new byte[] { 0x03, 0x02, 0x23, 0x07 };

    [Fact]
    public void AKeyIsEqualExactlyWhenWhatThePipelineIsCreatedFromIs() {
        var compute = Compute();

        Assert.Equal(expected: compute, actual: Compute());
        Assert.Equal(expected: compute.GetHashCode(), actual: Compute().GetHashCode());
        Assert.NotEqual(expected: compute, actual: Compute(name: "other"));
        Assert.NotEqual(expected: compute, actual: Compute(kernel: new byte[] { 0x03, 0x02, 0x23, 0x08 }));
        Assert.NotEqual(expected: compute, actual: Compute(binding: 1));
        Assert.Equal(
            expected: Compute(push: new GpuPushConstantBinding(data: new byte[4], offset: 0, stageFlags: GpuShaderStage.Compute)),
            actual: Compute(push: new GpuPushConstantBinding(data: new byte[] { 1, 2, 3, 4 }, offset: 0, stageFlags: GpuShaderStage.Compute))
        );

        var graphics = Graphics();

        Assert.Equal(expected: graphics, actual: Graphics());
        Assert.NotEqual(expected: graphics, actual: Graphics(format: GpuPixelFormat.R16G16B16A16Float));
        Assert.NotEqual(expected: graphics, actual: Graphics(finalLayout: GpuImageLayout.ShaderReadOnly));
        Assert.NotEqual(expected: graphics, actual: Graphics(depth: true));
        Assert.NotEqual(actual: compute, expected: ((object)graphics));
        Assert.Matches(expectedRegexPattern: "^[0-9a-f]{16}$", actualString: graphics.ContentKey);
    }
    [Fact]
    public void LeasesOnOneDeviceAndKeyShareOnePipelineCreatedAndCountedOnce() {
        var cache = new GpuPassPipelineCache();
        var gpu = new FakePipelineGpu();
        var other = new FakePipelineGpu();
        var first = cache.Acquire(device: gpu, key: Graphics());
        var second = cache.Acquire(device: gpu, key: Graphics());
        var elsewhere = cache.Acquire(device: other, key: Graphics());
        var pipeline = first.Wait(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Same(expected: pipeline, actual: second.Wait(cancellationToken: TestContext.Current.CancellationToken));
        Assert.NotSame(expected: pipeline, actual: elsewhere.Wait(cancellationToken: TestContext.Current.CancellationToken));
        Assert.NotNull(@object: pipeline.RenderPass);
        Assert.Equal(expected: pipeline.Graphics!.Handle, actual: pipeline.Handle);
        Assert.Equal(expected: 2, actual: cache.SharedPipelines);
        Assert.Equal(
            expected: (2L, 4L),
            actual: (Read(kind: GpuWork.PipelinesCreated, source: cache.Work), Read(kind: GpuWork.ShaderModulesCreated, source: cache.Work))
        );
        Assert.Equal(expected: GpuPassPipelineCache.WorkSourceName, actual: cache.Work.Name);

        // Every holder on a lost device releases: the device's entry is disposed, the other device's stands, and the next
        // lease on the device creates afresh.
        first.Release();
        Assert.Equal(expected: 2, actual: cache.SharedPipelines);
        second.Release();
        Assert.Null(@object: pipeline.Graphics);
        Assert.Equal(expected: 1, actual: cache.SharedPipelines);

        var rebuilt = cache.Acquire(device: gpu, key: Graphics());

        Assert.NotSame(expected: pipeline, actual: rebuilt.Wait(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(expected: 3L, actual: Read(kind: GpuWork.PipelinesCreated, source: cache.Work));
        rebuilt.Release();
        elsewhere.Release();
        Assert.Equal(expected: 0, actual: cache.SharedPipelines);
    }

    private static GpuPassPipelineKey Compute(string name = "accumulate", ReadOnlyMemory<byte>? kernel = null, uint binding = 0, GpuPushConstantBinding? push = null) =>
        GpuPassPipelineKey.OfCompute(
            bytecode: (kernel ?? Kernel),
            description: new GpuComputePipelineDescription(
                Bindings: [new GpuComputeBinding(Binding: binding, Kind: GpuComputeBindingKind.StorageBufferReadWrite)],
                Name: name,
                PushConstantBinding: push
            )
        );
    private static GpuPassPipelineKey Graphics(GpuPixelFormat format = GpuPixelFormat.R8G8B8A8Unorm, GpuImageLayout finalLayout = GpuImageLayout.RenderTarget, bool depth = false) =>
        GpuPassPipelineKey.OfGraphics(
            description: new GpuGraphicsPipelineDescription(
                DepthCompare: (depth ? GpuDepthCompare.Less : null),
                Layout: new GpuPipelineLayoutDescription(
                    groups: [new GpuGroupLayoutDescription(
                        bindings: [new GpuGroupBinding(binding: 0, kind: GpuBindingKind.ConstantBuffer)],
                        ordinal: 0
                    )],
                    pushesIndex: false,
                    stages: GpuShaderStage.Vertex | GpuShaderStage.Fragment
                ),
                Name: "copy",
                VertexInput: new GpuVertexInputLayout(
                    Attributes: [],
                    StrideBytes: 0
                )
            ),
            fragment: Kernel,
            renderPass: new GpuRenderPassDescription(
                Colors: [new GpuColorAttachment(
                    FinalLayout: finalLayout,
                    Format: format,
                    Load: GpuAttachmentLoad.Clear,
                    Store: GpuAttachmentStore.Store
                )],
                Depth: (depth
                    ? new GpuDepthAttachment(
                        Format: GpuPixelFormat.D32Float,
                        Load: GpuAttachmentLoad.Clear,
                        Store: GpuAttachmentStore.Store
                    )
                    : null)
            ),
            vertex: Kernel
        );
    private static long Read(WorkKind kind, IWorkCounterSource source) {
        Assert.True(condition: source.TryRead(kind: kind, value: out var value));

        return value;
    }
}
