using Puck.Abstractions.Gpu;

namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws for <see cref="GpuDescriptorHeapBudget"/>: each heap takes the size the device reports, the guaranteed minimum
/// when a Direct3D 12 runtime does not answer, and a device with no shared heap is refused by name; a candidate's pools are
/// admitted as one view range each, a pool holding no descriptor takes none, and a candidate that does not fit is refused
/// by name with its demand and leaves the heap as it found it; a check allocates nothing and refuses with
/// <see cref="GpuDescriptorHeapBudget.RefusalCode"/>; a released admission's ranges serve the next candidate;
/// more than <see cref="GpuDescriptorHeapBudget.MaxLivePools"/> live pools are refused by name; and a heap's bytes are
/// its descriptors at the device's increment.
/// </summary>
public sealed class GpuDescriptorHeapBudgetLawTests {
    private static GpuDeviceCapabilities Heap(uint views) => (GpuDeviceCapabilities.FromDirectX(
        resourceBindingTier: 3,
        rootSignatureVersion: "1.1",
        samplerHeapSize: 0,
        shaderModel: "6.6",
        viewHeapSize: 0
    ) with {
        ViewHeapSize = views,
    });
    private static GpuDescriptorPoolSizes Pool(uint sampled, uint buffers = 0, uint images = 0) => new(
        CombinedImageSamplerCount: sampled,
        MaxSets: 1,
        StorageBufferCount: buffers,
        StorageImageCount: images
    );

