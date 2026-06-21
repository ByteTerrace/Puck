namespace Puck.Commands;

/// <summary>
/// Describes a single command dispatch as seen after the handler ran — the data an
/// <see cref="ICommandObserver"/> renders, logs, or asserts on, regardless of which source drove it.
/// </summary>
/// <param name="Name">The dispatched command's unique name.</param>
/// <param name="Phase">The transition this dispatch represents.</param>
/// <param name="Result">The result the handler returned.</param>
/// <param name="Text">The optional text payload that drove the dispatch.</param>
public readonly record struct CommandActivation(
    string Name,
    CommandPhase Phase,
    CommandResult Result,
    string? Text = null
);
