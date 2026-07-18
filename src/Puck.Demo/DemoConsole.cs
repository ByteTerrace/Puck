using System.Numerics;
using Puck.Demo.DevConsole;

namespace Puck.Demo;

/// <summary>
/// A tiny single-line console for the demo: it tracks the in-progress input line, supports backspace, select-all,
/// and clipboard editing, and redraws the line after output is written so log lines and the prompt do not
/// clobber each other. Degrades gracefully when output is redirected (scripted runs). Every edit is ALSO published
/// to the <see cref="ConsoleTextStore"/> so the on-screen console overlay can mirror the input line and the recent
/// output in-window (the terminal echo stays, for scripted/redirected runs).
/// </summary>
internal sealed class DemoConsole {
    // The most output lines retained for the on-screen mirror (the terminal keeps its own scrollback).
    private const int MaxHistory = 256;

    private readonly ConsoleTextStore m_store;
    private readonly List<string> m_history = new(capacity: MaxHistory);
    private bool m_allSelected = false;
    private int m_anchorLeft;
    private int m_anchorTop;
    private string m_line = "";
    // A client-space, top-left override for the on-screen panel's position — set by a title-band drag
    // (ConsoleOverlayNode) or the console.move verb, cleared by console.reset. Null = the default token-derived rect.
    // Session/presentation-only, exactly like m_visible: never coupled to a CommandSnapshot, never replayed.
    private Vector2? m_panelPosition;
    private bool m_visible;

    /// <summary>Initializes a new instance of the <see cref="DemoConsole"/> class.</summary>
    /// <param name="store">The store the on-screen console overlay reads.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is <see langword="null"/>.</exception>
    public DemoConsole(ConsoleTextStore store) {
        ArgumentNullException.ThrowIfNull(argument: store);

        m_store = store;
        // The on-screen panel starts closed (the backtick `console` verb opens it live); a stdin-driven run needs no
        // open panel and drives the same command registry directly.
        Publish();
    }

    /// <summary>Gets the selected text — the whole line when select-all is active, otherwise empty.</summary>
    public string Selected => (m_allSelected ? m_line : "");

    /// <summary>Opens or closes the on-screen console panel (the terminal console is always live).</summary>
    /// <param name="visible">Whether the panel should be shown.</param>
    public void SetVisible(bool visible) {
        if (m_visible == visible) {
            return;
        }

        m_visible = visible;
        Publish();
    }

    /// <summary>Sets a client-space, top-left override for the on-screen panel's position — the write side both a
    /// title-band drag (<see cref="ConsoleOverlayNode"/>, every dragged frame) and the <c>console.move</c> verb use.
    /// Clamped here to non-negative client coordinates only: this store knows nothing about the panel's
    /// on-screen size or the window's dimensions (it must stay usable even when nothing is rendering, e.g. a headless
    /// run), so the overlay applies the full screen/title-band-aware clamp again at render time regardless of source.
    /// </summary>
    /// <param name="position">The requested top-left position, in client pixels.</param>
    /// <returns>The stored (clamped) position, for the caller to echo.</returns>
    public Vector2 SetPanelPosition(Vector2 position) {
        var clamped = new Vector2(x: Math.Max(val1: 0f, val2: position.X), y: Math.Max(val1: 0f, val2: position.Y));

        m_panelPosition = clamped;
        Publish();

        return clamped;
    }

    /// <summary>Clears the panel-position override (the <c>console.reset</c> verb): the panel returns to its default
    /// token-derived rect.</summary>
    public void ClearPanelPosition() {
        if (m_panelPosition is null) {
            return;
        }

        m_panelPosition = null;
        Publish();
    }

