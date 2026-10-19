using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interop;
using Xunit;

namespace Puck.Vulkan.Tests;

/// <summary>
/// Laws over a device's breadcrumbs (<see cref="VulkanDeviceCheckpoints"/>) on a CPU recording driver: each label a
/// command buffer opens records a checkpoint marker, one marker per label; a device loss reads the watched queue's last
/// checkpoints back as the labels and stages they were reached at, after the fault report; a device without the
/// extension marks nothing and says so.
/// </summary>
public sealed unsafe class VulkanDeviceCheckpointsLawTests {
    [ThreadStatic] private static List<string>? Calls;
    [ThreadStatic] private static uint Reached;

    [Fact]
    public void ALostDeviceReadsTheLabelsItsQueueReached() {
        Calls = [];
        Reached = 4;
        var device = new VulkanDeviceCommands(17, Procedures(), checkpointsEnabled: true);
        var checkpoints = device.Checkpoints;

        Assert.True(condition: checkpoints.IsEnabled);
        checkpoints.Watch(queue: 31);
        checkpoints.Mark(commandBuffer: 5, label: "world/sdf.world$mesh");
        checkpoints.Mark(commandBuffer: 5, label: "world/sdf.world$surface");
        checkpoints.Mark(commandBuffer: 6, label: "world/sdf.world$mesh");
        Assert.Equal(expected: ["mark 5 1", "mark 5 2", "mark 6 1"], actual: Calls);

        var report = device.Fault.ReadAfterDeviceLoss();

        Assert.Equal(
            expected: "VK_EXT_device_fault unavailable (extension or deviceFault feature not enabled).\n" +
                "VK_NV_device_diagnostic_checkpoints: the queue's last checkpoints\n" +
                "  started 'world/sdf.world$surface'\n" +
                "  finished every pass before 'world/sdf.world$mesh'\n" +
                "  reached stage 0x80 at 'world/sdf.world$surface'\n" +
                "  started 'unknown marker 0x9'",
            actual: report
        );
        Assert.Equal(expected: ["read 31 0", "read 31 4"], actual: Calls!.Skip(count: 3));
    }
    [Fact]
    public void AQueueThatReachedNoCheckpointOrAnAbsurdCountSaysSo() {
        Calls = [];
        Reached = 0;
        var quiet = new VulkanDeviceCommands(17, Procedures(), checkpointsEnabled: true);

        Assert.Equal(expected: "VK_NV_device_diagnostic_checkpoints: the queue reached no checkpoint.", actual: quiet.Checkpoints.Read());

        Reached = uint.MaxValue;
        var absurd = new VulkanDeviceCommands(17, Procedures(), checkpointsEnabled: true);

        Assert.Contains(expectedSubstring: "VK_NV_device_diagnostic_checkpoints query failed: ", actualString: absurd.Fault.ReadAfterDeviceLoss());
    }
    [Fact]
    public void ADeviceWithoutTheExtensionMarksNothing() {
        Calls = [];
        var device = new VulkanDeviceCommands(17, Procedures());

        Assert.False(condition: device.Checkpoints.IsEnabled);
        device.Checkpoints.Mark(commandBuffer: 5, label: "pass");
        Assert.Empty(collection: Calls);
        Assert.Equal(expected: "VK_NV_device_diagnostic_checkpoints unavailable.", actual: device.Checkpoints.Read());
        _ = Assert.Throws<ArgumentNullException>(testCode: () => new VulkanDeviceCheckpoints(device: 17, enabled: true, procedures: null!));
    }

    private static VulkanProcResolver Procedures() => new(getDeviceProcAddr: &Resolve, getInstanceProcAddr: &Resolve);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Resolve(nint handle, byte* name) => (Marshal.PtrToStringUTF8(ptr: ((nint)name)) switch {
        "vkCmdSetCheckpointNV" => ((nint)((delegate* unmanaged[Cdecl]<nint, nint, void>)&Mark)),
        "vkGetQueueCheckpointDataNV" => ((nint)((delegate* unmanaged[Cdecl]<nint, uint*, VkCheckpointDataNv*, void>)&Read)),
        "vkGetDeviceFaultInfoEXT" => 0,
        _ => ((nint)((delegate* unmanaged[Cdecl]<void>)&Trap)),
    });
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Trap() => Environment.FailFast(message: "The checkpoint law reached an unwired native entry point.");
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Mark(nint commandBuffer, nint marker) => Calls!.Add(item: $"mark {commandBuffer} {marker}");
    // The queue reached the surface pass's top, finished everything before the mesh pass, reached a fragment stage in
    // the surface pass, and holds a marker no label made.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Read(nint queue, uint* count, VkCheckpointDataNv* data) {
        Calls!.Add(item: $"read {queue} {((data == null) ? 0 : *count)}");
        if (data == null) {
            *count = Reached;
            return;
        }
        data[0] = data[0] with { PCheckpointMarker = 2, Stage = 0x1 };
        data[1] = data[1] with { PCheckpointMarker = 1, Stage = 0x2000 };
        data[2] = data[2] with { PCheckpointMarker = 2, Stage = 0x80 };
        data[3] = data[3] with { PCheckpointMarker = 9, Stage = 0x1 };
    }
}
