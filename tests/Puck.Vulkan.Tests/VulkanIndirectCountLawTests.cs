using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using Puck.Abstractions.Gpu;
using Puck.Abstractions.Memory;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;
using Xunit;

namespace Puck.Vulkan.Tests;

/// <summary>Holds <see cref="VulkanGpuRecorder.DrawIndirectCount"/> to the counted indirect draw it records, with no
/// driver: a command table whose <c>vkCmdDrawIndirectCountKHR</c> records what it is handed. The recorder reports the
/// capability only when the device enabled <c>VK_KHR_draw_indirect_count</c>, starts each record at its vertex count so
/// the identity rides the record's first instance, strides by one <see cref="GpuDrawIndirectCommand"/>, and a device
/// created without the extension refuses the call by name.</summary>
public sealed unsafe class VulkanIndirectCountLawTests {
    private const nint ArgumentBuffer = 0x0A00;
    private const nint CommandBuffer = 0x0C00;
    private const nint CountBuffer = 0x0B00;
    private const nint DeviceHandle = 0x0D00;

    [ThreadStatic]
    private static List<(nint CommandBuffer, nint Buffer, ulong Offset, nint CountBuffer, ulong CountOffset, uint MaxDrawCount, uint Stride)>? Draws;

    [Fact]
    public void ACountedIndirectDrawStartsAtTheRecordsVertexCountAndStridesByOneCommand() {
        var recorder = Recorder(drawIndirectCountEnabled: true);

        Assert.True(condition: recorder.CanDrawIndirectCount);

        Draws = [];

        try {
            recorder.DrawIndirectCount(
                argumentBufferHandle: ArgumentBuffer,
                argumentBufferOffset: 64UL,
                commandBufferHandle: CommandBuffer,
                countBufferHandle: CountBuffer,
                countBufferOffset: 4UL,
                maxDrawCount: 9U,
                pipelineLayoutHandle: 0x7000
            );
            Assert.Equal(
                actual: Draws,
                expected: [(CommandBuffer, ArgumentBuffer, (64UL + (GpuDrawIndirectCommand.VertexCountWord * sizeof(uint))), CountBuffer, 4UL, 9U, ((uint)GpuDrawIndirectCommand.StrideBytes))]
            );
        } finally {
            Draws = null;
        }
    }
    [Fact]
    public void ADeviceCreatedWithoutTheExtensionNeitherReportsNorRecordsIt() {
        var recorder = Recorder(drawIndirectCountEnabled: false);

        Assert.False(condition: recorder.CanDrawIndirectCount);
        Assert.Contains(
            expectedSubstring: VulkanDeviceCommands.DrawIndirectCountExtension,
            actualString: Assert.Throws<NotSupportedException>(testCode: () => recorder.DrawIndirectCount(
                argumentBufferHandle: ArgumentBuffer,
                argumentBufferOffset: 0UL,
                commandBufferHandle: CommandBuffer,
                countBufferHandle: CountBuffer,
                countBufferOffset: 0UL,
                maxDrawCount: 1U,
                pipelineLayoutHandle: 0x7000
            )).Message
        );
    }

    private static VulkanGpuRecorder Recorder(bool drawIndirectCountEnabled) {
        var commands = new VulkanDeviceCommands(
            deviceHandle: DeviceHandle,
            drawIndirectCountEnabled: drawIndirectCountEnabled,
            memory: null,
            procedures: new VulkanProcResolver(
                getDeviceProcAddr: &Resolve,
                getInstanceProcAddr: &Resolve
            )
        );
        var context = new DeviceContext(device: new VulkanLogicalDevice(
            device: commands,
            graphicsQueue: default,
            logicalDeviceApi: new NoLogicalDeviceApi(),
            physicalDevice: default,
            presentQueue: default
        ));

        return new VulkanGpuRecorder(
            deviceContext: context,
            recordingApi: new VulkanNativeCommandBufferRecordingApi(allocator: new HeapAllocator())
        );
    }
    // A vkGetDeviceProcAddr stand-in: vkCmdDrawIndirectCountKHR records, and every other entry point resolves to a trap
    // nothing here reaches.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Resolve(nint handle, byte* name) =>
        Encoding.UTF8.GetString(bytes: MemoryMarshal.CreateReadOnlySpanFromNullTerminated(value: name)) switch {
            "vkCmdDrawIndirectCountKHR" => ((nint)((delegate* unmanaged[Cdecl]<nint, nint, ulong, nint, ulong, uint, uint, void>)&DrawIndirectCount)),
            _ => ((nint)((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint>)&Trap)),
        };
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DrawIndirectCount(nint commandBuffer, nint buffer, ulong offset, nint countBuffer, ulong countOffset, uint maxDrawCount, uint stride) =>
        Draws!.Add(item: (commandBuffer, buffer, offset, countBuffer, countOffset, maxDrawCount, stride));
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Trap(nint first, nint second, nint third, nint fourth) => 0;

    private sealed class DeviceContext(VulkanLogicalDevice device) : IVulkanDeviceContext {
        public VulkanInstance Instance => throw new NotSupportedException();
        public VulkanLogicalDevice LogicalDevice => device;
        public VkPhysicalDevice PhysicalDevice => throw new NotSupportedException();
        public VulkanSurface Surface => throw new NotSupportedException();
    }
    private sealed class HeapAllocator : IAllocator {
        public void* Allocate(nuint size, nuint alignment = 0) => NativeMemory.Alloc(byteCount: Math.Max(
            val1: size,
            val2: 1
        ));
        public void Free(void* ptr) => NativeMemory.Free(ptr: ptr);
        public void* Reallocate(void* ptr, nuint newSize, nuint alignment = 0) => NativeMemory.Realloc(
            byteCount: Math.Max(
                val1: newSize,
                val2: 1
            ),
            ptr: ptr
        );
    }
    private sealed class NoLogicalDeviceApi : IVulkanLogicalDeviceApi {
        public VkResult CreateLogicalDevice(VulkanLogicalDeviceCreateRequest request, out VulkanDeviceCommands? device) => throw new NotSupportedException();
        public void DestroyDevice(VulkanDeviceCommands device) { }
        public nint GetDeviceQueue(VulkanDeviceCommands device, uint queueFamilyIndex, uint queueIndex) => throw new NotSupportedException();
        public VkResult WaitIdle(VulkanDeviceCommands device) => VkResult.Success;
    }
}
