using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Puck.Abstractions.Gpu;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;
using Xunit;

namespace Puck.Vulkan.Tests;

/// <summary>Pins, without a device, the lifetime rules of a Vulkan logical device and what is made on it: a surface
/// upload or shared-surface import (<see cref="VulkanDeviceOwnership"/>) refuses a device other than the one its
/// resources are on and refuses its release after that device is destroyed, and a device whose memory counts still hold
/// an allocation refuses its teardown by naming the leak. The command tables resolve every entry point to a trap that no
/// law reaches.</summary>
public sealed unsafe class VulkanDeviceOwnershipLawTests {
    private const nint FirstDeviceHandle = 0x0D00;
    private const nint SecondDeviceHandle = 0x0E00;

    [Fact]
    public void AHolderRefusesADeviceOtherThanTheOneItsResourcesAreOn() {
        var first = LogicalDevice(handle: FirstDeviceHandle);
        var second = LogicalDevice(handle: SecondDeviceHandle);

        VulkanDeviceOwnership.ThrowIfOtherDevice(held: null, holder: nameof(VulkanSurfaceImport), offered: first);
        VulkanDeviceOwnership.ThrowIfOtherDevice(held: first, holder: nameof(VulkanSurfaceImport), offered: first);

        var refusal = Assert.Throws<InvalidOperationException>(testCode: () => VulkanDeviceOwnership.ThrowIfOtherDevice(
            held: first,
            holder: nameof(VulkanSurfaceImport),
            offered: second
        ));

        Assert.StartsWith(expectedStartString: $"A {nameof(VulkanSurfaceImport)} was handed a device other", actualString: refusal.Message);
    }
    [Fact]
    public void AReleaseAfterTheDeviceIsDestroyedIsRefusedByName() {
        var device = LogicalDevice(handle: FirstDeviceHandle);

        VulkanDeviceOwnership.ThrowIfDestroyed(held: device, holder: nameof(VulkanSurfaceImport));
        device.Dispose();

        var refusal = Assert.Throws<InvalidOperationException>(testCode: () => VulkanDeviceOwnership.ThrowIfDestroyed(
            held: device,
            holder: nameof(VulkanSurfaceImport)
        ));

        Assert.StartsWith(expectedStartString: $"A {nameof(VulkanSurfaceImport)} was released after its device was destroyed", actualString: refusal.Message);
    }
    [Fact]
    public void TheSameHandleOnARecreatedDeviceIsItsOwnEntryAndTheLeakIsNamedAtTeardown() {
        const nint Reused = 0x3000;

        var memory = new GpuDeviceMemoryWork(backend: "vulkan");
        var lost = LogicalDevice(handle: FirstDeviceHandle, memory: memory);
        var replacement = LogicalDevice(handle: SecondDeviceHandle, memory: memory);

        lost.Commands.CountAllocated(allocationSize: 4096UL, memoryHandle: Reused, role: GpuMemoryRole.DeviceLocal);
        replacement.Commands.CountAllocated(allocationSize: 256UL, memoryHandle: Reused, role: GpuMemoryRole.DeviceLocal);

        var refusal = Assert.Throws<InvalidOperationException>(testCode: lost.Dispose);

        Assert.True(condition: lost.IsDisposed);
        Assert.Equal(
            actual: refusal.Message,
            expected: "memory.vulkan: device 0xD00 was torn down holding 1 counted allocation(s) their owners never released: 0x3000 (4096 bytes)."
        );
        Assert.True(condition: memory.CountReleased(allocation: Reused, device: SecondDeviceHandle));
        replacement.Dispose();
        Assert.Equal(expected: (4352L, 256L, 4096L), actual: (memory.Read(kind: GpuDeviceMemoryWork.Allocated), memory.Read(kind: GpuDeviceMemoryWork.Released), memory.Held));
    }

    private static VulkanLogicalDevice LogicalDevice(nint handle, GpuDeviceMemoryWork? memory = null) =>
        new(
            device: new VulkanDeviceCommands(
                deviceHandle: handle,
                memory: memory,
                procedures: new VulkanProcResolver(
                    getDeviceProcAddr: &ResolveTrap,
                    getInstanceProcAddr: &ResolveTrap
                )
            ),
            graphicsQueue: default,
            logicalDeviceApi: new DestroyOnlyDeviceApi(),
            physicalDevice: default,
            presentQueue: default
        );
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint ResolveTrap(nint handle, byte* name) =>
        ((nint)((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint>)&Trap));
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Trap(nint first, nint second, nint third, nint fourth) => 0;

    private sealed class DestroyOnlyDeviceApi : IVulkanLogicalDeviceApi {
        public VkResult CreateLogicalDevice(VulkanLogicalDeviceCreateRequest request, out VulkanDeviceCommands? device) =>
            throw new NotSupportedException();
        public void DestroyDevice(VulkanDeviceCommands device) =>
            device.Dispose();
        public nint GetDeviceQueue(VulkanDeviceCommands device, uint queueFamilyIndex, uint queueIndex) =>
            throw new NotSupportedException();
        public VkResult WaitIdle(VulkanDeviceCommands device) =>
            VkResult.Success;
    }
}