    /// <summary>Appends text to the input line (replacing the whole line first if it was select-all'd).</summary>
    /// <param name="text">The text to append.</param>
    public void Append(string text) {
        // Typing over a select-all replaces the whole line, like a normal text field.
        if (m_allSelected) {
            ClearLine();
        }

        // The anchor is where the current input line begins; capture it as the line starts so output can
        // later erase and redraw the line even after it has wrapped across rows.
        if (m_line.Length == 0) {
            CaptureAnchor();
        }

        m_line += text;
        Console.Out.Write(value: text);
        Publish();
    }
    /// <summary>Deletes the last character of the input line (or clears the line when select-all is active).</summary>
    public void Backspace() {
        if (m_allSelected) {
            ClearLine();

            return;
        }

        if (m_line.Length == 0) {
            return;
        }

        m_line = m_line[..^1];
        Publish();

        if (Console.IsOutputRedirected) {
            return;
        }

        var (left, top) = Console.GetCursorPosition();

        if (left == 0) {
            left = (Console.BufferWidth - 1);
            top -= 1;
        } else {
            left -= 1;
        }

        SetCursor(left: left, top: top);
        Console.Out.Write(value: ' ');
        SetCursor(left: left, top: top);
    }
    /// <summary>Selects the entire input line (a no-op when the line is empty).</summary>
    public void SelectAll() {
        m_allSelected = (m_line.Length > 0);
    }
    /// <summary>Returns the current line and resets the input state, moving the cursor to a fresh line.</summary>
    /// <returns>The submitted line.</returns>
    public string TakeLine() {
        var line = m_line;

        m_allSelected = false;
        m_line = "";
        Console.Out.WriteLine();

        // Echo the submitted command into the on-screen history so it reads like a real terminal.
        if (line.Length > 0) {
            AddHistory(message: $"> {line}");
        }

        Publish();

        return line;
    }
    /// <summary>Echoes a scripted (stdin-submitted) command line into the on-screen history ONLY — the panel then
    /// shows the same conversation the pipe drives (the unification contract's one-console rule) — without writing
    /// to stdout: the pipe already knows what it sent, and a scripted run's assertable stdout must stay
    /// byte-identical. Pair with <see cref="WriteLine"/> for the result, exactly like the panel's enter verb.</summary>
    /// <param name="line">The submitted command line.</param>
    public void EchoSubmittedLine(string line) {
        if (string.IsNullOrEmpty(value: line)) {
            return;
        }

        AddHistory(message: $"> {line}");
        Publish();
    }
    /// <summary>Writes a message above the in-progress input line, then redraws the line beneath it.</summary>
    /// <param name="message">The message to write.</param>
    public void WriteLine(string message) {
        AddHistory(message: message);
        Publish();

        // stdout is a PLAIN-TEXT sink: an enriched line (the compiled control-char stream the CRT terminal parses)
        // keeps its stream in the history/feed, but its C0 delimiters mangle a terminal/pipe — strip to the visible
        // runes for the stdout copy only. An ordinary line carries no delimiters and passes through byte-identical.
        var plainMessage = StripEnrichmentForStdout(message: message);

        if (Console.IsOutputRedirected) {
            Console.Out.WriteLine(value: plainMessage);
            Console.Out.Write(value: m_line);

            return;
        }

        if (m_line.Length > 0) {
            SetCursor(left: m_anchorLeft, top: m_anchorTop);
            Console.Out.Write(value: new string(c: ' ', count: m_line.Length));
            SetCursor(left: m_anchorLeft, top: m_anchorTop);
        }

        Console.Out.WriteLine(value: plainMessage);
        CaptureAnchor();
        Console.Out.Write(value: m_line);
    }

    private static string StripEnrichmentForStdout(string message) {
        foreach (var c in message) {
            if (Puck.Text.TextEnrichmentTags.IsDelimiter(unicodeScalar: c)) {
                return string.Concat(values: Puck.Text.TextEnrichmentTags.EnumerateVisibleRunes(text: message));
            }
        }

        return message;
    }
    private void ClearLine() {
        if (!Console.IsOutputRedirected && (m_line.Length > 0)) {
            SetCursor(left: m_anchorLeft, top: m_anchorTop);
            Console.Out.Write(value: new string(c: ' ', count: m_line.Length));
            SetCursor(left: m_anchorLeft, top: m_anchorTop);
        }

        m_allSelected = false;
        m_line = "";
        Publish();
    }
    private void CaptureAnchor() {
        if (Console.IsOutputRedirected) {
            return;
        }

        (m_anchorLeft, m_anchorTop) = Console.GetCursorPosition();
    }

    // Appends a message (splitting embedded newlines into separate history rows) and trims to the retained window.
    private void AddHistory(string message) {
        foreach (var line in message.Replace(oldValue: "\r", newValue: "").Split(separator: '\n')) {
            m_history.Add(item: line);
        }

        if (m_history.Count > MaxHistory) {
            m_history.RemoveRange(index: 0, count: (m_history.Count - MaxHistory));
        }
    }
    private void Publish() {
        m_store.Publish(frame: new ConsoleTextFrame(
            Input: m_line,
            Lines: m_history.ToArray(),
            PanelPosition: m_panelPosition,
            Visible: m_visible
        ));
    }
    private static void SetCursor(int left, int top) {
        Console.SetCursorPosition(
            left: Math.Clamp(value: left, max: (Console.BufferWidth - 1), min: 0),
            top: Math.Clamp(value: top, max: (Console.BufferHeight - 1), min: 0)
        );
    }
}
