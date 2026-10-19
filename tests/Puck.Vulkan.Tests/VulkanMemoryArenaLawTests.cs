using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;
using Xunit;

namespace Puck.Vulkan.Tests;

/// <summary>
/// Laws over a device's memory arena (<see cref="VulkanMemoryArena"/>) on a CPU recording driver: resources share a block
/// of their memory type, role and kind at aligned offsets, a resource past half a block gets its own, host-visible blocks
/// map once and hand each resource its offset, a pool keeps one empty block and frees the next, a failed bind or map
/// gives back what it took, destroying a resource the arena bound returns its range instead of freeing memory, and the
/// device's counts see one allocation per block. A device lost mid-frame tears down with resources still bound: every
/// block is unmapped and freed once before the device is destroyed, no destroy after that reaches the driver, a late or
/// repeated destroy never frees a block, the retired arena binds and maps nothing, and a rebuilt device's arena starts
/// afresh.
/// </summary>
public sealed unsafe class VulkanMemoryArenaLawTests {
    // A bind of this resource fails, and memory of this type refuses its mapping.
    private const nint Refused = 666;
    private const uint Unmappable = 9;
    private const ulong Block = VulkanMemoryArena.BlockBytes;

    [ThreadStatic] private static List<string>? Calls;
    [ThreadStatic] private static nint NextMemory;
    // When set, a buffer's destroy records whether this arena still held its range as the driver destroyed it.
    [ThreadStatic] private static VulkanMemoryArena? Watched;

