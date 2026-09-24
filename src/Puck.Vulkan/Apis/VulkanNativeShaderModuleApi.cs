using System.Runtime.InteropServices;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// The native implementation of <see cref="IVulkanShaderModuleApi"/>, marshaling to the
/// <c>vkCreateShaderModule</c> and <c>vkDestroyShaderModule</c> entry points resolved from the Vulkan loader.
/// </summary>
public unsafe sealed class VulkanNativeShaderModuleApi : IVulkanShaderModuleApi {
    private const uint StructureTypeShaderModuleCreateInfo = 16;

    /// <inheritdoc/>
    public VkResult CreateShaderModule(VulkanShaderModuleCreateRequest request, out nint moduleHandle) {
        ArgumentNullException.ThrowIfNull(
            argument: request.Device,
            paramName: nameof(request)
        );

        var createShaderModule = request.Device.CreateShaderModule;

        var spirVBytes = request.SpirVBytes.ToArray();
        var codeHandle = GCHandle.Alloc(
            type: GCHandleType.Pinned,
            value: spirVBytes
        );

        try {
            var createInfo = new VkShaderModuleCreateInfo {
                CodeSize = ((nuint)spirVBytes.Length),
                PCode = codeHandle.AddrOfPinnedObject(),
                SType = StructureTypeShaderModuleCreateInfo,
            };

            return createShaderModule(
                request.Device.Handle,
                in createInfo,
                0,
                out moduleHandle
            );
        } finally {
            codeHandle.Free();
        }
    }
    /// <inheritdoc/>
    public void DestroyShaderModule(VulkanDeviceCommands device, nint moduleHandle) =>
        device?.Destroy(
            destroy: device.DestroyShaderModule,
            handle: moduleHandle
        );
}
