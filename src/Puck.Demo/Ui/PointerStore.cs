using System.Numerics;
using Puck.Commands;
using Puck.Hosting;
using Puck.Input;

namespace Puck.Demo.Ui;

/// <summary>One pointer button's state as an overlay reads it: whether it is currently held, and monotonic
/// press/release counters an overlay diffs between two polls to catch every edge — including a press-and-release
/// that both land within the same pump — without needing a frame-boundary reset (the same idea as
/// <see cref="Puck.Platform.Windows.Win32NativeWindow"/>'s <c>ResizeCount</c>).</summary>
/// <param name="Held">Whether the button is currently down.</param>
/// <param name="PressCount">How many times the button has transitioned down, ever (monotonic).</param>
/// <param name="ReleaseCount">How many times the button has transitioned up, ever (monotonic).</param>
internal readonly record struct PointerButtonState(bool Held, ulong PressCount, ulong ReleaseCount);

/// <summary>The per-pump pointer snapshot a draggable overlay panel reads: the absolute client-space position and
/// each button's <see cref="PointerButtonState"/>, using the same 0=left/1=right/2=middle convention as
/// <see cref="WindowInputEvent.PointerButton"/>. Presentation/session-only — see <see cref="PointerStore"/>.</summary>
/// <param name="Position">The absolute pointer position, in client pixels.</param>
/// <param name="Left">The left button's state.</param>
/// <param name="Right">The right button's state.</param>
/// <param name="Middle">The middle button's state.</param>
internal readonly record struct PointerFrame(
    Vector2 Position,
    PointerButtonState Left,
    PointerButtonState Right,
    PointerButtonState Middle
);

/// <summary>The read seam a draggable overlay panel consumes; <see cref="PointerStore"/> is the writer.</summary>
internal interface IPointerSource {
    /// <summary>Copies the latest published frame, when one exists.</summary>
    /// <param name="frame">The latest frame, when published.</param>
    /// <returns><see langword="true"/> when a frame has been published.</returns>
    bool TrySnapshot(out PointerFrame frame);
}

/// <summary>
/// The demo-side pointer state store: implements <see cref="IPointerInputSink"/> so the launcher's window pump —
/// engine-agnostic, knowing nothing about the demo — hands it every raw <see cref="WindowInputEvent"/> as it is
/// dequeued (the demo contributes this as a <c>HostCapabilityContribution</c> in <see cref="DemoHost"/>, the same
/// seam a graphics backend uses to publish its device). Pointer BUTTONS carry no <see cref="InputSources"/>
/// vocabulary entry — <see cref="WindowInputMapper"/> deliberately passes them through inert, because a drag needs
/// continuous per-frame held state, not a one-shot bound command — so this store reads the raw event stream
/// directly instead of going through the command-binding pipeline. DETERMINISM NOTE: pointer state is
/// presentation/session-only; it is never coupled to a <c>CommandSnapshot</c> and never replayed — a run's hash
/// must not depend on where the mouse was. A thin wrapper over the shared <see cref="PublishBuffer{T}"/> (a
/// whole-reference swap per publish — no locks on the read path), matching
/// <see cref="Puck.Demo.DevConsole.ConsoleTextStore"/>.
/// </summary>
internal sealed class PointerStore : IPointerSource, IPointerInputSink {
    private readonly PublishBuffer<PointerFrame> m_buffer = new();
    private PointerButtonState m_left;
    private PointerButtonState m_middle;
    private Vector2 m_position;
    private PointerButtonState m_right;

    /// <inheritdoc/>
    public bool TrySnapshot(out PointerFrame frame) => m_buffer.TrySnapshot(frame: out frame);

    /// <inheritdoc/>
    public void Observe(in WindowInputEvent inputEvent) {
        switch (inputEvent.Kind) {
            case WindowInputKind.PointerPosition:
                m_position = inputEvent.Vector;
                break;
            case WindowInputKind.PointerButton:
                ApplyButton(inputEvent: in inputEvent);
                break;
            default:
                // Key/Text/PointerMove carry nothing an overlay-drag reads from this store.
                return;
        }

        Publish();
    }

    private void ApplyButton(in WindowInputEvent inputEvent) {
        var pressed = (inputEvent.Phase == CommandPhase.Started);

        switch ((int)inputEvent.Vector.X) {
            case 0:
                m_left = Advance(state: m_left, pressed: pressed);
                return;
            case 1:
                m_right = Advance(state: m_right, pressed: pressed);
                return;
            case 2:
                m_middle = Advance(state: m_middle, pressed: pressed);
                return;
        }
    }
    private static PointerButtonState Advance(PointerButtonState state, bool pressed) {
        return new PointerButtonState(
            Held: pressed,
            PressCount: (state.PressCount + (pressed ? 1UL : 0UL)),
            ReleaseCount: (state.ReleaseCount + (pressed ? 0UL : 1UL))
        );
    }
    private void Publish() {
        m_buffer.Publish(frame: new PointerFrame(
            Left: m_left,
            Middle: m_middle,
            Position: m_position,
            Right: m_right
        ));
    }
}
