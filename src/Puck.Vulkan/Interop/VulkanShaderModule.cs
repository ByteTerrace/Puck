using Puck.Shaders;
using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan.Interop;

/// <summary>
/// Owns a native shader module (<c>VkShaderModule</c>) handle, together with the shader stage it implements,
/// and destroys it when disposed.
/// </summary>
public sealed class VulkanShaderModule : IDisposable {
    private bool m_disposed;
    private readonly IVulkanShaderModuleApi m_shaderModuleApi;

    /// <summary>Gets the native <c>VkDevice</c> handle that owns the shader module.</summary>
    public nint DeviceHandle { get; }
    /// <summary>Gets the native <c>VkShaderModule</c> handle, or zero once the module has been disposed.</summary>
    public nint Handle { get; private set; }
    /// <summary>Gets the shader stage the module implements.</summary>
    public ShaderStage Stage { get; }

    /// <summary>Initializes a new instance of the <see cref="VulkanShaderModule"/> class, taking ownership of an existing native shader module handle.</summary>
    /// <param name="stage">The shader stage the module implements.</param>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle that owns the module.</param>
    /// <param name="handle">The native <c>VkShaderModule</c> handle to own.</param>
    /// <param name="shaderModuleApi">The API used to destroy the module on disposal.</param>
    /// <exception cref="ArgumentNullException"><paramref name="shaderModuleApi"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="deviceHandle"/> or <paramref name="handle"/> is zero.</exception>
    public VulkanShaderModule(
        ShaderStage stage,
        nint deviceHandle,
        nint handle,
        IVulkanShaderModuleApi shaderModuleApi
    ) {
        ArgumentNullException.ThrowIfNull(argument: shaderModuleApi);

        if (0 == deviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(deviceHandle)
            );
        }

        if (0 == handle) {
            throw new ArgumentException(
                message: "Vulkan shader-module handle must be non-zero.",
                paramName: nameof(handle)
            );
        }

        Stage = stage;
        DeviceHandle = deviceHandle;
        Handle = handle;
        m_shaderModuleApi = shaderModuleApi;
    }

    /// <summary>Destroys the owned shader module handle. Safe to call more than once.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        if (0 != Handle) {
            m_shaderModuleApi.DestroyShaderModule(
                deviceHandle: DeviceHandle,
                moduleHandle: Handle
            );
            Handle = 0;
        }

        m_disposed = true;
    }
}
