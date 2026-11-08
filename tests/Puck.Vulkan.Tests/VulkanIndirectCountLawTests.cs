using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using Puck.Abstractions.Gpu;
using Puck.Testing;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;
using Xunit;

namespace Puck.Vulkan.Tests;

/// <summary>Holds <see cref="VulkanGpuRecorder.DrawIndirectCount"/> to the counted indirect draw it records, with no
/// driver: a command table whose <c>vkCmdDrawIndirectCountKHR</c> records what it is handed. The recorder reports the
/// capability only when the device enabled <c>VK_KHR_draw_indirect_count</c>. A device without it issues every initialized
/// record separately from the same argument buffer, with its word offset pushed for DrawIndex identity lookup.</summary>
public sealed unsafe class VulkanIndirectCountLawTests {
    private const nint ArgumentBuffer = 0x0A00;
    private const nint CommandBuffer = 0x0C00;
    private const nint CountBuffer = 0x0B00;
    private const nint DeviceHandle = 0x0D00;

    [ThreadStatic]
    private static List<(nint CommandBuffer, nint Buffer, ulong Offset, nint CountBuffer, ulong CountOffset, uint MaxDrawCount, uint Stride)>? Draws;
    [ThreadStatic]
    private static List<uint>? Bases;

    [Fact]
    public void ACountedIndirectDrawStartsAtTheRecordsVertexCountAndStridesByOneCommand() {
        var recorder = Recorder(drawIndirectCountEnabled: true);

        Assert.True(condition: recorder.CanDrawIndirectCount);

        Draws = [];
        Bases = [];

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
            Assert.Equal(expected: [16u], actual: Bases);
        } finally {
            Draws = null;
            Bases = null;
        }
    }
    [Fact]
    public void ADeviceWithoutNativeCountingDrawsInitializedSlotsWithTheirOwnBases() {
        var recorder = Recorder(drawIndirectCountEnabled: false);

        Assert.False(condition: recorder.CanDrawIndirectCount);
        Draws = [];
        Bases = [];
        try {
            recorder.DrawIndirectCount(
                argumentBufferHandle: ArgumentBuffer,
                argumentBufferOffset: 40UL,
                commandBufferHandle: CommandBuffer,
                countBufferHandle: CountBuffer,
                countBufferOffset: 0UL,
                maxDrawCount: 3U,
                pipelineLayoutHandle: 0x7000
            );
            Assert.Equal(expected: [10u, 15u, 20u], actual: Bases);
            Assert.Equal(expected: [44UL, 64UL, 84UL], actual: Draws.Select(static draw => draw.Offset));
            Assert.All(collection: Draws, action: static draw => Assert.Equal(expected: 1u, actual: draw.MaxDrawCount));
        } finally {
            Draws = null;
            Bases = null;
        }
    }

    [Fact]
    public void FixedRecordsBatchWithoutNativeCountingWhenMultiDrawIsAvailable() {
        var recorder = Recorder(drawIndirectCountEnabled: false, multiDraw: true);
        Draws = [];
        Bases = [];
        try {
            recorder.DrawIndirectCount(argumentBufferHandle: ArgumentBuffer, argumentBufferOffset: 40UL,
                commandBufferHandle: CommandBuffer, countBufferHandle: CountBuffer, countBufferOffset: 0UL,
                maxDrawCount: 3u, pipelineLayoutHandle: 0x7000);
            Assert.Equal(expected: [10u], actual: Bases);
            Assert.Single(collection: Draws);
            Assert.Equal(expected: (44UL, 3u), actual: (Draws[0].Offset, Draws[0].MaxDrawCount));
        } finally {
            Draws = null;
            Bases = null;
        }
    }

    private static VulkanGpuRecorder Recorder(bool drawIndirectCountEnabled, bool multiDraw = false) {
        var commands = new VulkanDeviceCommands(
            deviceHandle: DeviceHandle,
            drawIndirectCountEnabled: drawIndirectCountEnabled,
            multiDrawIndirectEnabled: multiDraw,
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
            "vkCmdDrawIndirect" => ((nint)((delegate* unmanaged[Cdecl]<nint, nint, ulong, uint, uint, void>)&DrawIndirect)),
            "vkCmdPushConstants" => ((nint)((delegate* unmanaged[Cdecl]<nint, nint, uint, uint, uint, void*, void>)&Push)),
            _ => ((nint)((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint>)&Trap)),
        };
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DrawIndirectCount(nint commandBuffer, nint buffer, ulong offset, nint countBuffer, ulong countOffset, uint maxDrawCount, uint stride) =>
        Draws!.Add(item: (commandBuffer, buffer, offset, countBuffer, countOffset, maxDrawCount, stride));
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DrawIndirect(nint commandBuffer, nint buffer, ulong offset, uint count, uint stride) =>
        Draws!.Add((commandBuffer, buffer, offset, 0, 0UL, count, stride));
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Push(nint commandBuffer, nint layout, uint stages, uint offset, uint bytes, void* data) =>
        Bases!.Add(*(uint*)data);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Trap(nint first, nint second, nint third, nint fourth) => 0;

    private sealed class DeviceContext(VulkanLogicalDevice device) : IVulkanDeviceContext {
        public VulkanInstance Instance => throw new NotSupportedException();
        public VulkanLogicalDevice LogicalDevice => device;
        public VkPhysicalDevice PhysicalDevice => throw new NotSupportedException();
        public VulkanSurface Surface => throw new NotSupportedException();
    }
    private sealed class NoLogicalDeviceApi : IVulkanLogicalDeviceApi {
        public VkResult CreateLogicalDevice(VulkanLogicalDeviceCreateRequest request, out VulkanDeviceCommands? device) => throw new NotSupportedException();
        public void DestroyDevice(VulkanDeviceCommands device) { }
        public nint GetDeviceQueue(VulkanDeviceCommands device, uint queueFamilyIndex, uint queueIndex) => throw new NotSupportedException();
        public VkResult WaitIdle(VulkanDeviceCommands device) => VkResult.Success;
    }
}
