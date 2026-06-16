namespace Puck.Commands;

/// <summary>
/// Receives command activations pushed by an <see cref="ICommandSource"/>.
/// </summary>
public interface ICommandSink {
    /// <summary>Submits a command activation for processing.</summary>
    /// <param name="signal">
    /// The activation to process. A signal that names an unknown command, or a command whose map is not active, is silently ignored.
    /// </param>
    void Push(CommandSignal signal);
}
