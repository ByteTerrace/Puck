namespace Puck.Input.Output;

/// <summary>
/// A fixed-capacity, thread-safe queue between output callers and one device's I/O owner. The fixed bound keeps a
/// producer from growing memory without limit when a device stops reading; callers receive <see langword="false"/>
/// once the queue is full and may retry or coalesce their next update.
/// </summary>
public sealed class GamepadOutputQueue {
    private readonly Lock m_gate = new();
    private readonly Queue<GamepadOutputCommand> m_items = new(capacity: Capacity);

    /// <summary>Gets the maximum number of pending commands retained for one device.</summary>
    public const int Capacity = 64;

    /// <summary>Removes every pending command.</summary>
    public void Clear() {
        lock (m_gate) {
            m_items.Clear();
        }
    }
    /// <summary>Removes the oldest pending command.</summary>
    /// <param name="command">The removed command, or the default value when the queue is empty.</param>
    /// <returns><see langword="true"/> when a command was removed; otherwise <see langword="false"/>.</returns>
    public bool TryDequeue(out GamepadOutputCommand command) {
        lock (m_gate) {
            return m_items.TryDequeue(result: out command);
        }
    }
    /// <summary>Adds a command when capacity remains.</summary>
    /// <param name="command">The command to add.</param>
    /// <returns><see langword="true"/> when the command was added; otherwise <see langword="false"/>.</returns>
    public bool TryEnqueue(in GamepadOutputCommand command) {
        lock (m_gate) {
            if (m_items.Count >= Capacity) {
                return false;
            }

            m_items.Enqueue(item: command);

            return true;
        }
    }
}
