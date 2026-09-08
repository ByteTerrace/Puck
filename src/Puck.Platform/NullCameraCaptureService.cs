using System.Diagnostics.CodeAnalysis;

namespace Puck.Platform;

/// <summary>The "no camera" fallback: reports unsupported and never opens a device. Registered on platforms without a
/// camera backend, so a live-camera content source faults cleanly rather than failing.</summary>
public sealed class NullCameraCaptureService : ICameraCaptureService {
    /// <inheritdoc/>
    public bool IsSupported => false;

    /// <inheritdoc/>
    public IReadOnlyList<CameraDeviceInfo> EnumerateDevices() => [];
    /// <inheritdoc/>
    public bool TryOpenPixels(string deviceId, ReadOnlySpan<CameraStreamRequest> streams, [NotNullWhen(true)] out ICameraGraph<ICameraPixelStream>? graph) {
        graph = null;

        return false;
    }
    /// <inheritdoc/>
    public bool TryOpenShared(long adapterLuid, string deviceId, ReadOnlySpan<CameraStreamRequest> streams, [NotNullWhen(true)] out ICameraGraph<ICameraSharedStream>? graph) {
        graph = null;

        return false;
    }
}
