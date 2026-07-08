namespace Puck.Abstractions.Gpu;

/// <summary>
/// Reads back an offscreen image from the GPU to host memory. The readback object owns the host-visible staging
/// resources behind the memory it returns.
/// </summary>
public interface IGpuSurfaceReadback : IDisposable {
    /// <summary>Copies the source image to a host-visible buffer, blocks until the copy completes, and returns the
    /// pixel data. The source image must be shader-readable and is left shader-readable. The returned memory is
    /// only guaranteed valid until the next <see cref="Read"/> on this instance (the Direct3D 12 implementation
    /// reuses one output buffer across calls) — copy it if it must live longer.</summary>
    /// <param name="deviceContext">The GPU device context.</param>
    /// <param name="sourceImageHandle">The native image handle to read back from.</param>
    /// <param name="format">The pixel format.</param>
    /// <param name="width">The width, in pixels.</param>
    /// <param name="height">The height, in pixels.</param>
    /// <param name="bytesPerPixel">The number of bytes per pixel.</param>
    /// <returns>The tightly packed pixel data; valid until the next <see cref="Read"/> or this object's disposal.</returns>
    ReadOnlyMemory<byte> Read(IGpuDeviceContext deviceContext, nint sourceImageHandle, GpuPixelFormat format, uint width, uint height, uint bytesPerPixel);
    /// <summary>Records the image-to-staging copy and submits it under a completion fence WITHOUT waiting — the
    /// non-blocking counterpart of <see cref="Read"/>, for a pipelined caller that polls <see cref="IsReadComplete"/>
    /// on a later frame and only then <see cref="MapPixels"/> maps the result. The source image must be
    /// shader-readable and is left shader-readable. At most ONE read may be in flight per instance: this must not be
    /// called again until the outstanding submit has been mapped (the single-in-flight contract <see cref="Read"/>
    /// already implies).</summary>
    /// <param name="deviceContext">The GPU device context.</param>
    /// <param name="sourceImageHandle">The native image handle to read back from.</param>
    /// <param name="format">The pixel format.</param>
    /// <param name="width">The width, in pixels.</param>
    /// <param name="height">The height, in pixels.</param>
    /// <param name="bytesPerPixel">The number of bytes per pixel.</param>
    void SubmitRead(IGpuDeviceContext deviceContext, nint sourceImageHandle, GpuPixelFormat format, uint width, uint height, uint bytesPerPixel);
    /// <summary>Polls, WITHOUT blocking, whether the outstanding <see cref="SubmitRead"/>'s copy has completed.
    /// Returns <see langword="false"/> when no read is in flight or the copy has not yet finished; <see langword="true"/>
    /// once it has. Fail-safe: a torn-down or lost device yields <see langword="false"/> rather than throwing, because
    /// this is polled from the render loop.</summary>
    /// <returns>Whether the last <see cref="SubmitRead"/> has completed.</returns>
    bool IsReadComplete();
    /// <summary>Returns the pixels the last COMPLETED <see cref="SubmitRead"/> copied — the same reusable staging view
    /// <see cref="Read"/> returns (valid only until the next <see cref="Read"/> or <see cref="SubmitRead"/> reuses the
    /// staging buffer; copy it if it must outlive that) — and clears the in-flight state so a new <see cref="SubmitRead"/>
    /// may be issued.</summary>
    /// <returns>The tightly packed pixel data from the last completed read.</returns>
    ReadOnlyMemory<byte> MapPixels();
}
