using Puck.Commands;

namespace Puck.World;

/// <summary>
/// The console lines an operator wrote to hold a world's live values for the session (a state cell set, a document row
/// set), kept by the subject each line writes, so the latest line for a subject replaces the earlier one and the lines
/// replay in the order their subjects were last written. <c>world.pins</c> carries them across a restart beside the pins
/// it reads from their live homes; a reload keeps the values they wrote by itself.
/// </summary>
/// <remarks>A line enters only once its verdict accepts it: a refused write pins nothing. The verbs that write the
/// subjects record their own lines (<see cref="Track"/>), each naming the subject by the tokens before its value, so a
/// new pinning verb records itself and no list of verbs is kept here. Safe from any thread: a verdict settles wherever
/// the authority answers.</remarks>
public sealed class WorldPinLedger {
    private readonly Lock m_gate = new();
    private readonly List<(string Subject, string Line)> m_lines = [];

    /// <summary>Gets the recorded lines, oldest subject first.</summary>
    public IReadOnlyList<string> Lines {
        get {
            lock (m_gate) { return [.. m_lines.Select(selector: static entry => entry.Line)]; }
        }
    }

    /// <summary>Records a pinning verb's line once its verdict accepts it, and returns the verb's result unchanged.</summary>
    /// <param name="result">The verb's result: its verdict, or the settlement the verdict arrives on.</param>
    /// <param name="context">The line's context; a line with no text (a bound press) records nothing.</param>
    /// <param name="verb">The verb, as the line names it.</param>
    /// <param name="subjectTokens">The tokens after the verb that name what the line writes, before its value.</param>
    /// <param name="args">The line's arguments.</param>
    /// <returns><paramref name="result"/>.</returns>
    public CommandResult Track(CommandResult result, in CommandContext context, in WireArgs args, string verb, int subjectTokens) {
        if ((context.Text is not { } line) || result.IsError || (args.Count < subjectTokens)) { return result; }

        var subject = new System.Text.StringBuilder(value: verb);

        for (var index = 0; (index < subjectTokens); index++) { _ = subject.Append(value: ' ').Append(value: args[index].ToString()); }

        var key = subject.ToString();
        var text = line.Trim();

        if (result.Settlement is { } settlement) {
            settlement.OnSettled(continuation: verdict => {
                if (!verdict.IsError) { Note(line: text, subject: key); }
            });
        } else {
            Note(line: text, subject: key);
        }
        return result;
    }
    /// <summary>Forgets every recorded line.</summary>
    public void Clear() {
        lock (m_gate) { m_lines.Clear(); }
    }

    private void Note(string subject, string line) {
        lock (m_gate) {
            _ = m_lines.RemoveAll(match: entry => string.Equals(a: entry.Subject, b: subject, comparisonType: StringComparison.Ordinal));
            m_lines.Add(item: (subject, line));
        }
    }
}
