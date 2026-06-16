namespace Puck.Commands;

/// <summary>
/// Represents a single command activation emitted by an <see cref="ICommandSource"/> and pushed into
/// an <see cref="ICommandSink"/>.
/// </summary>
/// <remarks>
/// The receiving sink records <paramref name="Value"/> as the command's value for the current frame
/// and runs the command's handler, subject to command-map gating.
/// </remarks>
/// <param name="Name">The name of the command being activated.</param>
/// <param name="Value">The value carried by the activation for the current frame.</param>
/// <param name="Phase">The transition the activation represents.</param>
/// <param name="Text">
/// An optional text payload for text-bearing commands (for example, a console insertion) that the
/// numeric <paramref name="Value"/> cannot represent.
/// </param>
public readonly record struct CommandSignal(string Name, CommandValue Value, CommandPhase Phase, string? Text = null);
