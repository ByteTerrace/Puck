namespace Puck.Hosting;

/// <summary>One tape row: the text plus the verdict that decides how it is painted.</summary>
/// <param name="Text">The row's text.</param>
/// <param name="Refused">Whether the row belongs to a refused result (rendered in the danger hue, matching the toast
/// channel) rather than an accepted one.</param>
public readonly record struct ConsoleTapeLine(
    string Text,
    bool Refused
);

/// <summary>The published console-tape snapshot a renderer draws.</summary>
/// <param name="Visible">Whether the console panel is shown (hidden = a renderer draws nothing).</param>
/// <param name="Lines">The output history, oldest first; a renderer shows the trailing lines that fit.</param>
/// <param name="Input">The in-progress input line (rendered on the bottom row after the prompt).</param>
/// <param name="Selected">Whether the whole <see cref="Input"/> line is selected (Ctrl+A) — a renderer paints a
/// highlight rect behind it instead of the insertion caret.</param>
public readonly record struct ConsoleTapeFrame(
    bool Visible,
    IReadOnlyList<ConsoleTapeLine> Lines,
    string Input,
    bool Selected
);

/// <summary>The read seam a console renderer consumes (the unified overlay's console-panel writer); the terminal's
/// <see cref="ConsoleTape"/> is the writer.</summary>
public interface IConsoleTapeSource {
    /// <summary>Copies the latest published frame, when one exists.</summary>
    /// <param name="frame">The latest frame, when published.</param>
    /// <returns><see langword="true"/> when a frame has been published.</returns>
    bool TrySnapshot(out ConsoleTapeFrame frame);
}

/// <summary>
/// The console-tape state store: the <see cref="ConsoleTape"/> publishes an immutable frame on every console edit,
/// the render thread snapshots it. A thin named wrapper over the shared <see cref="PublishBuffer{T}"/> (a
/// whole-reference swap per publish — no locks on the read path, no torn frames) so DI registration and constructor
/// parameters still name a console-specific type.
/// </summary>
public sealed class ConsoleTapeStore : IConsoleTapeSource {
    private readonly PublishBuffer<ConsoleTapeFrame> m_buffer = new();

    /// <summary>Publishes a frame (the writer side).</summary>
    /// <param name="frame">The frame to publish.</param>
    public void Publish(in ConsoleTapeFrame frame) => m_buffer.Publish(frame: frame);

    /// <inheritdoc/>
    public bool TrySnapshot(out ConsoleTapeFrame frame) => m_buffer.TrySnapshot(frame: out frame);
}