    [Fact]
    public void ResourcesShareBlocksAndGiveBackTheirRanges() {
        Calls = [];
        NextMemory = 1000;
        var work = new GpuDeviceMemoryWork(backend: "vulkan");
        var device = new VulkanDeviceCommands(17, Procedures(), memory: work);
        var arena = device.Arena;
        var small = new VkMemoryRequirements { Alignment = 256UL, Size = 1000UL };

        // Two buffers share a block at aligned offsets; an image of the same type takes a block of its own kind, and a
        // buffer past half a block that no block holds gets one its size.
        Assert.Equal(expected: 1000, actual: arena.Bind(image: false, memoryTypeIndex: 0, requirements: in small, resource: 1, role: GpuMemoryRole.DeviceLocal));
        Assert.Equal(expected: 1000, actual: arena.Bind(image: false, memoryTypeIndex: 0, requirements: in small, resource: 2, role: GpuMemoryRole.DeviceLocal));
        Assert.Equal(expected: 1001, actual: arena.Bind(image: true, memoryTypeIndex: 0, requirements: in small, resource: 3, role: GpuMemoryRole.DeviceLocal));
        Assert.Equal(expected: 1002, actual: arena.Bind(image: false, memoryTypeIndex: 0, requirements: new VkMemoryRequirements { Alignment = 1UL, Size = Block }, resource: 4, role: GpuMemoryRole.DeviceLocal));
        Assert.Equal(expected: 1003, actual: arena.Bind(image: false, memoryTypeIndex: 1, requirements: in small, resource: 5, role: GpuMemoryRole.HostVisible));
        Assert.Equal(expected: 4, actual: arena.Blocks);
        Assert.Equal(
            expected: [$"alloc 1000 {Block} type=0", "bind buffer 1 1000 0", "bind buffer 2 1000 1024", $"alloc 1001 {Block} type=0", "bind image 3 1001 0", $"alloc 1002 {Block} type=0", "bind buffer 4 1002 0", $"alloc 1003 {Block} type=1", "map 1003", "bind buffer 5 1003 0"],
            actual: Calls
        );
        Assert.True(condition: work.TryRead(kind: GpuDeviceMemoryWork.Allocations, value: out var allocations));
        Assert.Equal(expected: 4L, actual: allocations);

        // Only a host-visible block is mapped, at the resource's offset.
        Assert.True(condition: arena.TryMapped(pointer: out var mapped, resource: 5));
        Assert.Equal(expected: ((nint)0x10000), actual: mapped);
        Assert.False(condition: arena.TryMapped(pointer: out mapped, resource: 1));
        Assert.Equal(expected: 0, actual: mapped);
        Assert.False(condition: arena.TryMapped(pointer: out _, resource: 99));

        // A pool keeps one empty block and frees a second; a resource the arena never bound is not its to release.
        Calls.Clear();
        Assert.False(condition: arena.Release(image: false, resource: 99));
        Assert.True(condition: arena.Release(image: false, resource: 2));
        Assert.True(condition: arena.Release(image: false, resource: 1));
        Assert.True(condition: arena.Release(image: false, resource: 4));
        Assert.Equal(expected: ["free 1002"], actual: Calls);

        // A buffer and an image may share a handle value, as a layer that wraps handles hands them out: each binds and
        // releases as its own resource.
        Calls.Clear();
        _ = arena.Bind(image: false, memoryTypeIndex: 0, requirements: in small, resource: 3, role: GpuMemoryRole.DeviceLocal);
        Assert.True(condition: arena.Release(image: false, resource: 3));
        Assert.False(condition: arena.Release(image: false, resource: 3));
        Assert.Equal(expected: ["bind buffer 3 1000 0"], actual: Calls);

        // A destroy gives the range back before the driver destroys the handle, so a creation handed the same handle
        // value the moment it is destroyed finds no range still standing under it.
        Calls.Clear();
        Watched = arena;
        _ = arena.Bind(image: false, memoryTypeIndex: 0, requirements: in small, resource: 9, role: GpuMemoryRole.DeviceLocal);
        device.Destroy(destroy: device.DestroyBuffer, handle: 9, memoryHandle: 1000);
        Watched = null;
        Assert.Equal(expected: ["bind buffer 9 1000 0", "destroy buffer 9 standing=False"], actual: Calls);

        // A failed bind gives its range back to the block, which the next resource takes.
        Calls.Clear();
        _ = Assert.Throws<VulkanException>(testCode: () => arena.Bind(image: false, memoryTypeIndex: 0, requirements: in small, resource: Refused, role: GpuMemoryRole.DeviceLocal));
        Assert.Equal(expected: 1000, actual: arena.Bind(image: false, memoryTypeIndex: 0, requirements: in small, resource: 6, role: GpuMemoryRole.DeviceLocal));
        Assert.Equal(expected: [$"bind buffer {Refused} 1000 0", "bind buffer 6 1000 0"], actual: Calls);

        // A block whose mapping fails is freed before the failure surfaces.
        Calls.Clear();
        _ = Assert.Throws<VulkanException>(testCode: () => arena.Bind(image: false, memoryTypeIndex: Unmappable, requirements: in small, resource: 7, role: GpuMemoryRole.HostVisibleDeviceLocal));
        Assert.Equal(expected: [$"alloc 1004 {Block} type={Unmappable}", "map 1004", "free 1004"], actual: Calls);

        // Destroying a resource the arena bound returns its range; any other resource frees its own memory.
        Calls.Clear();
        device.Destroy(destroy: device.DestroyBuffer, handle: 6, memoryHandle: 1000);
        device.Destroy(destroy: device.DestroyBuffer, handle: 77, memoryHandle: 555);
        Assert.Equal(expected: ["destroy buffer 6", "destroy buffer 77", "free 555"], actual: Calls);

        // The device's teardown frees every block, unmapping the mapped one.
        Calls.Clear();
        arena.FreeAll();
        Assert.Equal(expected: 0, actual: arena.Blocks);
        Assert.Equal(expected: ["free 1000", "free 1001", "unmap 1003", "free 1003"], actual: Calls);
        Assert.False(condition: arena.Release(image: true, resource: 3));
    }

