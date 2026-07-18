using Puck.Commands;
using Puck.Overlays;

namespace Puck.World;

/// <summary>
/// The on-screen mirror of the World console: every submitted stdin/console line and its result echo append into a
/// bounded scrollback ring, published as a <see cref="ConsolePanelFrame"/> for the unified overlay's console-panel
/// writer — the unification contract's "on-screen panel AND stdin" made literal (the pipe and the panel show the
/// same exchange). Event-driven: it publishes on each recorded line and on visibility flips, never per frame.
/// </summary>
/// <remarks>Single-threaded by contract: <see cref="Record"/> runs inside the command pump's drain and
/// <see cref="SetVisible"/> inside a verb handler — both on the window-pump thread. Only the published immutable
/// snapshot crosses to the render thread (through the store's lock-free buffer).</remarks>
internal sealed class WorldConsoleMirror {
    private const int MaxLines = 64;
    private const string EchoPrefix = "> ";

    private readonly string[] m_ring = new string[MaxLines];
    private readonly ConsolePanelStore m_store;
    private int m_count;
    private int m_head;
    private bool m_visible = true;

    /// <summary>Initializes a new instance of the <see cref="WorldConsoleMirror"/> class (visible by default —
    /// the console is the control plane).</summary>
    /// <param name="store">The console-panel store the overlay reads.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is <see langword="null"/>.</exception>
    public WorldConsoleMirror(ConsolePanelStore store) {
        ArgumentNullException.ThrowIfNull(argument: store);

        m_store = store;
        Publish();
    }

    /// <summary>Gets whether the panel is currently shown.</summary>
    public bool Visible => m_visible;

    /// <summary>Records one submitted console line and its result: the echoed input (the phosphor voice) then each
    /// output line.</summary>
    /// <param name="line">The submitted command line.</param>
    /// <param name="result">The command's result.</param>
    public void Record(string line, CommandResult result) {
        ArgumentNullException.ThrowIfNull(argument: line);

        Append(line: (EchoPrefix + line));

        if (result.Output is { Length: > 0 } output) {
            foreach (var range in output.AsSpan().Split(separator: '\n')) {
                Append(line: output[range].TrimEnd(trimChar: '\r'));
            }
        }

        Publish();
    }

    /// <summary>Shows or hides the panel (the <c>world.console</c> verb's seam).</summary>
    /// <param name="visible">Whether the panel is shown.</param>
    public void SetVisible(bool visible) {
        m_visible = visible;
        Publish();
    }

    private void Append(string line) {
        m_ring[((m_head + m_count) % MaxLines)] = line;

        if (m_count < MaxLines) {
            m_count++;
        } else {
            m_head = ((m_head + 1) % MaxLines);
        }
    }

    // Snapshot allocation is event-scoped (per recorded exchange), never per frame — the render thread only ever
    // reads the immutable array the frame carries.
    private void Publish() {
        var lines = new string[m_count];

        for (var index = 0; (index < m_count); index++) {
            lines[index] = m_ring[((m_head + index) % MaxLines)];
        }

        m_store.Publish(frame: new ConsolePanelFrame(Input: string.Empty, Lines: lines, Visible: m_visible));
    }
}
