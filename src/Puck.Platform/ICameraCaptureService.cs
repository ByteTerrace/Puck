using System.Diagnostics.CodeAnalysis;

namespace Puck.Platform;

/// <summary>
/// Opens a live camera (webcam / capture device) as a backend-neutral frame source — the platform seam behind the
/// engine's live-camera content source. The Windows implementation is Media Foundation; other platforms (and Windows
/// without Media Foundation or a device) get <see cref="NullCameraCaptureService"/>. Two tiers, both behind this seam:
/// the CPU-pixel tier (M2, <see cref="TryOpenDefault"/> — frames read back to host memory and uploaded) and the
/// GPU-resident zero-copy tier (M3, <see cref="TryOpenSharedDefault"/> — frames converted on-GPU and copied into
/// consumer-provisioned shared textures, never visiting host memory). The interface is OS-neutral.
/// </summary>
public interface ICameraCaptureService {
    /// <summary>Whether this platform can open camera devices at all (e.g. Media Foundation is present).</summary>
    bool IsSupported { get; }

    /// <summary>Tries to open the default video capture device, negotiating a capture mode near the requested envelope:
    /// the smallest native frame size covering the requested extent (else the largest available), at the lowest native
    /// frame rate covering the requested rate (else the highest available). The device remains authoritative — read the
    /// negotiated result off the session.</summary>
    /// <param name="requestedWidth">The desired frame width; the device may pick a nearby supported size.</param>
    /// <param name="requestedHeight">The desired frame height.</param>
    /// <param name="requestedRateHz">The desired capture rate; zero prefers the device's fastest mode at the chosen size.</param>
    /// <param name="session">When this returns <see langword="true"/>, the opened live session; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if a device was opened.</returns>
    bool TryOpenDefault(int requestedWidth, int requestedHeight, uint requestedRateHz, [NotNullWhen(true)] out ICameraCaptureSession? session);
    /// <summary>Tries to open the default video capture device on the GPU-resident zero-copy tier: the platform's
    /// decode device (LUID-matched to the consumer's adapter) converts frames on-GPU and copies them into shared
    /// textures the consumer provisions after negotiation (see <see cref="ICameraSharedCaptureSession.Start"/>).
    /// The capture mode is negotiated by the same envelope rule as <see cref="TryOpenDefault"/>.
    /// <para>Both hosts' shared camera feeds ride this tier — the Direct3D 12 host samples the shared targets it
    /// created directly, and the Vulkan host imports their NT handles onto its render device — falling back to
    /// <see cref="TryOpenDefault"/>'s CPU-pixel tier when the open refuses (no device, no adapter LUID, a failed
    /// target or import).</para></summary>
    /// <param name="adapterLuid">The consumer render device's adapter LUID; the decode device must share the adapter for the shared textures to be openable.</param>
    /// <param name="requestedWidth">The desired output width; the negotiated size is on the returned session.</param>
    /// <param name="requestedHeight">The desired output height.</param>
    /// <param name="requestedRateHz">The desired capture rate; zero prefers the device's fastest mode at the chosen size.</param>
    /// <param name="session">When this returns <see langword="true"/>, the negotiated (not yet streaming) session; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if a device was opened on the GPU tier.</returns>
    bool TryOpenSharedDefault(long adapterLuid, int requestedWidth, int requestedHeight, uint requestedRateHz, [NotNullWhen(true)] out ICameraSharedCaptureSession? session);
}
