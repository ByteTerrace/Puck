namespace Puck.DirectX;

/// <summary>
/// Specifies the byte layout of CPU pixels handed to Direct3D 12 — the small set the cross-backend transport
/// uses. A consumer maps it to the matching <c>DXGI_FORMAT</c> so a surface produced by either backend samples
/// with correct channels (a Vulkan producer's output is blue-green-red-alpha; a DirectX producer's is
/// red-green-blue-alpha).
/// </summary>
public enum DirectXPixelFormat {
    /// <summary>Four 8-bit unsigned-normalized channels in red, green, blue, alpha byte order.</summary>
    R8G8B8A8Unorm,
    /// <summary>Four 8-bit unsigned-normalized channels in blue, green, red, alpha byte order.</summary>
    B8G8R8A8Unorm,
}
