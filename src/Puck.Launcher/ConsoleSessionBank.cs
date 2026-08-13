using Puck.Abstractions.Windowing;
using Puck.Commands;
using Puck.Hosting;

namespace Puck.Launcher;

/// <summary>
/// The terminal's local, seat-indexed console sessions. Each session owns an independent tape, editor, history,
/// text queue identity, and set of text devices, while all sessions share one command registry and deterministic
/// input timeline.
/// </summary>
public sealed class ConsoleSessionBank : IConsoleSessions, ICommandObserver {
    private readonly IInputFocus m_focus;
    private readonly InputRouter m_router;
    private readonly Session[] m_sessions;

    private sealed class Session {
        public required ConsoleLineEditor Editor { get; init; }
        public required HashSet<InputDeviceId> Devices { get; init; }
        public required ConsoleTapeStore Store { get; init; }
        public required ConsoleTape Tape { get; init; }
        public required TextCommandSession Text { get; init; }
    }

    /// <summary>Initializes a fixed set of local seat sessions.</summary>
    public ConsoleSessionBank(
        int seatCount,
        TextCommandSource source,
        InputRouter router,
        IInputSlotResolver slotResolver,
        IClipboardService clipboard,
        IInputFocus focus,
        TerminalConsoleSessions terminalSessions
    ) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seatCount);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(slotResolver);
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(focus);
        ArgumentNullException.ThrowIfNull(terminalSessions);

        m_focus = focus;
        m_router = router;
        m_sessions = new Session[seatCount];
        for (var slot = 0; slot < seatCount; slot++) {
            var store = new ConsoleTapeStore();
            var tape = new ConsoleTape(store: store);
            var text = source.CreateSeatSession(
                router: router,
                slot: slot,
                onResult: tape.Record
            );
            var editor = new ConsoleLineEditor(
                source: text,
                tape: tape,
                clipboard: clipboard
            );
            var capturedSlot = slot;

            m_sessions[slot] = new Session {
                Devices = [],
                Editor = editor,
                Store = store,
                Tape = tape,
                Text = text,
            };
            tape.VisibilityChanged = visible => VisibilityChanged(slot: capturedSlot, visible: visible);
        }

        slotResolver.DeviceSlotChanging += ForgetTextDevice;
        terminalSessions.Attach(sessions: this);
    }

    /// <inheritdoc/>
    public int Count => m_sessions.Length;

    /// <summary>Gets one session's line editor.</summary>
    public ConsoleLineEditor EditorFor(int slot) => SessionFor(slot: slot).Editor;

    /// <summary>Gets one session's published tape store.</summary>
    public ConsoleTapeStore StoreFor(int slot) => SessionFor(slot: slot).Store;

    /// <summary>Gets one session's tape.</summary>
    public ConsoleTape TapeFor(int slot) => SessionFor(slot: slot).Tape;

    /// <summary>Associates a text-capable device with its resolved seat. When that seat's console is open, the
    /// device is immediately suppressed from ordinary bindings and any held gameplay state it owns is released.</summary>
    public void TrackTextDevice(int slot, InputDeviceId device) {
        var session = SessionFor(slot: slot);

        if (session.Devices.Add(item: device) && session.Tape.Visible) {
            m_router.SuppressHeld(device: device);
            m_focus.Release(deviceId: device);
        }
    }

    /// <summary>Records one administrative stdin/script exchange on the operator-visible seat-one tape.</summary>
    public void RecordAdministrative(string line, CommandResult result) => m_sessions[0].Tape.Record(line: line, result: result);

    /// <summary>Records one deferred administrative edit verdict on the operator-visible seat-one tape.</summary>
    public void RecordAdministrativeEcho(string message, bool refused) => m_sessions[0].Tape.RecordEcho(message: message, refused: refused);

    /// <summary>Records the deferred result of an administrative Simulation-routed line.</summary>
    public void RecordAdministrativeActivation(in CommandActivation activation) => m_sessions[0].Tape.OnCommand(activation: in activation);

    /// <inheritdoc/>
    public bool TryGetVisible(int slot, out bool visible) {
        if ((uint)slot >= (uint)m_sessions.Length) {
            visible = false;
            return false;
        }

        visible = m_sessions[slot].Tape.Visible;
        return true;
    }

    /// <inheritdoc/>
    public bool TrySetVisible(int slot, bool? visible, out bool resolved) {
        if ((uint)slot >= (uint)m_sessions.Length) {
            resolved = false;
            return false;
        }

        var tape = m_sessions[slot].Tape;

        resolved = (visible ?? !tape.Visible);
        tape.SetVisible(visible: resolved);
        return true;
    }

    /// <inheritdoc/>
    public void OnCommand(in CommandActivation activation) {
        if (
            (activation.Principal.Kind != CommandPrincipalKind.Seat) ||
            ((uint)activation.Slot >= (uint)m_sessions.Length)
        ) {
            return;
        }

        m_sessions[activation.Slot].Tape.OnCommand(activation: in activation);
    }

    private Session SessionFor(int slot) {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, m_sessions.Length);

        return m_sessions[slot];
    }

    private void ForgetTextDevice(InputDeviceId device) {
        var removed = false;

        foreach (var session in m_sessions) {
            removed |= session.Devices.Remove(item: device);
        }

        if (removed) {
            m_focus.Claim(deviceId: device);
        }
    }

    private void VisibilityChanged(int slot, bool visible) {
        var session = m_sessions[slot];

        foreach (var device in session.Devices) {
            if (visible) {
                m_router.SuppressHeld(device: device);
                m_focus.Release(deviceId: device);
            } else {
                m_focus.Claim(deviceId: device);
            }
        }

        if (!visible) {
            session.Editor.Clear();
        }
    }
}
