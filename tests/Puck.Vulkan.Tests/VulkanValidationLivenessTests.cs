using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Memory;
using Puck.Abstractions.Windowing;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Factories;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Xunit;

namespace Puck.Vulkan.Tests;

/// <summary>Holds the process's console error stream to one test at a time.</summary>
[CollectionDefinition(name: nameof(ConsoleErrorCollection), DisableParallelization = true)]
public sealed class ConsoleErrorCollection {
}
/// <summary>
/// Proves the validation messenger is live, so a World run with <c>--debug-layers</c> that prints no
/// <c>[vulkan-debug] validation</c> line is evidence rather than silence. An instance created exactly as
/// <see cref="VulkanInstanceFactory"/> creates one with validation receives one deliberate violation:
/// <c>vkEnumeratePhysicalDevices</c> with a null count pointer. The Khronos validation layer's stateless parameter
/// validation reports <c>VUID-vkEnumeratePhysicalDevices-pPhysicalDeviceCount-parameter</c> and skips the call, so
/// neither the loader nor the driver ever reads the null pointer. The test skips when the host has no Vulkan loader,
/// driver or validation layer.
/// </summary>
[Collection(name: nameof(ConsoleErrorCollection))]
public sealed class VulkanValidationLivenessTests {
    [Fact]
    public unsafe void ADeliberateViolationReachesTheValidationMessenger() {
        if (!OperatingSystem.IsWindows()) {
            Assert.Skip(reason: "The instance is created with the Win32 surface extension.");
        }

        var captured = new StringWriter();
        var error = Console.Error;
        VkResult result;

        Console.SetError(newError: captured);

        try {
            var procedures = new VulkanProcResolver();
            VulkanInstance instance;

            try {
                instance = new VulkanInstanceFactory(instanceApi: new VulkanNativeInstanceApi(
                    allocator: new HeapAllocator(),
                    procedures: procedures
                )).Create(
                    applicationName: nameof(VulkanValidationLivenessTests),
                    displayKind: NativeDisplayKind.Win32,
                    enableValidation: true
                );
            } catch (GpuDeviceUnavailableException exception) {
                Assert.Skip(reason: $"No Vulkan loader, driver or validation layer: {exception.Message}");

                return;
            }

            using (instance) {
                var enumerate = ((delegate* unmanaged[Cdecl]<nint, uint*, nint, VkResult>)procedures.ResolveInstanceProc(
                    functionName: "vkEnumeratePhysicalDevices"u8,
                    instanceHandle: instance.Commands.Handle
                ));

                result = enumerate(instance.Commands.Handle, null, 0);
            }
        } finally {
            Console.SetError(newError: error);
        }

        var lines = captured.ToString();

        // The layer's own words, for the record of a run.
        Console.Error.Write(value: lines);

        Assert.NotEqual(
            actual: result,
            expected: VkResult.Success
        );
        Assert.Contains(
            actualString: lines,
            expectedSubstring: "[vulkan-debug] validation ERROR"
        );
        Assert.Contains(
            actualString: lines,
            expectedSubstring: "VUID-vkEnumeratePhysicalDevices-pPhysicalDeviceCount-parameter"
        );
    }
    /// <summary>A buffer created through <see cref="VulkanGpuBufferFactory"/> with naming on, then left alive when its
    /// device is destroyed, is reported by the validation layer's object tracker under the name it was created with
    /// (<see cref="GpuObjectName"/>, applied by <see cref="VulkanGpuObjectNaming"/>), so a <c>[vulkan-debug]
    /// validation</c> line names the object it reports.</summary>
    [Fact]
    public void ALeakedObjectIsReportedByItsNameWhenTheDeviceIsDestroyed() {
        if (!OperatingSystem.IsWindows()) {
            Assert.Skip(reason: "The instance is created with the Win32 surface extension.");
        }

        var captured = new StringWriter();
        var error = Console.Error;

        Console.SetError(newError: captured);

        try {
            var allocator = new HeapAllocator();
            var procedures = new VulkanProcResolver();
            VulkanInstance instance;

            try {
                instance = new VulkanInstanceFactory(instanceApi: new VulkanNativeInstanceApi(
                    allocator: allocator,
                    procedures: procedures
                )).Create(
                    applicationName: nameof(VulkanValidationLivenessTests),
                    displayKind: NativeDisplayKind.Win32,
                    enableValidation: true
                );
            } catch (GpuDeviceUnavailableException exception) {
                Assert.Skip(reason: $"No Vulkan loader, driver or validation layer: {exception.Message}");

                return;
            }

            using (instance) {
                var physicalDeviceApi = new VulkanNativePhysicalDeviceApi(allocator: allocator);
                var physicalDevice = GraphicsDevice(
                    instance: instance,
                    physicalDeviceApi: physicalDeviceApi
                );
                VulkanLogicalDevice device;

                try {
                    device = new VulkanLogicalDeviceFactory(
                        logicalDeviceApi: new VulkanNativeLogicalDeviceApi(
                            allocator: allocator,
                            procedures: procedures
                        ),
                        physicalDeviceApi: physicalDeviceApi,
                        pipelineCacheStore: null,
                        pipelineCacheWork: new GpuPipelineCacheWork(backend: "vulkan")
                    ).Create(
                        instance: instance,
                        physicalDevice: physicalDevice
                    );
                } catch (GpuDeviceUnavailableException exception) {
                    Assert.Skip(reason: $"No usable Vulkan device: {exception.Message}");

                    return;
                }

                var context = new LeakContext(
                    Instance: instance,
                    LogicalDevice: device,
                    PhysicalDevice: physicalDevice
                );

                _ = new VulkanGpuBufferFactory(
                    bufferApi: new VulkanNativeBufferApi(),
                    deviceContext: context,
                    naming: new VulkanGpuObjectNaming(
                        deviceContext: context,
                        isEnabled: true
                    )
                ).CreateDeviceLocal(
                    name: new GpuObjectName(
                        owner: "law",
                        part: "leaked"
                    ),
                    sizeBytes: 256,
                    usage: GpuBufferUsage.Storage
                );
                device.Dispose();
            }
        } finally {
            Console.SetError(newError: error);
        }

        var lines = captured.ToString();

        // The layer's own words, for the record of a run.
        Console.Error.Write(value: lines);

        // A message spans lines (the object tracker lists the leaked objects on the next), so each message runs from its
        // prefix to the next one.
        Assert.Contains(
            collection: lines.Split(
                options: StringSplitOptions.None,
                separator: "[vulkan-debug] "
            ),
            filter: static message => (message.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "validation "
            ) && message.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "VkBuffer "
            ) && message.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "[law/leaked]"
            ))
        );
    }

    // The first physical device with a graphics queue family, discrete preferred; the leak needs no surface, so the
    // graphics family stands in for the present family too.
    private static VkPhysicalDevice GraphicsDevice(VulkanInstance instance, VulkanNativePhysicalDeviceApi physicalDeviceApi) {
        VkPhysicalDevice? chosen = null;

        foreach (var handle in physicalDeviceApi.EnumeratePhysicalDevices(instance: instance.Commands)) {
            var graphics = physicalDeviceApi.GetQueueFamilies(
                instance: instance.Commands,
                physicalDeviceHandle: handle
            ).FirstOrDefault(predicate: static family => ((0U != family.QueueCount) && (0 != (family.Flags & VkQueueFlags.Graphics))));

            if (0U == graphics.QueueCount) {
                continue;
            }

            var candidate = new VkPhysicalDevice(
                deviceType: physicalDeviceApi.GetPhysicalDeviceType(
                    instance: instance.Commands,
                    physicalDeviceHandle: handle
                ),
                handle: handle,
                queueFamilySelection: new VulkanQueueFamilySelection(
                    graphicsFamilyIndex: graphics.Index,
                    presentFamilyIndex: graphics.Index
                )
            );

            if ((chosen is null) || (VkPhysicalDeviceType.DiscreteGpu == candidate.DeviceType)) {
                chosen = candidate;
            }
        }

        if (chosen is null) {
            Assert.Skip(reason: "No Vulkan device has a graphics queue family.");
        }

        return chosen.Value;
    }

    // A device context over a device created without a surface; nothing the leak creates reads the surface.
    private sealed record LeakContext(VulkanInstance Instance, VulkanLogicalDevice LogicalDevice, VkPhysicalDevice PhysicalDevice) : IVulkanDeviceContext {
        public VulkanSurface Surface => throw new InvalidOperationException(message: "The leak's device has no surface.");
    }
    private sealed unsafe class HeapAllocator : IAllocator {
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
}
