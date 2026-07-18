namespace Puck.Platform;

/// <summary>
/// An owned native-image feed whose platform producer pumps independently. <see cref="IFrameCaptureSource.TryCapture"/>
/// acquires an already completed latest frame and never waits for the target or capture device; the returned frame's
/// storage remains valid until the next <see cref="IFrameCaptureSource.TryCapture"/> call on the same feed. Consumption
/// is safe to race with <see cref="IDisposable.Dispose"/>; a losing read returns no frame rather than exposing released
/// storage.
/// </summary>
public interface INativeImageCaptureFeed : IFrameCaptureSource, IDisposable {
    /// <summary>Gets whether the target or capture session has permanently ended.</summary>
    bool IsEnded { get; }

    /// <summary>Gets whether the live source extent differs from the attached GPU targets' extent, in which case GPU
    /// publishing is paused until <see cref="AttachGpuTargets"/> is called again with matching targets. Always
    /// <see langword="false"/> when no GPU targets are attached.</summary>
    bool GpuTargetsOutdated { get; }

    /// <summary>Gets a monotonically increasing counter of frames copied into the GPU targets; a consumer compares it
    /// against the value it last sampled to skip unchanged frames (the newest-frame-wins drop policy). Never resets
    /// across the feed's lifetime.</summary>
    long GpuRevision { get; }

    /// <summary>Gets the index (into <see cref="NativeImageGpuCaptureTargets.SharedTargetHandles"/>) of the most
    /// recently completed GPU copy, or <c>-1</c> until the first copy into the currently attached targets.</summary>
    int LatestGpuSlot { get; }

    /// <summary>Gets the live capture source height in pixels; it updates on window resize or monitor mode change and is
    /// the height the consumer should size its GPU targets to.</summary>
    int SourceHeight { get; }

    /// <summary>Gets the live capture source width in pixels; it updates on window resize or monitor mode change and is
    /// the width the consumer should size its GPU targets to.</summary>
    int SourceWidth { get; }

    /// <summary>Enables or replaces GPU publishing into the given consumer-provisioned shared targets. The feed opens
    /// each handle once on its capture device; a subsequent call replaces the set and safely releases the previously
    /// opened targets. Thread-safe against the capture callback.</summary>
    /// <param name="targets">The shared targets, sized to the live source extent (<see cref="SourceWidth"/> ×
    /// <see cref="SourceHeight"/>).</param>
    void AttachGpuTargets(NativeImageGpuCaptureTargets targets);
}