    [Fact]
    public void ALostDeviceTearsDownWithBlocksLiveFreeingEachOnceAndTouchingNothingAfterDestroy() {
        Calls = [];
        NextMemory = 1000;
        var work = new GpuDeviceMemoryWork(backend: "vulkan");
        var device = new VulkanDeviceCommands(17, Procedures(), memory: work);
        var logical = new VulkanLogicalDevice(device, default, new VkQueue(familyIndex: 0, handle: 31), default, new LogicalApi());
        var sync = new SynchronizationApi();
        var queue = new VulkanGpuQueueSubmitter(new Context(logical: logical), new VulkanQueueSubmitter(), sync);
        var small = new VkMemoryRequirements { Alignment = 256UL, Size = 1000UL };
        var arena = device.Arena;
        var buffers = new VulkanNativeBufferApi();

        // Mid-frame: a device-local buffer and image, and a mapped staging buffer, all live when the device is lost.
        _ = arena.Bind(image: false, memoryTypeIndex: 0, requirements: in small, resource: 1, role: GpuMemoryRole.DeviceLocal);
        _ = arena.Bind(image: true, memoryTypeIndex: 0, requirements: in small, resource: 2, role: GpuMemoryRole.DeviceLocal);
        _ = arena.Bind(image: false, memoryTypeIndex: 1, requirements: in small, resource: 3, role: GpuMemoryRole.HostVisible);
        var staging = new VulkanBufferHandles(Buffer: 3, Device: device, Memory: 1002);
        Assert.Equal(expected: ((nint)0x10000), actual: buffers.Map(handles: staging, sizeBytes: 1000UL));
        using var fence = queue.CreateSubmissionFence();
        queue.Submit(commandBufferHandles: [42], fence: fence);
        sync.Lost = true;
        _ = Assert.Throws<DeviceLostException>(testCode: fence.Wait);

        // Recovery tears the device down with every resource still bound: each block unmapped and freed once, before the
        // device is destroyed, and the counts end with nothing held.
        Calls.Clear();
        logical.Dispose();
        logical.Dispose();
        Assert.Equal(expected: ["free 1000", "free 1001", "unmap 1002", "free 1002", "destroy device"], actual: Calls);
        Assert.True(condition: device.IsDestroyed);
        Assert.Equal(expected: 0L, actual: work.Held);

        // The owners release their resources late, one twice: nothing reaches the driver, and no block is freed again.
        Calls.Clear();
        device.Destroy(destroy: device.DestroyBuffer, handle: 1, memoryHandle: 1000);
        device.Destroy(destroy: device.DestroyImage, handle: 2, memoryHandle: 1001);
        buffers.Unmap(handles: staging);
        buffers.Destroy(handles: staging);
        buffers.Destroy(handles: staging);
        device.Destroy(destroy: device.DestroyBuffer, handle: 0, memoryHandle: 1000);
        Assert.Empty(collection: Calls);
        Assert.False(condition: arena.TryMapped(pointer: out _, resource: 3));
        _ = Assert.Throws<InvalidOperationException>(testCode: () => buffers.Map(handles: staging, sizeBytes: 1000UL));
        _ = Assert.Throws<ObjectDisposedException>(testCode: () => arena.Bind(image: false, memoryTypeIndex: 0, requirements: in small, resource: 4, role: GpuMemoryRole.DeviceLocal));

        // The rebuilt device's arena is its own: it allocates afresh and frees only its own blocks.
        var rebuilt = new VulkanDeviceCommands(18, Procedures(), memory: work);
        using (var rebuiltLogical = new VulkanLogicalDevice(rebuilt, default, new VkQueue(familyIndex: 0, handle: 32), default, new LogicalApi())) {
            Assert.Equal(expected: 1003, actual: rebuilt.Arena.Bind(image: false, memoryTypeIndex: 0, requirements: in small, resource: 1, role: GpuMemoryRole.DeviceLocal));
            rebuilt.Destroy(destroy: rebuilt.DestroyBuffer, handle: 1, memoryHandle: 1003);
        }
        Assert.Equal(expected: [$"alloc 1003 {Block} type=0", "bind buffer 1 1003 0", "destroy buffer 1", "free 1003", "destroy device"], actual: Calls);
    }

