using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Memory;
using Puck.Abstractions.Windowing;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Factories;
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
