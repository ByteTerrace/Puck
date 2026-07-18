namespace Puck.Commands;

/// <summary>
/// Represents the result a command handler returns for display in a transcript.
/// </summary>
/// <remarks>
/// Handlers return their output as data rather than writing to standard output. Commands that act as
/// continuous controls return <see cref="None"/>; their effect is observed by polling the command
/// value instead of through transcript output.
/// </remarks>
/// <param name="Output">The text to append to the transcript.</param>
/// <param name="ClearTranscript"><see langword="true"/> to request that the transcript be cleared.</param>
public readonly record struct CommandResult(string Output, bool ClearTranscript = false) {
    /// <summary>
    /// Whether this result reports a FAILURE (a bad argument count, an unparsable value, an unknown target). Defaults to
    /// <see langword="false"/>, so every existing result is a success and nothing changes. It is the wire's
    /// acknowledgement discriminator: the registry's <c>wire.ack quiet</c> mode suppresses a SUCCESS echo from a
    /// wire-native verb but ALWAYS surfaces an error, so a scripted run still sees its failures on a quiet pipe. A
    /// wire-native (<see cref="CommandDefinition.WithWireArgs"/>) handler is therefore contractually required to set
    /// <c>IsError: true</c> on every failure return — that is what makes quiet mode safe.
    /// </summary>
    public bool IsError { get; init; }

    /// <summary>Gets a result that produces no transcript output and leaves the transcript unchanged.</summary>
    public static CommandResult None => new("");

    /// <summary>Creates a result that requests the transcript be cleared and produces no output.</summary>
    /// <returns>A result with <see cref="ClearTranscript"/> set to <see langword="true"/>.</returns>
    public static CommandResult Cleared() => new(
        ClearTranscript: true,
        Output: ""
    );
}
