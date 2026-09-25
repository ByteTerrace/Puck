namespace Puck.Commands;

/// <summary>
/// Represents the result a command handler returns for display in a transcript.
/// </summary>
/// <remarks>
/// Handlers return their output as data rather than writing to standard output. A command whose effect is
/// not transcript output — a continuous control driven by <see cref="CommandContext.Value"/>, for example —
/// returns <see cref="None"/>.
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
    /// <summary>
    /// Gets a value indicating whether the handler THREW rather than returning this verdict — the one
    /// <see cref="IsError"/> result that says nothing about the command's subject. Set only by the registry's own
    /// exception boundary, never by a handler, so a caller that must separate "the world said no" from "the host
    /// broke" has the distinction the text alone cannot carry.
    /// </summary>
    public bool Faulted { get; init; }
    /// <summary>
    /// Gets the pending verdict of work this handler started and an authority decides later, or
    /// <see langword="null"/> when this result is the whole of what the command did. A session that settles results
    /// reports the verdict in this result's place and holds its next line behind it.
    /// </summary>
    public CommandSettlement? Settlement { get; init; }
    /// <summary>Gets a result that produces no transcript output and leaves the transcript unchanged.</summary>
    public static CommandResult None => new("");

    /// <summary>Creates a result for work whose verdict arrives later, or returns that verdict itself when it arrived
    /// before the handler returned: a settled verdict is the handler's own result, so every output sink reports it
    /// and <c>wire.errors</c> counts it like any synchronous refusal, never left on a settlement no sink reads.</summary>
    /// <param name="settlement">The pending verdict, which its creator settles on every path.</param>
    /// <param name="late">The sink that reports a verdict arriving after this call, or <see langword="null"/> when the
    /// settlement's creator reports it; it is never invoked for a verdict this call returns.</param>
    /// <returns>The verdict when <paramref name="settlement"/> is already settled; otherwise a result with no output of
    /// its own and <see cref="Settlement"/> set.</returns>
    public static CommandResult Settling(CommandSettlement settlement, Action<CommandResult>? late = null) {
        ArgumentNullException.ThrowIfNull(argument: settlement);

        if (settlement.TryTakeVerdict(
            late: late,
            verdict: out var verdict
        )) {
            return verdict;
        }

        return new(Output: "") {
            Settlement = settlement,
        };
    }
    /// <summary>Creates a result that requests the transcript be cleared and produces no output.</summary>
    /// <returns>A result with <see cref="ClearTranscript"/> set to <see langword="true"/>.</returns>
    public static CommandResult Cleared() => new(
        ClearTranscript: true,
        Output: ""
    );
    /// <summary>Creates an error result with transcript output.</summary>
    /// <param name="output">The text to append to the transcript.</param>
    /// <returns>A result with <see cref="IsError"/> set to <see langword="true"/>.</returns>
    public static CommandResult Error(string output) => new(Output: output) {
        IsError = true,
    };
    /// <summary>Refuses a wire call carrying any trailing token for a verb that takes none — the shared body every
    /// zero-argument verb's argument check reduces to: <c>[verb: unrecognized '&lt;token&gt;' — expected no
    /// arguments]</c>.</summary>
    /// <param name="args">The verb's wire arguments.</param>
    /// <param name="verb">The verb name.</param>
    /// <returns>The refusal, or <see langword="null"/> when no argument was passed.</returns>
    public static CommandResult? RequireNoArguments(in WireArgs args, string verb) => ((args.Count == 0)
        ? null
        : Error(output: $"[{verb}: unrecognized '{args[0]}' — expected no arguments]")
    );
    /// <summary>Creates the shared wrong-argument-count refusal every command module hand-spelled locally:
    /// <c>[verb: expected form]</c>, or <c>[verb: expected no arguments]</c> when <paramref name="form"/> is
    /// empty.</summary>
    /// <param name="verb">The verb name.</param>
    /// <param name="form">The expected argument grammar, or empty for a no-argument verb.</param>
    /// <returns>A result with <see cref="IsError"/> set to <see langword="true"/>.</returns>
    public static CommandResult Usage(string verb, string form) => Error(output: (string.IsNullOrEmpty(value: form)
        ? $"[{verb}: expected no arguments]"
        : $"[{verb}: expected {form}]"));
}
