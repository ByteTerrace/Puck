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
    /// <summary>Gets a result that produces no transcript output and leaves the transcript unchanged.</summary>
    public static CommandResult None => new("");

    /// <summary>Creates a result that requests the transcript be cleared and produces no output.</summary>
    /// <returns>A result with <see cref="ClearTranscript"/> set to <see langword="true"/>.</returns>
    public static CommandResult Cleared() => new(
        ClearTranscript: true,
        Output: ""
    );
}
