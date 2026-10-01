using System.Runtime.Versioning;

namespace Puck.Platform.Windows;

/// <summary>The frame format and encoding a native capture opens with, from the color space of the display it captures.
/// A display whose capture would take another format or encoding, or one discovery cannot describe, ends the feed so its
/// consumer reopens it with a fresh frame pool and encoding; a move to another display with the same encoding does
/// not.</summary>
[SupportedOSPlatform("windows10.0.19041")]
public sealed class Win32CaptureDisplay {
    /// <summary>Opens a capture's display contract; an unknown color space is refused rather than taken for SDR.</summary>
    /// <param name="colorSpace">The color space DXGI reports for the captured display, or null when discovery fails.</param>
    /// <exception cref="NotSupportedException"><paramref name="colorSpace"/> is null.</exception>
    public Win32CaptureDisplay(DisplayColorSpace? colorSpace) {
        if (colorSpace is not { } known) {
            throw new NotSupportedException(message: "The captured display's color space is unavailable.");
        }

        Output = Win32GraphicsCaptureFeed.CaptureOutputOf(display: known);
    }

    /// <summary>Gets the fixed frame format and encoding.</summary>
    public DisplayOutput Output { get; }

    /// <summary>Gets whether the display the target shows on now still takes the capture's opening format and encoding.
    /// Toggling HDR, moving to a display that differs in it, or losing discovery requires a new feed.</summary>
    /// <param name="colorSpace">The current display's reported color space, or null when discovery fails.</param>
    /// <returns>Whether the feed may continue publishing in its fixed format and encoding.</returns>
    public bool IsCurrent(DisplayColorSpace? colorSpace) => (
        (colorSpace is { } known) &&
        (Output == Win32GraphicsCaptureFeed.CaptureOutputOf(display: known))
    );
}
