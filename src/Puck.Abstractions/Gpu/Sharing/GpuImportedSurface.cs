namespace Puck.Abstractions.Gpu;

/// <summary>The native image and image-view handles produced by importing a shared surface. Both handles are opaque
/// outside the backend that created them and remain owned by the corresponding <see cref="IGpuSurfaceImport"/>.</summary>
/// <param name="ImageHandle">The native image/resource handle used for transfer operations.</param>
/// <param name="ImageViewHandle">The native image-view handle used for sampling.</param>
public readonly record struct GpuImportedSurface(nint ImageHandle, nint ImageViewHandle);
