namespace Puck.Abstractions;

/// <summary>
/// A compute storage image whose backing texture lives in <em>shared</em> GPU memory: in addition to the normal
/// <see cref="IGpuStorageImage"/> handles a compute shader writes, it exposes a shared external handle
/// (<see cref="SharedHandle"/>) another backend on the same adapter imports to sample the result zero-copy, with
/// no host-memory round trip. It is the compute-dispatch analog of <see cref="IGpuExportableRenderTarget"/>: drive
/// it with the same neutral <c>IGpuComputeRecorder</c> as any storage image, transition it to
/// <see cref="GpuImageLayout.External"/> as the final recorded step, then call <see cref="FinalizeForExport"/> once
/// before handing the surface off.
/// </summary>
public interface IGpuExportableStorageImage : IGpuStorageImage {
    /// <summary>Gets the shared external handle (a Windows NT handle, or a POSIX file descriptor on other platforms) another backend imports to sample this image zero-copy.</summary>
    nint SharedHandle { get; }

    /// <summary>Blocks until the GPU has finished the compute work written into this image, so a consumer importing
    /// <see cref="SharedHandle"/> samples completed pixels. The handoff layout transition to
    /// <see cref="GpuImageLayout.External"/> is recorded by the producer before submit; this call only drains the
    /// producer's queue. Call once per frame after submitting, before emitting the surface.</summary>
    void FinalizeForExport();
}
