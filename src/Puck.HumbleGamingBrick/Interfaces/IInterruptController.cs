namespace Puck.HumbleGamingBrick.Interfaces;

/// <summary>
/// The hub the five interrupt sources report into and the CPU dispatches from. A component requests an interrupt by
/// raising its line; the CPU consults the pending set (requested and enabled) to decide whether to wake from HALT and
/// whether to dispatch, then acknowledges the one it serviced. The request and enable masks are the IF and IE
/// registers, readable and writable on the bus.
/// </summary>
public interface IInterruptController {
    /// <summary>Gets or sets the interrupt request mask (the IF register's low five bits).</summary>
    InterruptKind Requested { get; set; }
    /// <summary>Gets or sets the interrupt enable mask (the IE register's low five bits).</summary>
    InterruptKind Enabled { get; set; }
    /// <summary>Gets the set of interrupts that are both requested and enabled — the ones eligible to dispatch and the
    /// ones that wake the CPU from HALT.</summary>
    InterruptKind Pending { get; }

    /// <summary>Raises an interrupt line, adding it to the request mask.</summary>
    /// <param name="kind">The source to request.</param>
    void Request(InterruptKind kind);
    /// <summary>Clears an interrupt line from the request mask, as the CPU does when it dispatches that interrupt.</summary>
    /// <param name="kind">The source to acknowledge.</param>
    void Acknowledge(InterruptKind kind);
}
