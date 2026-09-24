using Puck.Shaders;
using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan.Interop;

/// <summary>
/// Owns a native shader module (<c>VkShaderModule</c>) handle, together with the shader stage it implements,
/// and destroys it when disposed.
/// </summary>
public sealed class VulkanShaderModule : IGpuShaderModule {
    private readonly IVulkanShaderModuleApi m_shaderModuleApi;

    private bool m_disposed;

    /// <summary>Gets the command table of the logical device that owns the shader module.</summary>
    public VulkanDeviceCommands Device { get; }
    /// <summary>Gets the native <c>VkShaderModule</c> handle, or zero once the module has been disposed.</summary>
    public nint Handle { get; private set; }
    /// <summary>Gets the shader stage the module implements.</summary>
    public ShaderStage Stage { get; }

    /// <summary>Initializes a new instance of the <see cref="VulkanShaderModule"/> class, taking ownership of an existing native shader module handle.</summary>
    /// <param name="stage">The shader stage the module implements.</param>
    /// <param name="device">The command table of the logical device that owns the module.</param>
    /// <param name="handle">The native <c>VkShaderModule</c> handle to own.</param>
    /// <param name="shaderModuleApi">The API used to destroy the module on disposal.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> or <paramref name="shaderModuleApi"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="handle"/> is zero.</exception>
    public VulkanShaderModule(
        ShaderStage stage,
        VulkanDeviceCommands device,
        nint handle,
        IVulkanShaderModuleApi shaderModuleApi
    ) {
        ArgumentNullException.ThrowIfNull(argument: shaderModuleApi);

        ArgumentNullException.ThrowIfNull(argument: device);

        VulkanArgument.RequireHandle(
            handle: handle,
            handleDescription: "shader-module",
            paramName: nameof(handle)
        );

        Stage = stage;
        Device = device;
        Handle = handle;
        m_shaderModuleApi = shaderModuleApi;
    }

    /// <summary>Destroys the owned shader module handle. Safe to call more than once.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_shaderModuleApi.DestroyShaderModule(
            device: Device,
            moduleHandle: Handle
        );
        Handle = 0;
        m_disposed = true;
    }
}
