namespace Puck.Demo.DevConsole;

/// <summary>The per-frame developer-console snapshot the overlay renders.</summary>
/// <param name="Visible">Whether the console panel is open (hidden = the overlay passes the frame through untouched).</param>
/// <param name="Lines">The output history, oldest first; the overlay shows the trailing lines that fit.</param>
/// <param name="Input">The in-progress input line (rendered on the bottom row after the prompt).</param>
internal readonly record struct ConsoleTextFrame(
    bool Visible,
    IReadOnlyList<string> Lines,
    string Input
);

/// <summary>The read seam the console overlay consumes; <see cref="DemoConsole"/> is the writer.</summary>
internal interface IConsoleTextSource {
    /// <summary>Copies the latest published frame, when one exists.</summary>
    /// <param name="frame">The latest frame, when published.</param>
    /// <returns><see langword="true"/> when a frame has been published.</returns>
    bool TrySnapshot(out ConsoleTextFrame frame);
}

/// <summary>
/// The developer-console state store: <see cref="DemoConsole"/> publishes an immutable frame on every edit, the
/// render thread snapshots it. A whole-reference swap per publish — no locks on the read path, no torn frames
/// (mirrors <c>BindingBarStore</c>).
/// </summary>
internal sealed class ConsoleTextStore : IConsoleTextSource {
    private volatile Holder? m_latest;

    private sealed record Holder(ConsoleTextFrame Frame);

    /// <summary>Publishes a frame (the writer side).</summary>
    /// <param name="frame">The frame to publish.</param>
    public void Publish(in ConsoleTextFrame frame) {
        m_latest = new Holder(Frame: frame);
    }

    /// <inheritdoc/>
    public bool TrySnapshot(out ConsoleTextFrame frame) {
        var latest = m_latest;

        if (latest is null) {
            frame = default;

            return false;
        }

        frame = latest.Frame;

        return true;
    }
}
