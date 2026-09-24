namespace Puck.Abstractions.Gpu;

/// <summary>
/// An image whose backing memory is <em>shared</em>: beyond the normal <see cref="IGpuImage"/> handles, it exposes a
/// shared external handle (<see cref="SharedHandle"/>) another backend on the same adapter imports to sample the result
/// zero-copy, with no host-memory round trip. The producer records its work through the same neutral recorders as any
/// image, transitions it to <see cref="GpuImageLayout.External"/> as its final recorded step, submits, then calls
/// <see cref="FinalizeForExport"/> once before handing the surface off.
/// </summary>
public interface IGpuExportableImage : IGpuImage {
    /// <summary>Gets the shared external handle (a Windows NT handle, or a POSIX file descriptor on other platforms) another backend imports to sample this image zero-copy.</summary>
    nint SharedHandle { get; }

    /// <summary>Blocks until the GPU has finished the work written into this image, so a consumer importing
    /// <see cref="SharedHandle"/> samples completed pixels. The handoff transition to
    /// <see cref="GpuImageLayout.External"/> is recorded by the producer before submit; this call only drains the
    /// producer's queue. Call once per frame after submitting, before emitting the surface.</summary>
    void FinalizeForExport();
}
