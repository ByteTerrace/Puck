namespace Puck.Demo;

/// <summary>
/// A tiny single-line console for the demo: it tracks the in-progress input line, supports backspace, select-all,
/// and clipboard editing, and redraws the line after output is written so log lines and the prompt do not
/// clobber each other. Degrades gracefully when output is redirected (scripted runs).
/// </summary>
internal sealed class DemoConsole {
    private bool m_allSelected = false;
    private int m_anchorLeft;
    private int m_anchorTop;
    private string m_line = "";

    /// <summary>Gets the selected text — the whole line when select-all is active, otherwise empty.</summary>
    public string Selected => (m_allSelected ? m_line : "");

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

        return line;
    }
    /// <summary>Writes a message above the in-progress input line, then redraws the line beneath it.</summary>
    /// <param name="message">The message to write.</param>
    public void WriteLine(string message) {
        if (Console.IsOutputRedirected) {
            Console.Out.WriteLine(value: message);
            Console.Out.Write(value: m_line);

            return;
        }

        if (m_line.Length > 0) {
            SetCursor(left: m_anchorLeft, top: m_anchorTop);
            Console.Out.Write(value: new string(c: ' ', count: m_line.Length));
            SetCursor(left: m_anchorLeft, top: m_anchorTop);
        }

        Console.Out.WriteLine(value: message);
        CaptureAnchor();
        Console.Out.Write(value: m_line);
    }

    private void ClearLine() {
        if (!Console.IsOutputRedirected && (m_line.Length > 0)) {
            SetCursor(left: m_anchorLeft, top: m_anchorTop);
            Console.Out.Write(value: new string(c: ' ', count: m_line.Length));
            SetCursor(left: m_anchorLeft, top: m_anchorTop);
        }

        m_allSelected = false;
        m_line = "";
    }
    private void CaptureAnchor() {
        if (Console.IsOutputRedirected) {
            return;
        }

        (m_anchorLeft, m_anchorTop) = Console.GetCursorPosition();
    }
    private static void SetCursor(int left, int top) {
        Console.SetCursorPosition(
            left: Math.Clamp(value: left, max: (Console.BufferWidth - 1), min: 0),
            top: Math.Clamp(value: top, max: (Console.BufferHeight - 1), min: 0)
        );
    }
}
