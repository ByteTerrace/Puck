namespace Puck.Commands;

/// <summary>
/// Observes each source-driven (pushed) command activation in the <see cref="CommandRegistry"/> — the path
/// that has no return value to inspect. Handlers return their effect as data; observers decide where it goes
/// — a console echo, a transcript overlay, a log, or a test assertion — without the handlers knowing. The
/// text path (<see cref="CommandRegistry.Submit"/>) returns its result to the caller instead.
/// </summary>
public interface ICommandObserver {
    /// <summary>Called once per command dispatch, immediately after the handler ran.</summary>
    /// <param name="activation">The command, phase, result, and text payload for this dispatch.</param>
    void OnCommand(in CommandActivation activation);
}
