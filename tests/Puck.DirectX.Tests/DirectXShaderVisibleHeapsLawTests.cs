using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Puck.Abstractions.Gpu;
using Puck.DirectX.Interop;
using Xunit;

namespace Puck.DirectX.Tests;

/// <summary>Laws for a Direct3D 12 device's two shader-visible descriptor heaps
/// (<see cref="DirectXShaderVisibleHeaps"/>): one pair per device, created at bring-up and recreated by
/// <see cref="DirectXDeviceContext.Recreate"/>; every pool a range of the view heap, a destroyed pool's range the one the
/// next equal pool receives; a candidate that does not fit refused whole, by name, allocating nothing; both heaps'
/// bytes counted under <c>memory.directx</c> and ended at teardown; and a storage clear recorded, submitted and
/// completed through a clear slot of the view heap with no heap of its own. Each law runs on a software (WARP) device
/// without the debug layer and skips when the host has none that meets the device floor.</summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXShaderVisibleHeapsLawTests {
    private static GpuDescriptorPoolSizes Pool(uint buffers) => new(
        CombinedImageSamplerCount: 0,
        MaxSets: 1,
        StorageBufferCount: buffers,
        StorageImageCount: 0
    );
    // The first view descriptor a pool's range holds.
    private static uint StartOf(nint pool) =>
        ((DirectXDescriptorPool)GCHandle.FromIntPtr(value: pool).Target!).Admission!.Ranges[0].Start;

    [Fact]
    public void ADeviceHasOneHeapPairCreatedOnceAndRecreatedOnRecreate() {
        var memory = new GpuDeviceMemoryWork(backend: "directx");
        using var context = WarpContext(memory: memory);
        var bindings = context.Services.Bindings;
        var first = context.DescriptorHeaps;
        var capabilities = context.Capabilities!;

        bindings.DestroyPool(poolHandle: bindings.CreatePool(sizes: Pool(buffers: 4)));
        bindings.DestroyPool(poolHandle: bindings.CreatePool(sizes: Pool(buffers: 4)));

        Assert.Same(
            actual: context.DescriptorHeaps,
            expected: first
        );
        Assert.Equal(
            actual: (first.Budget.ViewDescriptors, first.Budget.SamplerDescriptors),
            expected: (capabilities.ViewHeapSize, capabilities.SamplerHeapSize)
        );
        Assert.NotEqual(
            actual: first.ViewHeap,
            expected: 0
        );

        context.Recreate();

        var second = context.DescriptorHeaps;

        Assert.NotSame(
            actual: second,
            expected: first
        );
        Assert.Equal(
            actual: (first.ViewHeap, first.SamplerHeap),
            expected: (0, 0)
        );
        Assert.Equal(
            actual: memory.Held,
            expected: checked((long)(second.ViewHeapBytes + second.SamplerHeapBytes))
        );
    }
    [Fact]
    public void APoolIsARangeOfTheViewHeapAndADestroyedPoolsRangeServesTheNext() {
        using var context = WarpContext(memory: null);
        var bindings = context.Services.Bindings;
        var budget = context.DescriptorHeaps.Budget;
        var clears = DirectXShaderVisibleHeaps.ClearDescriptors;
        var first = bindings.CreatePool(sizes: Pool(buffers: 10));
        var second = bindings.CreatePool(sizes: Pool(buffers: 10));

        // The device's clear range sits ahead of every pool.
        Assert.Equal(
            actual: (StartOf(pool: first), StartOf(pool: second), budget.LivePools),
            expected: (clears, (clears + 10U), 3)
        );

        bindings.DestroyPool(poolHandle: first);

        var third = bindings.CreatePool(sizes: Pool(buffers: 10));

        Assert.Equal(
            actual: (StartOf(pool: third), budget.FreeViewDescriptors),
            expected: (clears, ((budget.ViewDescriptors - clears) - 20U))
        );
        bindings.DestroyPool(poolHandle: second);
        bindings.DestroyPool(poolHandle: third);
        Assert.Equal(
            actual: (budget.LivePools, budget.FreeViewDescriptors),
            expected: (1, (budget.ViewDescriptors - clears))
        );
    }
    [Fact]
    public void ACandidateThatDoesNotFitIsRefusedWholeByNameAndAllocatesNothing() {
        using var context = WarpContext(memory: null);
        var bindings = context.Services.Bindings;
        var budget = context.DescriptorHeaps.Budget;
        var free = budget.FreeViewDescriptors;

        Assert.True(condition: bindings.CanAdmit(
            owner: "fits",
            pools: [Pool(buffers: (free - 1U)), Pool(buffers: 1U)],
            refusal: out _
        ));
        Assert.False(condition: bindings.CanAdmit(
            owner: "over",
            pools: [Pool(buffers: free), Pool(buffers: 1U)],
            refusal: out var refusal
        ));
        Assert.StartsWith(
            actualString: refusal,
            expectedStartString: $"[{GpuDescriptorHeapBudget.RefusalCode}] 'over' needs {(free + 1U)} view descriptors in 2 pool(s) and is refused: "
        );
        Assert.StartsWith(
            actualString: Assert.Throws<InvalidOperationException>(testCode: () => bindings.CreatePool(sizes: Pool(buffers: (free + 1U)))).Message,
            expectedStartString: $"[{GpuDescriptorHeapBudget.RefusalCode}] "
        );
        Assert.Equal(
            actual: (budget.FreeViewDescriptors, budget.LivePools),
            expected: (free, 1)
        );
    }
    [Fact]
    public void BothHeapsBytesCountAsDeviceLocalAndEndAtTeardown() {
        var memory = new GpuDeviceMemoryWork(backend: "directx");
        var context = WarpContext(memory: memory);
        var heaps = context.DescriptorHeaps;
        var bytes = checked((long)(heaps.ViewHeapBytes + heaps.SamplerHeapBytes));
        var pool = context.Services.Bindings.CreatePool(sizes: Pool(buffers: 8));

        Assert.Equal(
            actual: (memory.Held, memory.Read(kind: GpuDeviceMemoryWork.Allocated), (heaps.ViewHeapBytes == (((ulong)heaps.Budget.ViewDescriptors) * heaps.ViewIncrement))),
            expected: (bytes, bytes, true)
        );

        // A pool is a range of a heap already counted, so a teardown holding one refuses nothing.
        context.Dispose();
        context.Services.Bindings.DestroyPool(poolHandle: pool);
        Assert.Equal(
            actual: (memory.Held, memory.Read(kind: GpuDeviceMemoryWork.Released)),
            expected: (0L, bytes)
        );
    }
    [Fact]
    public void AClearRecordsThroughTheDevicesHeapsAndReturnsItsSlotWhenItsCommandBufferIsReused() {
        using var context = WarpContext(memory: null);
        var services = context.Services;
        using var buffer = services.BufferFactory.CreateDeviceLocal(
            sizeBytes: 256UL,
            usage: GpuBufferUsage.Storage
        );
        using var pool = services.CommandPoolFactory.Create();
        var command = pool.CommandBufferHandle;

        for (var recording = 0; (recording < 2); recording++) {
            services.Recorder.BeginCommandBuffer(commandBufferHandle: command);
            services.Recorder.ClearStorageBuffer(
                bufferHandle: buffer.BufferHandle,
                commandBufferHandle: command,
                sizeBytes: 256UL
            );
            services.Recorder.EndCommandBuffer(commandBufferHandle: command);
            services.QueueSubmitter.SubmitAndWait(commandBufferHandles: [command]);
        }

        // The second recording's reset returned the first clear's slot, so the next clear takes the same one.
        using var next = context.DescriptorHeaps.AllocateClear();

        Assert.Equal(
            actual: next.Slot,
            expected: 1U
        );
    }

    private static DirectXDeviceContext WarpContext(GpuDeviceMemoryWork? memory) =>
        DirectXTestDevices.Warp(memory: memory);
}
