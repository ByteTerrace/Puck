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
internal sealed class WorldConsoleMirror : ICommandObserver {
    private const string EchoPrefix = "> ";
    private const int MaxLines = 64;

    private string m_input = string.Empty;
    private readonly ConsolePanelLine[] m_ring = new ConsolePanelLine[MaxLines];
    private bool m_selected;
    private readonly ConsolePanelStore m_store;
    private int m_count;
    private int m_head;
    private bool m_visible;

    /// <summary>Initializes a new instance of the <see cref="WorldConsoleMirror"/> class — HIDDEN by default.</summary>
    /// <remarks>The pipe is the control plane and carries it whether or not this panel draws, so drawing it by
    /// default only covered the top half of the frame. Backtick, <c>world.console</c>, and the play wheel's Console
    /// sector open it on demand.</remarks>
    /// <param name="store">The console-panel store the overlay reads.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is <see langword="null"/>.</exception>
    public WorldConsoleMirror(ConsolePanelStore store) {
        ArgumentNullException.ThrowIfNull(argument: store);

        m_store = store;
        Publish();
    }

    /// <summary>Whether the panel is currently shown.</summary>
    public bool Visible => m_visible;

    /// <summary>Gets or sets the callback invoked once each time the panel's visibility CHANGES, carrying the new
    /// state — wired in <see cref="WorldBootComposition"/> to <see cref="Puck.Hosting.IInputFocus.Release"/>/
    /// <see cref="Puck.Hosting.IInputFocus.Claim"/> for the keyboard device (so typing suppresses gameplay input for
    /// exactly as long as the panel is on screen) and, on the hide edge, to the line editor's reset so a reopened
    /// console never resurrects a half-typed line.</summary>
    public Action<bool>? VisibilityChanged { get; set; }

    /// <summary>Records one submitted console line and its result: the echoed input (the phosphor voice) then each
    /// output line, each carrying the result's verdict so the panel paints a refusal as a refusal.</summary>
    /// <param name="line">The submitted command line.</param>
    /// <param name="result">The command's result.</param>
    public void Record(string line, CommandResult result) {
        ArgumentNullException.ThrowIfNull(argument: line);

        // The echo keeps the phosphor voice whatever the verdict; only the OUTPUT rows carry the refusal.
        Append(line: (EchoPrefix + line), refused: false);

        if (result.Output is { Length: > 0 } output) {
            foreach (var range in output.AsSpan().Split(separator: '\n')) {
                Append(line: output[range].TrimEnd(trimChar: '\r'), refused: result.IsError);
            }
        }

        Publish();
    }

    /// <summary>Records the deferred verdict of a Simulation-routed console line — <see cref="Record"/> only ever saw
    /// the <see cref="CommandResult.None"/> the submit returned when the line entered the tick queue, so without this
    /// the panel showed the echoed input and never the refusal that followed a tick later.</summary>
    /// <param name="activation">The dispatch, as seen after the handler ran on its tick.</param>
    /// <remarks>No double render: the text path is unobserved (an Immediate verb reaches the panel through
    /// <see cref="Record"/> alone), a physical pad activation carries no text and is skipped, and a world-mutation verb
    /// returns no output here — its outcome arrives on the server's edit-echo tap through
    /// <see cref="RecordEcho"/>.</remarks>
    public void OnCommand(in CommandActivation activation) {
        if ((activation.Text is null) || (activation.Result.Output is not { Length: > 0 } output)) {
            return;
        }

        foreach (var range in output.AsSpan().Split(separator: '\n')) {
            Append(line: output[range].TrimEnd(trimChar: '\r'), refused: activation.Result.IsError);
        }

        Publish();
    }

    /// <summary>Records one UNSOLICITED edit-boundary echo — a tick-boundary mutation outcome has no submitted line
    /// to hang off, so without this the panel would never show it and only the (wrapped, still bounded) toast and
    /// stderr would carry the reason.</summary>
    /// <param name="message">The echo text.</param>
    /// <param name="refused">Whether the echo narrates a rejection/denial.</param>
    public void RecordEcho(string message, bool refused) {
        ArgumentNullException.ThrowIfNull(argument: message);

        Append(line: message, refused: refused);
        Publish();
    }

    /// <summary>Shows or hides the panel (the <c>world.console</c> verb's seam and <c>Escape</c>'s close).</summary>
    /// <param name="visible">Whether the panel is shown.</param>
    public void SetVisible(bool visible) {
        var wasVisible = m_visible;

        m_visible = visible;

        if (!visible) {
            // A reopened console starts clean rather than showing a half-typed line from before it closed.
            m_input = string.Empty;
            m_selected = false;
        }

        Publish();

        // Fire on a real transition only (never a same-state SetVisible(true) while already shown) — the mirror
        // only clears its own display echo above; the line editor holds the authoritative buffer/caret and is reset
        // through its own home on the hide edge (see the VisibilityChanged hook).
        if (wasVisible != visible) {
            VisibilityChanged?.Invoke(visible);
        }
    }

    /// <summary>Sets the prompt row's live input echo — <see cref="WorldConsoleInput"/>'s republish seam.</summary>
    /// <param name="input">The input line to display: caret marker included when <paramref name="selected"/> is
    /// <see langword="false"/>, the raw line otherwise (the highlight rect stands in for the caret).</param>
    /// <param name="selected">Whether the whole input line is selected (Ctrl+A).</param>
    public void SetInput(string input, bool selected) {
        m_input = (input ?? string.Empty);
        m_selected = selected;
        Publish();
    }

    private void Append(string line, bool refused) {
        m_ring[((m_head + m_count) % MaxLines)] = new ConsolePanelLine(Text: line, Refused: refused);

        if (m_count < MaxLines) {
            m_count++;
        } else {
            m_head = ((m_head + 1) % MaxLines);
        }
    }

    // Snapshot allocation is event-scoped (per recorded exchange), never per frame — the render thread only ever
    // reads the immutable array the frame carries.
    private void Publish() {
        var lines = new ConsolePanelLine[m_count];

        for (var index = 0; (index < m_count); index++) {
            lines[index] = m_ring[((m_head + index) % MaxLines)];
        }

        m_store.Publish(frame: new ConsolePanelFrame(Input: m_input, Lines: lines, Selected: m_selected, Visible: m_visible));
    }
}
