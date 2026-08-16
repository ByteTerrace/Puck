using Puck.Commands;
using Puck.Input;

namespace Puck.Launcher;

/// <summary>
/// The <see cref="IWindowInputObserver"/> that bridges raw window text/edit events into the
/// seat session selected by the event's text-device identity. <see cref="ConsoleSessionBank"/> owns focus suppression
/// and principal-bound submission; this class only performs editing gestures.
/// </summary>
public sealed class ConsoleInputSink : IWindowInputObserver {
    private readonly ConsoleSessionBank m_sessions;
    private readonly Func<InputDeviceId, int> m_slotOf;

    /// <summary>Initializes a new instance of the <see cref="ConsoleInputSink"/> class.</summary>
    /// <param name="sessions">The terminal's local seat sessions.</param>
    /// <param name="slotResolver">The roster-owned device-to-seat resolver.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sessions"/> or <paramref name="slotResolver"/> is
    /// <see langword="null"/>.</exception>
    public ConsoleInputSink(ConsoleSessionBank sessions, IInputSlotResolver slotResolver) {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(slotResolver);

        m_sessions = sessions;
        m_slotOf = slotResolver.ResolveSlot;
    }

    /// <inheritdoc/>
    public void Observe(in WindowInputEvent inputEvent) {
        if (inputEvent.Kind is not (WindowInputKind.Key or WindowInputKind.Text)) {
            return;
        }

        var slot = m_slotOf(arg: inputEvent.DeviceId);

        if (
            (slot < 0) ||
            (slot >= m_sessions.Count)
        ) {
            return;
        }

        m_sessions.TrackTextDevice(
            slot: slot,
            device: inputEvent.DeviceId
        );

        if (
            !m_sessions.TryGetVisible(
            slot: slot,
            visible: out var visible
        ) ||
            !visible
        ) {
            // Console closed: nothing else here has anything to do.
            return;
        }

        var input = m_sessions.EditorFor(slot: slot);

        if (inputEvent.Kind == WindowInputKind.Text) {
            if (inputEvent.Text is { } text) {
                input.AppendText(text: text);
            }

            return;
        }

        if (
            (inputEvent.Kind == WindowInputKind.Key) &&
            (inputEvent.Phase == CommandPhase.Started)
        ) {
            // A Control chord on a letter is a clipboard/select verb, not typed text — handled ahead of the
            // named-key switch below since KeyCode.Letter never appears in it. Ctrl+letters never produce
            // TypedText (their WM_CHAR is a control char, filtered), so there is no double-append risk.
            if (
                (inputEvent.Key == KeyCode.Letter) &&
                inputEvent.Modifiers.HasFlag(flag: WindowInputModifiers.Control)
            ) {
                switch (char.ToLowerInvariant(c: inputEvent.Character)) {
                    case 'a':
                        input.SelectAll();
                        break;
                    case 'c':
                        input.Copy();
                        break;
                    case 'x':
                        input.Cut();
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
                    input.Backspace();
                    break;
                case KeyCode.Enter:
                    input.Submit();
                    break;
                case KeyCode.Escape:
                    _ = m_sessions.TrySetVisible(
                        resolved: out _,
                        slot: slot,
                        visible: false
                    );
                    break;
                case KeyCode.ArrowLeft:
                    input.CaretLeft();
                    break;
                case KeyCode.ArrowRight:
                    input.CaretRight();
                    break;
                case KeyCode.ArrowUp:
                    input.HistoryPrev();
                    break;
                case KeyCode.ArrowDown:
                    input.HistoryNext();
                    break;
                // KeyCode has no Delete/Home/End today, so DeleteForward/CaretHome/CaretEnd have no key bound to
                // them yet.
                default:
                    break;
            }
        }
    }
}