    [Fact]
    public void EachHeapTakesTheSizeTheDeviceReports() {
        var guaranteed = new GpuDescriptorHeapBudget(capabilities: GpuDeviceCapabilities.FromDirectX(
            resourceBindingTier: 3,
            rootSignatureVersion: "1.1",
            samplerHeapSize: 0,
            shaderModel: "6.6",
            viewHeapSize: 0
        ));
        var reported = new GpuDescriptorHeapBudget(capabilities: GpuDeviceCapabilities.FromDirectX(
            resourceBindingTier: 3,
            rootSignatureVersion: "1.1",
            samplerHeapSize: 4080,
            shaderModel: "6.6",
            viewHeapSize: 2_000_000
        ));

        Assert.Equal(
            actual: (guaranteed.ViewDescriptors, guaranteed.SamplerDescriptors, guaranteed.FreeViewDescriptors, guaranteed.LivePools),
            expected: (GpuDeviceCapabilities.DirectXMinimumViewHeapSize, GpuDeviceCapabilities.DirectXMinimumSamplerHeapSize, GpuDeviceCapabilities.DirectXMinimumViewHeapSize, 0)
        );
        Assert.Equal(
            actual: (reported.ViewDescriptors, reported.SamplerDescriptors),
            expected: (2_000_000u, 4080u)
        );
    }
    [Fact]
    public void ADeviceWithNoSharedHeapIsRefusedByName() {
        var vulkan = new GpuDeviceCapabilities(
            Backend: "vulkan",
            MaxBoundDescriptorSets: 8,
            MaxPerStageResources: 200,
            MaxPerStageSampledImages: 200,
            MaxPerStageSamplers: 200,
            MaxPerStageStorageBuffers: 200,
            MaxPerStageStorageImages: 200,
            MaxPerStageUniformBuffers: 15,
            MaxPushConstantBytes: 128,
            MaxRootSignatureWords: 0
        );

        Assert.Contains(
            actualString: Assert.Throws<ArgumentException>(testCode: () => new GpuDescriptorHeapBudget(capabilities: vulkan)).Message,
            expectedSubstring: "The vulkan device reports no shader-visible descriptor heap (views 0, samplers 0)"
        );
    }
    [Fact]
    public void ACandidateIsAdmittedWholeOrRefusedByNameAndLeavesTheHeapAsItFoundIt() {
        var heap = new GpuDescriptorHeapBudget(capabilities: Heap(views: 100));

        Assert.True(condition: heap.TryAdmit(
            admission: out var first,
            owner: "first",
            pools: [Pool(buffers: 20, images: 10, sampled: 10), Pool(sampled: 0), Pool(sampled: 30)],
            refusal: out var admitted
        ));
        Assert.Equal(
            actual: (first.Owner, admitted, heap.FreeViewDescriptors, heap.LivePools),
            expected: ("first", string.Empty, 30u, 2)
        );
        Assert.Equal(
            actual: first.Ranges,
            expected: [(0u, 40u), (40u, 30u)]
        );

        Assert.False(condition: heap.TryAdmit(
            admission: out var refused,
            owner: "second",
            pools: [Pool(sampled: 20), Pool(sampled: 20)],
            refusal: out var refusal
        ));
        Assert.Null(@object: refused);
        Assert.Contains(
            actualString: refusal,
            expectedSubstring: "'second' needs 40 view descriptors in 2 pool(s) and is refused: A range of 20 is refused"
        );
        Assert.Equal(
            actual: (heap.FreeViewDescriptors, heap.LivePools),
            expected: (30u, 2)
        );

        heap.Release(admission: first);
        Assert.True(condition: heap.TryAdmit(
            admission: out var second,
            owner: "second",
            pools: [Pool(sampled: 20), Pool(sampled: 20)],
            refusal: out _
        ));
        Assert.Equal(
            actual: second.Ranges,
            expected: [(0u, 20u), (20u, 20u)]
        );
        Assert.Equal(
            actual: (heap.FreeViewDescriptors, heap.LivePools),
            expected: (60u, 2)
        );
    }
    [Fact]
    public void ACheckAllocatesNothingAndRefusesByTheOneCode() {
        var heap = new GpuDescriptorHeapBudget(capabilities: Heap(views: 100));

        Assert.True(condition: heap.CanAdmit(
            owner: "fits",
            pools: [Pool(sampled: 60), Pool(sampled: 40)],
            refusal: out var admitted
        ));
        Assert.Equal(
            actual: (admitted, heap.FreeViewDescriptors, heap.LivePools),
            expected: (string.Empty, 100u, 0)
        );
        Assert.False(condition: heap.CanAdmit(
            owner: "over",
            pools: [Pool(sampled: 60), Pool(sampled: 41)],
            refusal: out var refusal
        ));
        Assert.StartsWith(
            actualString: refusal,
            expectedStartString: $"[{GpuDescriptorHeapBudget.RefusalCode}] 'over' needs 101 view descriptors in 2 pool(s) and is refused: "
        );
        Assert.Equal(
            actual: (heap.FreeViewDescriptors, heap.LivePools),
            expected: (100u, 0)
        );
    }
    [Fact]
    public void MoreThanTheMostLivePoolsAreRefusedByName() {
        var heap = new GpuDescriptorHeapBudget(capabilities: Heap(views: 100_000));
        var pools = Enumerable.Repeat(
            count: GpuDescriptorHeapBudget.MaxLivePools,
            element: Pool(sampled: 1)
        ).ToArray();

        Assert.True(condition: heap.TryAdmit(
            admission: out _,
            owner: "full",
            pools: pools,
            refusal: out _
        ));
        Assert.False(condition: heap.TryAdmit(
            admission: out _,
            owner: "one-more",
            pools: [Pool(sampled: 1)],
            refusal: out var refusal
        ));
        Assert.Contains(
            actualString: refusal,
            expectedSubstring: $"{GpuDescriptorHeapBudget.MaxLivePools} ranges are already live"
        );
        Assert.Equal(
            actual: heap.LivePools,
            expected: GpuDescriptorHeapBudget.MaxLivePools
        );
    }
    [Fact]
    public void AHeapsBytesAreItsDescriptorsAtTheDevicesIncrement() {
        Assert.Equal(
            actual: GpuDescriptorHeapBudget.HeapBytes(
                descriptors: GpuDeviceCapabilities.DirectXMinimumViewHeapSize,
                incrementBytes: 32
            ),
            expected: 32_000_000UL
        );
        Assert.Equal(
            actual: GpuDescriptorHeapBudget.HeapBytes(
                descriptors: uint.MaxValue,
                incrementBytes: uint.MaxValue
            ),
            expected: (((ulong)uint.MaxValue) * uint.MaxValue)
        );
    }
}
