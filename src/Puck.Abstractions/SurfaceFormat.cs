namespace Puck.Abstractions;

/// <summary>
/// Specifies the backend-neutral pixel format of a <see cref="Surface"/>.
/// </summary>
public enum SurfaceFormat {
    /// <summary>An unknown or unspecified format.</summary>
    Unknown = 0,
    /// <summary>The R8G8B8A8 unsigned normalized format.</summary>
    R8G8B8A8Unorm,
    /// <summary>The B8G8R8A8 unsigned normalized format.</summary>
    B8G8R8A8Unorm,
}
