using Puck.Hosting;

namespace Puck.Overlays;

/// <summary>One scrollback row: the text plus the verdict that decides how it is painted.</summary>
/// <param name="Text">The row's text.</param>
/// <param name="Refused">Whether the row belongs to a REFUSED result (rendered in the danger hue, matching the toast
/// channel) rather than an accepted one.</param>
public readonly record struct ConsolePanelLine(
    string Text,
    bool Refused
);

/// <summary>The per-frame console-panel snapshot the unified overlay renders.</summary>
/// <param name="Visible">Whether the console panel is shown (hidden = the writer emits nothing).</param>
/// <param name="Lines">The output history, oldest first; the writer shows the trailing lines that fit.</param>
/// <param name="Input">The in-progress input line (rendered on the bottom row after the prompt).</param>
public readonly record struct ConsolePanelFrame(
    bool Visible,
    IReadOnlyList<ConsolePanelLine> Lines,
    string Input
);

/// <summary>The read seam <see cref="ConsolePanelWriter"/> consumes; the host's console mirror is the writer.</summary>
public interface IConsolePanelSource {
    /// <summary>Copies the latest published frame, when one exists.</summary>
    /// <param name="frame">The latest frame, when published.</param>
    /// <returns><see langword="true"/> when a frame has been published.</returns>
    bool TrySnapshot(out ConsolePanelFrame frame);
}

/// <summary>
/// The console-panel state store: the host publishes an immutable frame on every console edit, the render thread
/// snapshots it. A thin named wrapper over the shared <see cref="PublishBuffer{T}"/> (a whole-reference swap per
/// publish — no locks on the read path, no torn frames) so DI registration and constructor parameters still name a
/// console-specific type.
/// </summary>
public sealed class ConsolePanelStore : IConsolePanelSource {
    private readonly PublishBuffer<ConsolePanelFrame> m_buffer = new();

    /// <summary>Publishes a frame (the writer side).</summary>
    /// <param name="frame">The frame to publish.</param>
    public void Publish(in ConsolePanelFrame frame) => m_buffer.Publish(frame: frame);

    /// <inheritdoc/>
    public bool TrySnapshot(out ConsolePanelFrame frame) => m_buffer.TrySnapshot(frame: out frame);
}
