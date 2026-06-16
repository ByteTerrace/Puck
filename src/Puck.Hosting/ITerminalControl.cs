namespace Puck.Hosting;

/// <summary>
/// The "baton": terminal ownership — a held capability. The engine that holds it (the primary) may drive
/// the terminal's lifecycle; engines that do not hold it may render but cannot drive the terminal. It is
/// resolved through <see cref="IHostContext.HoldsCapability{TCapability}"/>, so — unlike an inherited
/// capability — it does not propagate down the tree; a host grants it to a child only by re-publishing it on
/// the child's context. <see cref="IInputFocus"/> is its companion held capability, granted independently.
/// </summary>
public interface ITerminalControl {
    /// <summary>Asks the terminal to close.</summary>
    void RequestExit();
}
