namespace Puck.Abstractions.Gpu;

/// <summary>
/// An image whose backing memory is <em>shared</em>: beyond the normal <see cref="IGpuImage"/> handles, it exposes a
/// shared external handle (<see cref="SharedHandle"/>) another backend on the same adapter imports to sample the result
/// zero-copy, with no host-memory round trip. The producer records its work through the same neutral recorders as any
/// image, transitions it to <see cref="GpuImageLayout.External"/> as its final recorded step, submits, then calls
/// <see cref="CompleteWrite"/> once before handing the surface off.
/// </summary>
public interface IGpuExportableImage : IGpuImage {
    /// <summary>Gets the shared external handle (a Windows NT handle, or a POSIX file descriptor on other platforms) another backend imports to sample this image zero-copy.</summary>
    nint SharedHandle { get; }
    /// <summary>Gets the shared fence (a Windows NT handle) <see cref="CompleteWrite"/> signals, which a consumer on
    /// another device opens to wait for a write before it reads; zero when the image has none, and
    /// <see cref="CompleteWrite"/> waits for the write on the CPU instead.</summary>
    nint SharedFenceHandle { get; }

    /// <summary>Completes the work submitted into this image so far for a consumer on another device: queues the next
    /// value of the image's shared fence on the producer's queue behind that work and returns it, which the consumer's
    /// work waits for on its own device, so nothing blocks. An image with no shared fence blocks until the producer's
    /// queue has finished the work and returns zero. The handoff transition to <see cref="GpuImageLayout.External"/> is
    /// recorded by the producer before submit. Called once per frame after submitting, before emitting the
    /// surface.</summary>
    /// <returns>The fence value the write signals, or zero when it has finished before this returned.</returns>
    /// <exception cref="ObjectDisposedException">The image was disposed.</exception>
    /// <exception cref="DeviceLostException">The device was lost.</exception>
    ulong CompleteWrite();
}
