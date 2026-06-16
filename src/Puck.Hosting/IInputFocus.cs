namespace Puck.Hosting;

/// <summary>
/// Input focus: a held capability — the right to receive the terminal's input. The terminal routes its
/// input only to the engine that holds focus. Like the <see cref="ITerminalControl"/> baton it is held by a
/// single engine and does not propagate to children (resolved via
/// <see cref="IHostContext.HoldsCapability{TCapability}"/>), but it is granted <em>independently</em>: a host
/// may hand a child input focus without the baton, so a hosted engine can render and take input yet not be
/// able to drive the terminal's lifecycle.
/// </summary>
public interface IInputFocus {
    /// <summary>Gets whether input is currently being routed to the holder. The terminal stops routing when
    /// focus is released (or the terminal is not foreground).</summary>
    bool IsActive { get; }

    /// <summary>Yields input focus: the terminal stops routing input to the holder until focus is granted
    /// to an engine again.</summary>
    void Release();
}
