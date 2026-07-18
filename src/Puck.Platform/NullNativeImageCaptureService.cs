using System.Diagnostics.CodeAnalysis;

namespace Puck.Platform;

/// <summary>The unsupported-platform native image capture service.</summary>
public sealed class NullNativeImageCaptureService : INativeImageCaptureService {
    /// <inheritdoc/>
    public bool IsSupported => false;

    /// <inheritdoc/>
    public bool TryCreateWindowCapture(string windowTitleFragment, int width, int height, double refreshRateHz, [NotNullWhen(true)] out INativeImageCaptureFeed? feed, long? adapterLuid = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: windowTitleFragment);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: refreshRateHz);

        if (!double.IsFinite(refreshRateHz)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(refreshRateHz), actualValue: refreshRateHz, message: "The refresh rate must be finite.");
        }

        feed = null;
        return false;
    }

    /// <inheritdoc/>
    public bool TryCreateMonitorCapture(int monitorIndex, int width, int height, double refreshRateHz, [NotNullWhen(true)] out INativeImageCaptureFeed? feed, long? adapterLuid = null) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: refreshRateHz);

        if (!double.IsFinite(refreshRateHz)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(refreshRateHz), actualValue: refreshRateHz, message: "The refresh rate must be finite.");
        }

        feed = null;
        return false;
    }
}
