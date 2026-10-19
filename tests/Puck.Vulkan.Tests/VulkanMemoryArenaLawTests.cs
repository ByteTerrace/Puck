using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interop;
using Xunit;

namespace Puck.Vulkan.Tests;

/// <summary>
/// Laws over a device's memory arena (<see cref="VulkanMemoryArena"/>) on a CPU recording driver: resources share a block
/// of their memory type, role and kind at aligned offsets, a resource past half a block gets its own, host-visible blocks
/// map once and hand each resource its offset, a pool keeps one empty block and frees the next, a failed bind or map
/// gives back what it took, destroying a resource the arena bound returns its range instead of freeing memory, and the
/// device's counts see one allocation per block.
/// </summary>
public sealed unsafe class VulkanMemoryArenaLawTests {
    // A bind of this resource fails, and memory of this type refuses its mapping.
    private const nint Refused = 666;
    private const uint Unmappable = 9;
    private const ulong Block = VulkanMemoryArena.BlockBytes;

    [ThreadStatic] private static List<string>? Calls;
    [ThreadStatic] private static nint NextMemory;

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
        Assert.False(condition: arena.Release(resource: 99));
        Assert.True(condition: arena.Release(resource: 2));
        Assert.True(condition: arena.Release(resource: 1));
        Assert.True(condition: arena.Release(resource: 4));
        Assert.Equal(expected: ["free 1002"], actual: Calls);

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
        Assert.False(condition: arena.Release(resource: 3));
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
    private static void DestroyBuffer(nint device, nint buffer, nint allocator) => Calls!.Add(item: $"destroy buffer {buffer}");
}
