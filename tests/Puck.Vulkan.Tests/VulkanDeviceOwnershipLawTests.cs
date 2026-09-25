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
/// an allocation refuses its teardown by naming the leak. Every teardown reaches the native destroy exactly once, for its
/// own device, before the leak is named. The command tables resolve every entry point to a trap that no law reaches, and
/// the device API records each table whose device it destroys.</summary>
public sealed unsafe class VulkanDeviceOwnershipLawTests {
    private const nint FirstDeviceHandle = 0x0D00;
    private const nint SecondDeviceHandle = 0x0E00;

    [Fact]
    public void AHolderRefusesADeviceOtherThanTheOneItsResourcesAreOn() {
        var api = new DestroyRecordingDeviceApi();
        var first = LogicalDevice(api: api, handle: FirstDeviceHandle);
        var second = LogicalDevice(api: api, handle: SecondDeviceHandle);

        VulkanDeviceOwnership.ThrowIfOtherDevice(held: null, holder: nameof(VulkanSurfaceImport), offered: first);
        VulkanDeviceOwnership.ThrowIfOtherDevice(held: first, holder: nameof(VulkanSurfaceImport), offered: first);

        var refusal = Assert.Throws<InvalidOperationException>(testCode: () => VulkanDeviceOwnership.ThrowIfOtherDevice(
            held: first,
            holder: nameof(VulkanSurfaceImport),
            offered: second
        ));

        Assert.StartsWith(expectedStartString: $"A {nameof(VulkanSurfaceImport)} was handed a device other", actualString: refusal.Message);
        Assert.Empty(collection: api.Destroyed);
    }
    [Fact]
    public void AReleaseAfterTheDeviceIsDestroyedIsRefusedByName() {
        var api = new DestroyRecordingDeviceApi();
        var device = LogicalDevice(api: api, handle: FirstDeviceHandle);

        VulkanDeviceOwnership.ThrowIfDestroyed(held: device, holder: nameof(VulkanSurfaceImport));
        Assert.Empty(collection: api.Destroyed);
        device.Dispose();
        device.Dispose();

        // The native destroy runs once, for this device's table. The table outlives the destroy only as the handle its
        // memory entries are keyed by; the owner refuses every further use of it.
        Assert.Same(expected: device.Commands, actual: Assert.Single(collection: api.Destroyed));
        Assert.True(condition: device.IsDisposed);
        Assert.Equal(expected: FirstDeviceHandle, actual: device.Commands.Handle);
        _ = Assert.Throws<ObjectDisposedException>(testCode: device.WaitIdle);
        device.TryWaitIdle();

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
        var api = new DestroyRecordingDeviceApi();
        var lost = LogicalDevice(api: api, handle: FirstDeviceHandle, memory: memory);
        var replacement = LogicalDevice(api: api, handle: SecondDeviceHandle, memory: memory);

        lost.Commands.CountAllocated(allocationSize: 4096UL, memoryHandle: Reused, role: GpuMemoryRole.DeviceLocal);
        replacement.Commands.CountAllocated(allocationSize: 256UL, memoryHandle: Reused, role: GpuMemoryRole.DeviceLocal);

        var refusal = Assert.Throws<InvalidOperationException>(testCode: lost.Dispose);

        Assert.True(condition: lost.IsDisposed);
        Assert.Equal(
            actual: refusal.Message,
            expected: "memory.vulkan: device 0xD00 was torn down holding 1 counted allocation(s) their owners never released: 0x3000 (4096 bytes)."
        );
        // The refusal comes after the native destroy, which ran once and for the lost device only.
        Assert.Same(expected: lost.Commands, actual: Assert.Single(collection: api.Destroyed));
        lost.Dispose();
        _ = Assert.Single(collection: api.Destroyed);
        Assert.True(condition: memory.CountReleased(allocation: Reused, device: SecondDeviceHandle));
        replacement.Dispose();
        Assert.Equal(expected: new[] { lost.Commands, replacement.Commands }, actual: api.Destroyed);
        Assert.Equal(expected: (4352L, 256L, 4096L), actual: (memory.Read(kind: GpuDeviceMemoryWork.Allocated), memory.Read(kind: GpuDeviceMemoryWork.Released), memory.Held));
    }

    private static VulkanLogicalDevice LogicalDevice(DestroyRecordingDeviceApi api, nint handle, GpuDeviceMemoryWork? memory = null) =>
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
            logicalDeviceApi: api,
            physicalDevice: default,
            presentQueue: default
        );
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint ResolveTrap(nint handle, byte* name) =>
        ((nint)((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint>)&Trap));
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Trap(nint first, nint second, nint third, nint fourth) => 0;

    // Records each table whose device it destroys, in order, as the native destroy would reach it.
    private sealed class DestroyRecordingDeviceApi : IVulkanLogicalDeviceApi {
        public List<VulkanDeviceCommands> Destroyed { get; } = [];

        public VkResult CreateLogicalDevice(VulkanLogicalDeviceCreateRequest request, out VulkanDeviceCommands? device) =>
            throw new NotSupportedException();
        public void DestroyDevice(VulkanDeviceCommands device) =>
            Destroyed.Add(item: device);
        public nint GetDeviceQueue(VulkanDeviceCommands device, uint queueFamilyIndex, uint queueIndex) =>
            throw new NotSupportedException();
        public VkResult WaitIdle(VulkanDeviceCommands device) =>
            VkResult.Success;
    }
}
