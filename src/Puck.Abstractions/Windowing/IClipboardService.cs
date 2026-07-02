namespace Puck.Abstractions.Windowing;

/// <summary>
/// The system-clipboard seam the window's text pipeline uses (Ctrl+V pastes through it). Platforms without a
/// clipboard register a null implementation, so callers need no platform checks; failures never throw — operations
/// silently no-op or return <see langword="false"/>.
/// </summary>
public interface IClipboardService {
    /// <summary>Replaces the clipboard contents with the given text. Best-effort: when the clipboard is unavailable
    /// (held by another process, or the null implementation) the call silently does nothing.</summary>
    /// <param name="text">The text to place on the clipboard.</param>
    void SetText(string text);
    /// <summary>Attempts to read text from the clipboard.</summary>
    /// <param name="text">The clipboard text; the empty string when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the clipboard held non-empty text; <see langword="false"/> when it is
    /// empty, holds no text, or is unavailable.</returns>
    bool TryGetText(out string text);
}
