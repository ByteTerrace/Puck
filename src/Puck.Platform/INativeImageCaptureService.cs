using System.Diagnostics.CodeAnalysis;

namespace Puck.Platform;

/// <summary>Creates owned, self-pumping native image feeds.</summary>
public interface INativeImageCaptureService {
    /// <summary>Gets whether window capture is available on the current platform and desktop session.</summary>
    bool IsSupported { get; }

    /// <summary>Opens the first visible top-level window whose title contains <paramref name="windowTitleFragment"/>.</summary>
    /// <param name="windowTitleFragment">The case-insensitive title fragment used to resolve the target window.</param>
    /// <param name="width">The fixed output width in pixels.</param>
    /// <param name="height">The fixed output height in pixels.</param>
    /// <param name="refreshRateHz">The maximum compositor readback cadence in frames per second.</param>
    /// <param name="feed">The fully initialized owned feed on success; otherwise <see langword="null"/>.</param>
    /// <param name="adapterLuid">The LUID of the adapter the feed's capture device must be created on (packed
    /// <c>(HighPart &lt;&lt; 32) | LowPart</c>), so its shared targets can be opened by a render device on the same
    /// adapter; <see langword="null"/> keeps the default adapter and leaves GPU publishing unavailable.</param>
    /// <returns><see langword="true"/> when a target was resolved and its capture feed started; otherwise <see langword="false"/>.</returns>
    bool TryCreateWindowCapture(
        string windowTitleFragment,
        int width,
        int height,
        double refreshRateHz,
        [NotNullWhen(true)] out INativeImageCaptureFeed? feed,
        long? adapterLuid = null
    );

    /// <summary>Opens whole-monitor capture for the monitor at <paramref name="monitorIndex"/>.</summary>
    /// <param name="monitorIndex">The 0-based monitor index, where 0 is the primary monitor and the rest follow in enumeration order.</param>
    /// <param name="width">The fixed output width in pixels.</param>
    /// <param name="height">The fixed output height in pixels.</param>
    /// <param name="refreshRateHz">The maximum compositor readback cadence in frames per second.</param>
    /// <param name="feed">The fully initialized owned feed on success; otherwise <see langword="null"/>.</param>
    /// <param name="adapterLuid">The LUID of the adapter the feed's capture device must be created on (packed
    /// <c>(HighPart &lt;&lt; 32) | LowPart</c>), so its shared targets can be opened by a render device on the same
    /// adapter; <see langword="null"/> keeps the default adapter and leaves GPU publishing unavailable.</param>
    /// <returns><see langword="true"/> when the monitor was resolved and its capture feed started; otherwise <see langword="false"/>. An out-of-range index resolves to <see langword="false"/> rather than throwing.</returns>
    bool TryCreateMonitorCapture(
        int monitorIndex,
        int width,
        int height,
        double refreshRateHz,
        [NotNullWhen(true)] out INativeImageCaptureFeed? feed,
        long? adapterLuid = null
    );
}
