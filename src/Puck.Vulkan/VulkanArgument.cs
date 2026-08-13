namespace Puck.Vulkan;

/// <summary>
/// Argument guards for native Vulkan handles passed across the managed boundary.
/// </summary>
public static class VulkanArgument {
    /// <summary>Throws when a required native Vulkan handle is zero.</summary>
    /// <param name="handle">The native handle to validate.</param>
    /// <param name="handleDescription">A short noun phrase naming the handle kind (for example, <c>"logical-device"</c>), placed into the failure message as <c>Vulkan {handleDescription} handle must be non-zero.</c></param>
    /// <param name="paramName">The name of the parameter carrying the handle, reported by the thrown exception.</param>
    /// <exception cref="ArgumentException"><paramref name="handle"/> is zero.</exception>
    public static void RequireHandle(nint handle, string handleDescription, string paramName) {
        if (0 == handle) {
            throw new ArgumentException(
                message: $"Vulkan {handleDescription} handle must be non-zero.",
                paramName: paramName
            );
        }
    }
}
