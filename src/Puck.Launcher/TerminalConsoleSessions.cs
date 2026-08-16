using Puck.Hosting;

namespace Puck.Launcher;

/// <summary>The terminal-owned stable console-session seam. A presentation host attaches its concrete session bank
/// after the command registry has composed, avoiding a registry → module → editor → registry construction cycle.</summary>
public sealed class TerminalConsoleSessions : IConsoleSessions {
    private readonly ConsoleTape m_operatorTape;

    private ConsoleSessionBank? m_attached;

    /// <summary>Initializes the stable session seam and its distinct administrative tape.</summary>
    public TerminalConsoleSessions() {
        OperatorStore = new ConsoleTapeStore();
        m_operatorTape = new ConsoleTape(store: OperatorStore);
    }

    /// <inheritdoc/>
    public int Count => (m_attached?.Count ?? 0);
    /// <summary>Gets the administrative stdin/script tape, independent of every seated text session.</summary>
    public ConsoleTapeStore OperatorStore { get; }

    /// <summary>Attaches the host's concrete local sessions.</summary>
    public void Attach(ConsoleSessionBank sessions) {
        ArgumentNullException.ThrowIfNull(sessions);

        m_attached = sessions;
    }
    /// <summary>Mirrors one administrative stdin/script exchange into the operator-visible console tape.</summary>
    public void RecordAdministrative(string line, Puck.Commands.CommandResult result) {
        m_operatorTape.Record(
            line: line,
            result: result
        );
        m_attached?.RecordAdministrative(
            line: line,
            result: result
        );
    }
    /// <summary>Records a deferred administrative result on its own tape and temporary display mirror.</summary>
    public void RecordAdministrativeActivation(in Puck.Commands.CommandActivation activation) {
        m_operatorTape.OnCommand(activation: in activation);
        m_attached?.RecordAdministrativeActivation(activation: in activation);
    }
    /// <summary>Mirrors one deferred administrative edit verdict into the operator-visible console tape.</summary>
    public void RecordAdministrativeEcho(string message, bool refused) {
        m_operatorTape.RecordEcho(
            message: message,
            refused: refused
        );
        m_attached?.RecordAdministrativeEcho(
            message: message,
            refused: refused
        );
    }
    /// <inheritdoc/>
    public bool TryGetVisible(int slot, out bool visible) {
        if (m_attached is { } attached) {
            return attached.TryGetVisible(
                slot: slot,
                visible: out visible
            );
        }

        visible = false;
        return false;
    }
    /// <inheritdoc/>
    public bool TrySetVisible(int slot, bool? visible, out bool resolved) {
        if (m_attached is { } attached) {
            return attached.TrySetVisible(
                resolved: out resolved,
                slot: slot,
                visible: visible
            );
        }

        resolved = false;
        return false;
    }
}
/// <summary>Late-resolving command observer that routes deferred seat-text verdicts to the attached session bank.</summary>
public sealed class ConsoleSessionCommandObserver(Func<ConsoleSessionBank> sessions, TerminalConsoleSessions terminalSessions) : Puck.Commands.ICommandObserver {
    /// <inheritdoc/>
    public void OnCommand(in Puck.Commands.CommandActivation activation) {
        if (activation.Principal.Kind == Puck.Commands.CommandPrincipalKind.Seat) {
            sessions().OnCommand(activation: in activation);
        } else if (activation.Principal.Kind == Puck.Commands.CommandPrincipalKind.Console) {
            terminalSessions.RecordAdministrativeActivation(activation: in activation);
        }
    }
}
