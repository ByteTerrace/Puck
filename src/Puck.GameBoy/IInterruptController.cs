namespace Puck.GameBoy;

/// <summary>
/// The interrupt controller: holds the enable mask (IE) and the request flags (IF) and arbitrates which pending
/// source the CPU services next, in hardware priority order.
/// </summary>
public interface IInterruptController {
    /// <summary>Gets or sets the interrupt-enable mask (the IE register at 0xFFFF).</summary>
    byte InterruptEnable { get; set; }
    /// <summary>Gets or sets the interrupt-request flags (the IF register at 0xFF0F; unused upper bits read as one).</summary>
    byte InterruptFlag { get; set; }
    /// <summary>Gets whether any enabled source is currently requesting.</summary>
    bool HasPending { get; }
    /// <summary>Requests an interrupt, setting its IF flag.</summary>
    void Request(InterruptKind kind);
    /// <summary>Clears an interrupt request, clearing its IF flag.</summary>
    void Clear(InterruptKind kind);
    /// <summary>Gets the highest-priority enabled, requested interrupt, if one is pending.</summary>
    bool TryGetPending(out InterruptKind kind);
}
