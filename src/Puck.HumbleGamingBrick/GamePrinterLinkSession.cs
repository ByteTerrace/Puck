namespace Puck.HumbleGamingBrick;

/// <summary>
/// A serial cable between one machine and a <see cref="GamePrinterDevice"/> — the printer analogue of
/// <see cref="SerialLinkSession"/>, but a machine wired to a modeled device peer rather than to a second machine.
/// Constructing the session attaches the printer to the machine's serial port; the machine is then advanced THROUGH the
/// session so the printer's tick-clock print countdown stays in lockstep with the emulated timeline: <see cref="Run"/>
/// advances the machine by a T-cycle budget and hands the same budget to the printer's <see cref="GamePrinterDevice.AdvanceBusy"/>.
/// <para>
/// Unlike a two-machine link there is no interleave to arbitrate — the printer is purely reactive, clocked entirely by the
/// bits the console shifts out over its internal clock, so a bare <see cref="Machine.Run"/> already drives the exchange.
/// The session exists only to carry the deterministic clock into the printer's busy countdown and to own the attach/detach
/// lifetime. A linked pair advanced on one fixed budget schedule is a pure function of state and schedule, so a printer
/// run is replay-identical, and — because the printer's whole state is snapshotted — a snapshot/restore of the machine
/// and the printer across a transfer-idle instant continues the exact print.
/// </para>
/// </summary>
public sealed class GamePrinterLinkSession : IDisposable {
    private readonly Machine m_machine;
    private readonly SerialComponent m_port;
    private readonly GamePrinterDevice m_printer;
    private bool m_disposed;

    /// <summary>Attaches a printer to a machine's serial port.</summary>
    /// <param name="machine">The machine the printer is cabled to.</param>
    /// <param name="printer">The printer device.</param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The machine's serial port is already linked.</exception>
    public GamePrinterLinkSession(MachineInstance machine, GamePrinterDevice printer) {
        ArgumentNullException.ThrowIfNull(argument: machine);
        ArgumentNullException.ThrowIfNull(argument: printer);

        var port = machine.GetRequiredService<SerialComponent>();

        SerialComponent.AttachPeer(port: port, peer: printer);

        m_machine = machine.Machine;
        m_port = port;
        m_printer = printer;
    }

    /// <summary>Advances the machine by a budget of T-cycles (dots) and carries the same budget into the printer's print
    /// countdown — the seam a host drives in place of the machine's own <see cref="Machine.Run"/> while a printer is cabled.</summary>
    /// <param name="tCycles">The number of T-cycles to advance this call.</param>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    public void Run(ulong tCycles) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);

        m_machine.Run(tCycles: tCycles);
        m_printer.AdvanceBusy(tCycles: tCycles);
    }

    /// <summary>Severs the cable: the serial port loses its peer and the machine steps independently again. The machine
    /// and printer themselves are untouched (they are owned by the caller, not the session).</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        SerialComponent.DetachPeer(port: m_port);
    }
}
