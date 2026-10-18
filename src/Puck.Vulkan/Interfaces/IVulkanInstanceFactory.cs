using Puck.Vulkan.Interop;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Creates a fully configured <see cref="VulkanInstance"/>, selecting the surface extension for the display
/// kind and optionally enabling the validation layers.
/// </summary>
public interface IVulkanInstanceFactory {
    /// <summary>Creates a Vulkan instance for the given application and display kind.</summary>
    /// <param name="applicationName">The application name reported to the implementation.</param>
    /// <param name="displayKind">The native display kind, which selects the surface extension to enable.</param>
    /// <param name="validation">The validation level: above <see cref="GpuValidation.Off"/> the Khronos validation layer
    /// with synchronization validation, and at <see cref="GpuValidation.Full"/> GPU-assisted validation and the
    /// best-practices checks, NVIDIA's included.</param>
    /// <param name="debugOutput">Where the validation layer's messages are written, one <c>[vulkan-debug]</c> line each,
    /// with the line saying whether the layer is live; <see langword="null"/> writes to the process's standard error at
    /// the time of writing, where a run's validation checks read them.</param>
    /// <returns>A new, owning <see cref="VulkanInstance"/>.</returns>
    /// <exception cref="GpuDeviceUnavailableException">The host has no Vulkan loader, or instance creation failed (no installable client driver).</exception>
    VulkanInstance Create(string applicationName, NativeDisplayKind displayKind, GpuValidation validation, TextWriter? debugOutput = null);
}
