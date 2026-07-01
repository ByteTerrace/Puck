namespace Puck.Abstractions.Gpu;

/// <summary>
/// An offscreen render target whose backing image lives in <em>shared</em> GPU memory: in addition to the normal
/// <see cref="IGpuRenderTarget"/> handles a backend draws into, it exposes a shared external handle
/// (<see cref="SharedHandle"/>) another backend on the same adapter imports to sample the result zero-copy, with
/// no host-memory round trip. Drive it with the same <c>IGpuCommandRecorder</c> / compositor as any render
/// target, then call <see cref="FinalizeForExport"/> once before handing the surface off.
/// </summary>
public interface IGpuExportableRenderTarget : IGpuRenderTarget {
    /// <summary>Gets the shared external handle (a Windows NT handle, or a POSIX file descriptor on other platforms) another backend imports to sample this target zero-copy.</summary>
    nint SharedHandle { get; }

    /// <summary>Serializes all rendering into this target and leaves its image in the cross-backend handoff
    /// state, so a consumer importing <see cref="SharedHandle"/> samples completed pixels. Call once after the
    /// compose pass for the frame, before emitting the surface.</summary>
    void FinalizeForExport();
}
