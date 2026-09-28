using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Puck.Abstractions.Gpu;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;
using Xunit;

namespace Puck.Vulkan.Tests;

/// <summary>Pins, without a device, the commands a <see cref="VulkanSurfaceReadback"/> records through the real
/// <see cref="VulkanGpuRecorder"/>: after the copy into the readback buffer comes a buffer barrier from the copy's
/// transfer write to <c>VK_PIPELINE_STAGE_HOST_BIT</c> and <c>VK_ACCESS_HOST_READ_BIT</c>, before the command buffer
/// ends, so the host that reads the buffer once the submission completes sees the copied bytes. A completed submission
/// alone makes no device write visible to the host.</summary>
public sealed unsafe class VulkanSurfaceReadbackLawTests {
    private const nint Buffer = 0x30;
    private const nint CommandBuffer = 0x10;
    private const nint DeviceHandle = 0x40;
    private const nint Image = 0x20;

    public static TheoryData<GpuImageLayout> SourceLayouts() => [
        GpuImageLayout.External,
        GpuImageLayout.General,
        GpuImageLayout.ShaderReadOnly,
    ];
    [MemberData(nameof(SourceLayouts))]
    [Theory]
    public void TheCopyReachesTheHostThroughABarrierBeforeTheCommandBufferEnds(GpuImageLayout sourceLayout) {
        var calls = Record(sourceLayout: sourceLayout);

        Assert.Equal(
            actual: calls.Select(selector: call => call.Name),
            expected: [
                nameof(IVulkanCommandBufferRecordingApi.BeginCommandBuffer),
                nameof(IVulkanCommandBufferRecordingApi.TransitionImageLayout),
                nameof(IVulkanCommandBufferRecordingApi.CopyImageToBuffer),
                nameof(IVulkanCommandBufferRecordingApi.PipelineBufferBarrier),
                nameof(IVulkanCommandBufferRecordingApi.TransitionImageLayout),
                nameof(IVulkanCommandBufferRecordingApi.EndCommandBuffer),
            ]
        );

        var copy = calls[2];
        var barrier = calls[3];

        Assert.Equal(
            actual: copy.Arguments["bufferHandle"],
            expected: Buffer
        );
        Assert.Equal(
            actual: barrier.Arguments["bufferHandle"],
            expected: Buffer
        );
        Assert.Equal(
            actual: (barrier.Arguments["sourceStageMask"], barrier.Arguments["sourceAccessMask"]),
            expected: (((object)VulkanPipelineStageFlags.Transfer), ((object)VulkanAccessFlags.TransferWrite))
        );
        Assert.Equal(
            actual: (barrier.Arguments["destinationStageMask"], barrier.Arguments["destinationAccessMask"]),
            expected: (((object)VulkanPipelineStageFlags.Host), ((object)VulkanAccessFlags.HostRead))
        );
    }
    [MemberData(nameof(SourceLayouts))]
    [Theory]
    public void TheSourceImageReturnsToItsLayoutAfterTheCopy(GpuImageLayout sourceLayout) {
        var calls = Record(sourceLayout: sourceLayout);
        var toSource = calls[1];
        var back = calls[4];

        Assert.Equal(
            actual: (toSource.Arguments["oldLayout"], toSource.Arguments["newLayout"]),
            expected: (back.Arguments["newLayout"], back.Arguments["oldLayout"])
        );
        Assert.Equal(
            actual: toSource.Arguments["newLayout"],
            expected: VulkanImageLayout.TransferSourceOptimal
        );
    }

    private static List<Call> Record(GpuImageLayout sourceLayout) {
        var recording = DispatchProxy.Create<IVulkanCommandBufferRecordingApi, CallRecorder>();
        var commands = new VulkanDeviceCommands(
            deviceHandle: DeviceHandle,
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

        VulkanSurfaceReadback.Record(
            bufferHandle: Buffer,
            commandBufferHandle: CommandBuffer,
            device: commands,
            height: 2,
            recorder: new VulkanGpuRecorder(
                deviceContext: context,
                recordingApi: recording
            ),
            recordingApi: recording,
            sourceImageHandle: Image,
            sourceLayout: sourceLayout,
            width: 2
        );

        return ((CallRecorder)((object)recording)).Calls;
    }
    // Every entry point resolves to a trap: the recording API is a proxy, so nothing reaches a native command.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Resolve(nint handle, byte* name) =>
        ((nint)((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint>)&Trap));
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Trap(nint first, nint second, nint third, nint fourth) => 0;

    public sealed record Call(string Name, IReadOnlyDictionary<string, object?> Arguments);
    public class CallRecorder : DispatchProxy {
        public List<Call> Calls { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
            ArgumentNullException.ThrowIfNull(argument: targetMethod);

            var parameters = targetMethod.GetParameters();

            Assert.Equal(
                actual: args![Array.FindIndex(
                    array: parameters,
                    match: parameter => (parameter.Name == "commandBufferHandle")
                )],
                expected: CommandBuffer
            );
            Calls.Add(item: new Call(
                Arguments: parameters.Select(selector: (parameter, index) => (parameter.Name!, args[index])).ToDictionary(),
                Name: targetMethod.Name
            ));

            return ((targetMethod.ReturnType == typeof(VkResult))
                ? VkResult.Success
                : null);
        }
    }

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