    private static VulkanProcResolver Procedures() => new(getDeviceProcAddr: &Resolve, getInstanceProcAddr: &Resolve);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Resolve(nint handle, byte* name) => (Marshal.PtrToStringUTF8(ptr: ((nint)name)) switch {
        "vkAllocateMemory" => ((nint)((delegate* unmanaged[Cdecl]<nint, VkMemoryAllocateInfo*, nint, nint*, VkResult>)&AllocateMemory)),
        "vkFreeMemory" => ((nint)((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&FreeMemory)),
        "vkBindBufferMemory" => ((nint)((delegate* unmanaged[Cdecl]<nint, nint, nint, ulong, VkResult>)&BindBuffer)),
        "vkBindImageMemory" => ((nint)((delegate* unmanaged[Cdecl]<nint, nint, nint, ulong, VkResult>)&BindImage)),
        "vkMapMemory" => ((nint)((delegate* unmanaged[Cdecl]<nint, nint, ulong, nuint, uint, nint*, VkResult>)&MapMemory)),
        "vkUnmapMemory" => ((nint)((delegate* unmanaged[Cdecl]<nint, nint, void>)&UnmapMemory)),
        "vkDestroyBuffer" => ((nint)((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&DestroyBuffer)),
        "vkDestroyImage" => ((nint)((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&DestroyImage)),
        "vkQueueSubmit" => ((nint)((delegate* unmanaged[Cdecl]<nint, uint, nint, nint, VkResult>)&Submit)),
        _ => ((nint)((delegate* unmanaged[Cdecl]<void>)&Trap)),
    });
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Trap() => Environment.FailFast(message: "The arena law reached an unwired native entry point.");
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static VkResult AllocateMemory(nint device, VkMemoryAllocateInfo* info, nint allocator, nint* memory) {
        *memory = NextMemory++;
        Calls!.Add(item: $"alloc {*memory} {info->AllocationSize} type={info->MemoryTypeIndex}");

        return VkResult.Success;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void FreeMemory(nint device, nint memory, nint allocator) => Calls!.Add(item: $"free {memory}");
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static VkResult BindBuffer(nint device, nint buffer, nint memory, ulong offset) => Bind(kind: "buffer", memory: memory, offset: offset, resource: buffer);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static VkResult BindImage(nint device, nint image, nint memory, ulong offset) => Bind(kind: "image", memory: memory, offset: offset, resource: image);
    private static VkResult Bind(string kind, nint resource, nint memory, ulong offset) {
        Calls!.Add(item: $"bind {kind} {resource} {memory} {offset}");

        return ((resource == Refused) ? VkResult.ErrorOutOfDeviceMemory : VkResult.Success);
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static VkResult MapMemory(nint device, nint memory, ulong offset, nuint size, uint flags, nint* data) {
        Calls!.Add(item: $"map {memory}");
        *data = ((memory == 1004) ? 0 : 0x10000);

        return ((memory == 1004) ? VkResult.ErrorMemoryMapFailed : VkResult.Success);
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void UnmapMemory(nint device, nint memory) => Calls!.Add(item: $"unmap {memory}");
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DestroyBuffer(nint device, nint buffer, nint allocator) =>
        Calls!.Add(item: ((Watched is { } arena) ? $"destroy buffer {buffer} standing={arena.Release(image: false, resource: buffer)}" : $"destroy buffer {buffer}"));
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DestroyImage(nint device, nint image, nint allocator) => Calls!.Add(item: $"destroy image {image}");
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static VkResult Submit(nint queue, uint count, nint submits, nint fence) => VkResult.Success;

    private sealed class Context(VulkanLogicalDevice logical) : IVulkanDeviceContext {
        public VulkanInstance Instance => throw new NotSupportedException();
        public VulkanLogicalDevice LogicalDevice => logical;
        public VkPhysicalDevice PhysicalDevice => throw new NotSupportedException();
        public VulkanSurface Surface => throw new NotSupportedException();
    }
    private sealed class LogicalApi : IVulkanLogicalDeviceApi {
        public VkResult CreateLogicalDevice(VulkanLogicalDeviceCreateRequest request, out VulkanDeviceCommands? device) => throw new NotSupportedException();
        public void DestroyDevice(VulkanDeviceCommands device) => Calls!.Add(item: "destroy device");
        public nint GetDeviceQueue(VulkanDeviceCommands device, uint queueFamilyIndex, uint queueIndex) => throw new NotSupportedException();
        public VkResult WaitIdle(VulkanDeviceCommands device) => throw new NotSupportedException();
    }
    // One fence, signaled until the device is lost.
    private sealed class SynchronizationApi : IVulkanFrameSynchronizationApi {
        public bool Lost;

        public VkResult CreateFence(VulkanFrameSynchronizationCreateRequest request, out nint fenceHandle) { fenceHandle = 23; return VkResult.Success; }
        public VkResult CreateSemaphore(VulkanFrameSynchronizationCreateRequest request, out nint semaphoreHandle) => throw new NotSupportedException();
        public void DestroyFence(VulkanDeviceCommands device, nint fenceHandle) { }
        public void DestroySemaphore(VulkanDeviceCommands device, nint semaphoreHandle) => throw new NotSupportedException();
        public VkResult GetFenceStatus(VulkanDeviceCommands device, nint fenceHandle) => (Lost ? VkResult.ErrorDeviceLost : VkResult.Success);
        public VkResult ResetFence(VulkanDeviceCommands device, nint fenceHandle) => VkResult.Success;
        public VkResult WaitForFence(VulkanDeviceCommands device, nint fenceHandle, ulong timeout) => (Lost ? VkResult.ErrorDeviceLost : VkResult.Success);
    }
}
