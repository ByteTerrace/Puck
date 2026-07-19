using Puck.Hosting;

namespace Puck.Overlays;

/// <summary>The latest transient toast the unified overlay renders (expiry runs on the CONTENT tick in
/// <see cref="ToastWriter"/>, never wall clock).</summary>
/// <param name="Sequence">A monotonically increasing publish counter — a new value restarts the toast's lifetime.</param>
/// <param name="Message">The toast text (the writer clips it to one line).</param>
/// <param name="IsError">Whether the toast narrates a rejection/denial (the danger hue) rather than a success.</param>
public readonly record struct OverlayToastFrame(
    int Sequence,
    string Message,
    bool IsError
);

/// <summary>The read seam <see cref="ToastWriter"/> consumes; any host echo path is the writer.</summary>
public interface IOverlayToastSource {
    /// <summary>Copies the latest published toast, when one exists.</summary>
    /// <param name="frame">The latest toast, when published.</param>
    /// <returns><see langword="true"/> when a toast has been published.</returns>
    bool TrySnapshot(out OverlayToastFrame frame);
}

/// <summary>
/// The toast state store: event-driven (a host publishes on each echo; nothing ticks it), sequence-stamped so the
/// writer can restart the lifetime on every distinct publish even when the text repeats. A thin named wrapper over
/// the shared <see cref="PublishBuffer{T}"/>.
/// </summary>
public sealed class OverlayToastStore : IOverlayToastSource {
    private readonly PublishBuffer<OverlayToastFrame> m_buffer = new();
    private int m_sequence;

    /// <summary>Publishes a toast (the writer side), advancing the sequence.</summary>
    /// <param name="message">The toast text.</param>
    /// <param name="isError">Whether the toast narrates a rejection/denial.</param>
    public void Publish(string message, bool isError) {
        ArgumentNullException.ThrowIfNull(argument: message);

        m_buffer.Publish(frame: new OverlayToastFrame(
            IsError: isError,
            Message: message,
            Sequence: Interlocked.Increment(location: ref m_sequence)
        ));
    }

    /// <inheritdoc/>
    public bool TrySnapshot(out OverlayToastFrame frame) => m_buffer.TrySnapshot(frame: out frame);
}
