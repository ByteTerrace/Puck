using System.Diagnostics.CodeAnalysis;

namespace Puck.Platform;

/// <summary>The graceful "no camera" fallback: reports unsupported and never opens a device. Registered on platforms
/// without a camera backend (or where Media Foundation / a device is unavailable), so a live-camera content source
/// cleanly falls back rather than failing.</summary>
public sealed class NullCameraCaptureService : ICameraCaptureService {
    /// <inheritdoc/>
    public bool IsSupported => false;

    /// <inheritdoc/>
    public bool TryOpenDefault(int requestedWidth, int requestedHeight, [NotNullWhen(true)] out ICameraCaptureSession? session) {
        session = null;

        return false;
    }
}
