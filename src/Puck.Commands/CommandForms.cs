namespace Puck.Commands;

/// <summary>
/// The argument forms of one console verb, spelled once. The verb's description reads <see cref="Spelling"/> (the
/// definition refuses a description that does not contain it) and every grammar refusal is <see cref="Refuse(string?)"/>,
/// the only door to <see cref="CommandResult"/>'s grammar refusal, so the two cannot drift apart.
/// </summary>
/// <remarks>A form is the text after the verb (<c>&lt;row&gt; &lt;key&gt; [add]</c>); a verb that accepts several
/// shapes declares one form per shape, and a refusal names the one nearest what was typed.</remarks>
public sealed class CommandForms {
    private readonly string[] m_forms;
    private readonly CommandUsage[] m_usages;

    private CommandForms(string verb, string[] forms) {
        m_forms = forms;
        m_usages = [.. forms.Select(selector: CommandUsage.ParseForm)];
        Verb = verb;
    }

    /// <summary>Gets the verb these forms belong to.</summary>
    public string Verb { get; }
    /// <summary>Gets the forms, each as spelled after the verb, in declaration order.</summary>
    public IReadOnlyList<string> Forms => m_forms;
    /// <summary>Gets every form joined as a description spells alternatives: <c>form | form</c>.</summary>
    public string Form => string.Join(separator: " | ", values: m_forms);
    /// <summary>Gets the verb followed by its forms, the text the verb's description contains.</summary>
    public string Spelling => $"{Verb} {Form}";
    /// <summary>Gets each form as data a caller can draw valid lines from.</summary>
    public IReadOnlyList<CommandUsage> Usages => m_usages;

    /// <summary>Declares a verb's forms.</summary>
    /// <param name="verb">The verb name.</param>
    /// <param name="forms">The forms, each the text after the verb and none empty; a verb that takes no argument declares
    /// none and refuses through <see cref="CommandResult.RequireNoArguments"/>.</param>
    /// <returns>The declared forms.</returns>
    /// <exception cref="ArgumentException"><paramref name="verb"/> is empty, <paramref name="forms"/> is empty, or a form
    /// is empty or holds <c> | </c>, which separates forms.</exception>
    public static CommandForms Of(string verb, params string[] forms) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: verb);
        ArgumentNullException.ThrowIfNull(argument: forms);

        if (forms.Length == 0) {
            throw new ArgumentException(
                message: $"'{verb}' declares no form; a verb that takes no argument has none.",
                paramName: nameof(forms)
            );
        }

        foreach (var form in forms) {
            if (string.IsNullOrWhiteSpace(value: form) || form.Contains(value: " | ", comparisonType: StringComparison.Ordinal)) {
                throw new ArgumentException(
                    message: $"A form of '{verb}' is empty or spells several forms in one: '{form}'.",
                    paramName: nameof(forms)
                );
            }
        }

        return new(
            forms: [.. forms],
            verb: verb
        );
    }

    /// <summary>Refuses a line whose grammar no form fits: <c>[verb: expected form | form]</c>, or <c>[verb: unrecognized
    /// 'got' — expected form | form]</c> when the caller knows the word it could not read.</summary>
    /// <param name="got">The offending token, or <see langword="null"/> when the grammar as a whole is wrong (a wrong
    /// argument count).</param>
    /// <returns>The grammar refusal.</returns>
    public CommandResult Refuse(string? got = null) => CommandResult.Usage(
        form: Form,
        got: got,
        verb: Verb
    );
    /// <summary>Refuses a line whose grammar no form fits, naming only the form nearest the words typed: the form
    /// leading with the first word, else the placeholder-led form whose word count is nearest. When every form leads
    /// with a literal the first word is not, no form is nearer and the refusal names them all.</summary>
    /// <param name="args">The words typed after the verb.</param>
    /// <param name="got">The offending token, or <see langword="null"/> when the grammar as a whole is wrong.</param>
    /// <returns>The grammar refusal.</returns>
    public CommandResult Refuse(in WireArgs args, string? got = null) {
        if (m_forms.Length == 1) { return Refuse(got: got); }

        var best = 0;
        var bestRank = (int.MaxValue, int.MaxValue);

        for (var index = 0; (index < m_forms.Length); index++) {
            var usage = m_usages[index];
            var lead = Lead(args: args, usage: usage);
            var distance = ((args.Count < usage.MinimumWords)
                ? (usage.MinimumWords - args.Count)
                : ((args.Count > usage.MaximumWords) ? (args.Count - usage.MaximumWords) : 0));

            if ((lead, distance).CompareTo(other: bestRank) < 0) {
                best = index;
                bestRank = (lead, distance);
            }
        }

        // Every form leads with a literal the first word is not: no form is nearer than another, so all are named.
        return CommandResult.Usage(
            form: ((bestRank.Item1 == 2) ? Form : m_forms[best]),
            got: got,
            verb: Verb
        );
    }

    /// <summary>Refuses a line whose grammar no form fits, naming the one form its caller knows applies: a verb whose
    /// shape depends on who invokes it, where the form nearest the words typed (<see cref="Refuse(in WireArgs, string?)"/>)
    /// may be another invoker's.</summary>
    /// <param name="index">The form's position in <see cref="Forms"/>.</param>
    /// <param name="got">The offending token, or <see langword="null"/> when the grammar as a whole is wrong.</param>
    /// <returns>The grammar refusal.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> names no form.</exception>
    public CommandResult RefuseForm(int index, string? got = null) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            other: m_forms.Length,
            value: index
        );

        return CommandResult.Usage(
            form: m_forms[index],
            got: got,
            verb: Verb
        );
    }

    // 0 when the form leads with a literal the first word spells, 1 when it leads with a placeholder, 2 when it leads
    // with a literal the first word is not.
    private static int Lead(in WireArgs args, CommandUsage usage) {
        if ((usage.Tokens.Count == 0) || (usage.Tokens[0].Kind != CommandUsageTokenKind.Word)) { return 1; }

        foreach (var name in usage.Tokens[0].Names) {
            if (args.Is(index: 0, value: name)) { return 0; }
        }

        return 2;
    }
}
