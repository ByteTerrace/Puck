namespace Puck.Abstractions;

/// <summary>
/// A backend-neutral storage image a compute shader writes and a consumer later samples: its native image and
/// image-view handles plus its pixel extent, owned for its lifetime.
/// </summary>
public interface IGpuStorageImage : IDisposable {
    /// <summary>Gets the native image handle.</summary>
    nint ImageHandle { get; }
    /// <summary>Gets the native image-view handle (bound as the storage-image descriptor and sampled afterward).</summary>
    nint ImageViewHandle { get; }
    /// <summary>Gets the image height in pixels.</summary>
    uint Height { get; }
    /// <summary>Gets the image width in pixels.</summary>
    uint Width { get; }
}
