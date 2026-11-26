using Puck.Commands;

namespace Puck.World;

/// <summary>
/// The console lines an operator wrote to hold a world's live values for the session (a state cell set, a document row
/// set), kept by the subject each line writes, so the latest line for a subject replaces the earlier one and the lines
/// replay in the order their subjects were last written. <c>world.pins</c> carries them across a restart beside the pins
/// it reads from their live homes; a reload keeps the values they wrote by itself.
/// </summary>
/// <remarks>A line enters only once its verdict accepts it: a refused write pins nothing. The verbs that write the
/// subjects record their own lines (<see cref="Track"/>), each naming its subject, so a new pinning verb records itself
/// and no list of verbs is kept here. A subject whose value moves after the line that wrote it (a state row that
/// advances, eases or cycles, or that a rule writes) is recorded with a reader of its live home, and the ledger
/// answers that reading in the line's place. Safe from any thread: a verdict settles wherever the authority
/// answers.</remarks>
public sealed class WorldPinLedger {
    private readonly Lock m_gate = new();
    private readonly List<(string Subject, string Line, Func<string?>? Live)> m_lines = [];

    /// <summary>Gets the lines that set each recorded subject again, oldest subject first: a subject recorded with a
    /// live reader answers what that reader returns now, and its recorded line where the reader returns
    /// <see langword="null"/>.</summary>
    public IReadOnlyList<string> Lines {
        get {
            (string Subject, string Line, Func<string?>? Live)[] entries;

            lock (m_gate) { entries = [.. m_lines]; }

            // The readers run outside the gate: one may wait on an authority that is settling a verdict into Note.
            return [.. entries.Select(selector: static entry => (entry.Live?.Invoke() ?? entry.Line))];
        }
    }

    /// <summary>Returns the subject a line names by its leading tokens: the verb and the tokens before its value.</summary>
    /// <param name="verb">The verb, as the line names it.</param>
    /// <param name="args">The line's arguments.</param>
    /// <param name="tokens">The count of leading arguments that name what the line writes, at most the argument
    /// count.</param>
    /// <returns>The subject.</returns>
    public static string Subject(string verb, in WireArgs args, int tokens) {
        var subject = new System.Text.StringBuilder(value: verb);

        for (var index = 0; (index < Math.Min(val1: tokens, val2: args.Count)); index++) { _ = subject.Append(value: ' ').Append(value: args[index].ToString()); }

        return subject.ToString();
    }

    /// <summary>Records a pinning verb's line once its verdict accepts it, and returns the verb's result unchanged.</summary>
    /// <param name="result">The verb's result: its verdict, or the settlement the verdict arrives on.</param>
    /// <param name="context">The line's context; a line with no text (a bound press) records nothing.</param>
    /// <param name="subject">What the line writes: two lines with one subject hold the same thing, and the later
    /// replaces the earlier.</param>
    /// <param name="live">Reads the line that sets the subject as it stands when the pins are read, or
    /// <see langword="null"/> for a subject the recorded line itself restores. Called from any thread, outside this
    /// ledger's lock; a <see langword="null"/> return answers the recorded line.</param>
    /// <returns><paramref name="result"/>.</returns>
    public CommandResult Track(CommandResult result, in CommandContext context, string subject, Func<string?>? live = null) {
        if ((context.Text is not { } line) || result.IsError) { return result; }

        var text = line.Trim();

        if (result.Settlement is { } settlement) {
            settlement.OnSettled(continuation: verdict => {
                if (!verdict.IsError) { Note(line: text, live: live, subject: subject); }
            });
        } else {
            Note(line: text, live: live, subject: subject);
        }
        return result;
    }
    /// <summary>Forgets every recorded line.</summary>
    public void Clear() {
        lock (m_gate) { m_lines.Clear(); }
    }

    private void Note(string subject, string line, Func<string?>? live) {
        lock (m_gate) {
            _ = m_lines.RemoveAll(match: entry => string.Equals(a: entry.Subject, b: subject, comparisonType: StringComparison.Ordinal));
            m_lines.Add(item: (subject, line, live));
        }
    }
}
