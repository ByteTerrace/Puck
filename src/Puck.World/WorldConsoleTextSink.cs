using Puck.Commands;
using Puck.Input;

namespace Puck.World;

/// <summary>
/// The <see cref="IWindowInputObserver"/> that bridges raw window text/edit events into <see cref="WorldConsoleInput"/>
/// while the console panel is open — gameplay suppression is NOT this class's job: it only feeds the line editor;
/// <see cref="Puck.Hosting.IInputFocus"/> releasing the keyboard device (wired in
/// <see cref="WorldBootComposition"/> off <see cref="WorldConsoleMirror.VisibilityChanged"/>) is what stops a
/// captured keystroke from also driving the avatar.
/// </summary>
internal sealed class WorldConsoleTextSink : IWindowInputObserver {
    // Armed from the start so the FIRST press toggles; a press disarms until its own release re-arms.
    private bool m_backtickArmed = true;
    private readonly WorldConsoleInput m_input;
    private readonly WorldConsoleMirror m_mirror;

    /// <summary>Initializes a new instance of the <see cref="WorldConsoleTextSink"/> class.</summary>
    /// <param name="input">The line editor captured keystrokes are applied to.</param>
    /// <param name="mirror">The mirror whose visibility gates capture and whose <see cref="WorldConsoleMirror.SetVisible"/>
    /// closes the panel on <c>Escape</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="mirror"/> is
    /// <see langword="null"/>.</exception>
    public WorldConsoleTextSink(WorldConsoleInput input, WorldConsoleMirror mirror) {
        ArgumentNullException.ThrowIfNull(argument: input);
        ArgumentNullException.ThrowIfNull(argument: mirror);

        m_input = input;
        m_mirror = mirror;
    }

    /// <inheritdoc/>
    public void Observe(in WindowInputEvent inputEvent) {
        // The backtick toggle is owned HERE, in BOTH directions, and is deliberately absent from the default binding
        // table. It cannot be a focus-gated binding: opening the panel releases the seat's keyboard focus, so the
        // router would receive the press that opens the console but never the one meant to close it. Nor can the two
        // halves be split between this observer and a binding — the observer runs ahead of the focus gate, so a close
        // here re-claims focus and the SAME event then reaches the router, which toggles it straight back open. One
        // owner, one toggle. A key rebound to the console verb still works through the router in every case this
        // cannot reach (a pad button carries its own device id, so releasing the keyboard's focus never gates it).
        // Arming on the RELEASE is what makes one press one toggle: Windows repeats WM_KEYDOWN while a key is held, so
        // toggling on any press edge would flip the panel repeatedly under a slightly long press.
        if ((inputEvent.Kind == WindowInputKind.Key) && (inputEvent.Key == KeyCode.Backtick)) {
            if (inputEvent.Phase == CommandPhase.Completed) {
                m_backtickArmed = true;
            } else if (m_backtickArmed) {
                m_backtickArmed = false;

                m_mirror.SetVisible(visible: !m_mirror.Visible);
            }

            return;
        }

        if (!m_mirror.Visible) {
            // Console closed: nothing else here has anything to do.
            return;
        }

        if (inputEvent.Kind == WindowInputKind.Text) {
            if (inputEvent.Text is { } text) {
                m_input.AppendText(text: text);
            }

            return;
        }

        if ((inputEvent.Kind == WindowInputKind.Key) && (inputEvent.Phase == CommandPhase.Started)) {
            // A Control chord on a letter is a clipboard/select verb, not typed text — handled ahead of the
            // named-key switch below since KeyCode.Letter never appears in it. Ctrl+letters never produce
            // TypedText (their WM_CHAR is a control char, filtered), so there is no double-append risk.
            if ((inputEvent.Key == KeyCode.Letter) && inputEvent.Modifiers.HasFlag(flag: WindowInputModifiers.Control)) {
                switch (char.ToLowerInvariant(c: inputEvent.Character)) {
                    case 'a':
                        m_input.SelectAll();
                        break;
                    case 'c':
                        m_input.Copy();
                        break;
                    case 'x':
                        m_input.Cut();
                        break;
                    case 'v':
                        // Paste arrives via TypedText (the platform reads the clipboard itself); nothing to do here.
                        break;
                    default:
                        // Any other Ctrl+letter: nothing to do.
                        break;
                }

                return;
            }

            switch (inputEvent.Key) {
                case KeyCode.Backspace:
                    m_input.Backspace();
                    break;
                case KeyCode.Enter:
                    m_input.Submit();
                    break;
                case KeyCode.Escape:
                    m_mirror.SetVisible(visible: false);
                    break;
                case KeyCode.ArrowLeft:
                    m_input.CaretLeft();
                    break;
                case KeyCode.ArrowRight:
                    m_input.CaretRight();
                    break;
                case KeyCode.ArrowUp:
                    m_input.HistoryPrev();
                    break;
                case KeyCode.ArrowDown:
                    m_input.HistoryNext();
                    break;
                // KeyCode has no Delete/Home/End today, so DeleteForward/CaretHome/CaretEnd have no key bound to
                // them yet.
                default:
                    break;
            }
        }
    }
}
