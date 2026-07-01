using System.Diagnostics.CodeAnalysis;

namespace Puck.Platform;

/// <summary>
/// Opens a live camera (webcam / capture device) as a backend-neutral frame source — the platform seam behind the
/// engine's live-camera content source. The Windows implementation is Media Foundation; other platforms (and Windows
/// without Media Foundation or a device) get <see cref="NullCameraCaptureService"/>. This is the CPU-pixel "fallback"
/// tier of the camera plan (M2): frames are read back to host memory and uploaded; the GPU-resident zero-copy tier
/// (M3) is a later step. The cross-platform shape (a service that opens a session which is an
/// <see cref="Puck.Abstractions.IFrameCaptureSource"/>) is deliberately OS-neutral so V4L2 / AVFoundation slot in later.
/// </summary>
public interface ICameraCaptureService {
    /// <summary>Whether this platform can open camera devices at all (e.g. Media Foundation is present).</summary>
    bool IsSupported { get; }

    /// <summary>Tries to open the default video capture device, negotiating a frame size near the requested one.</summary>
    /// <param name="requestedWidth">The desired frame width; the device may pick a nearby supported size.</param>
    /// <param name="requestedHeight">The desired frame height.</param>
    /// <param name="session">When this returns <see langword="true"/>, the opened live session; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if a device was opened.</returns>
    bool TryOpenDefault(int requestedWidth, int requestedHeight, [NotNullWhen(true)] out ICameraCaptureSession? session);
}
