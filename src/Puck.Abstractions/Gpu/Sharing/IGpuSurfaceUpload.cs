namespace Puck.Abstractions.Gpu;

/// <summary>
/// Uploads CPU pixels to a GPU image for sampling, on the device context the <see cref="IGpuSurfaceTransferFactory"/>
/// that created it is bound to. The upload object owns the image (and staging resources) behind the handles it
/// returns, holds them on the device of its first upload, and is released before that device goes, a device loss
/// included. Both backends implement it once, as <see cref="GpuSurfaceUpload"/>.
/// </summary>
public interface IGpuSurfaceUpload : IDisposable {
    /// <summary>Uploads an image and returns a native image view handle over it, ready for sampling by any work submitted
    /// to the same queue after this call returns: the copy is queue-ordered ahead of that work, and the caller's pixel
    /// buffer is free to reuse. The returned handle is owned by this upload object (the caller never destroys it) and is
    /// only guaranteed valid until the next <see cref="Upload"/> on this instance or this object's disposal; the same view
    /// is returned while the extent and format are unchanged.</summary>
    /// <param name="pixels">The image's rows of texels, tightly packed
    /// (<see cref="GpuPixelFormats.ImageByteLength"/>).</param>
    /// <param name="format">The pixel format.</param>
    /// <param name="width">The width, in texels.</param>
    /// <param name="height">The height, in texels.</param>
    /// <returns>The native image view handle, owned by this upload object.</returns>
    /// <exception cref="ArgumentException"><paramref name="pixels"/> is not exactly the image's length.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is zero, or exceeds the device's image limit (the
    /// current image and its view stay).</exception>
    /// <exception cref="NotSupportedException">The device cannot sample <paramref name="format"/>; the message names
    /// the format and the device.</exception>
    nint Upload(ReadOnlyMemory<byte> pixels, GpuPixelFormat format, uint width, uint height);
    /// <summary>Releases the upload as <see cref="IDisposable.Dispose"/> does, without first waiting for the device to go
    /// idle: the owner calls it only once no submission can still sample its images, after a checkpoint ordered behind
    /// every reader has completed or after a device loss. An upload whose release waits for nothing releases the same
    /// way either call.</summary>
    void DisposeCompleted() => Dispose();
}
