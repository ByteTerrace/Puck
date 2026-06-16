using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// The color value to clear an image to, given as four floating-point components (R, G, B, A). The
/// components are interpreted according to the image's format at clear time.
/// </summary>
/// <remarks>
/// EXCEPTION (not 1:1): VkClearColorValue is a C union (float32[4] / int32[4] / uint32[4]). Only the float32[4] view is
/// bound; all three members are 16 B, so the size is unaffected.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct VkClearColorValue(float float32_0, float float32_1, float float32_2, float float32_3) {
    /// <summary>The red component of the clear color.</summary>
    public readonly float Float32_0 = float32_0;
    /// <summary>The green component of the clear color.</summary>
    public readonly float Float32_1 = float32_1;
    /// <summary>The blue component of the clear color.</summary>
    public readonly float Float32_2 = float32_2;
    /// <summary>The alpha component of the clear color.</summary>
    public readonly float Float32_3 = float32_3;
}
