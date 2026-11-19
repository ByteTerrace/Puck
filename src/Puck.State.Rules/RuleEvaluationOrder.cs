namespace Puck.State.Rules;

/// <summary>Where one rule sits in a tick's evaluation: the place of its line, which is where it is checked, and the
/// place of its writes, which is where they can land.</summary>
/// <param name="Line">The place of the rule's line, or <c>-1</c> when it may run anywhere in the tick.</param>
/// <param name="Writer">The place of the rule's writes.</param>
public readonly record struct RulePlace(long Line, long Writer);
/// <summary>The order a host evaluates one rule array's tick in: the rules no group claims, in document order, then
/// every group's passes, then the host's interactions in theirs. A host evaluates <see cref="Ungrouped"/> directly and
/// the groups through <c>EvaluateGroups</c>; the work sheet places each line and write through <see cref="Place"/> and
/// <see cref="InteractionPlace"/>, so the sheet and the host read one ordering and cannot disagree on it.</summary>
public sealed class RuleEvaluationOrder {
    private readonly bool[] m_claimed;
    private readonly int[] m_rank;

    private RuleEvaluationOrder(CompiledRule[] rules, bool[] claimed) {
        m_claimed = claimed;
        m_rank = new int[rules.Length];
        Rules = rules;

        var unclaimed = 0;

        for (var index = 0; (index < rules.Length); index++) {
            m_rank[index] = unclaimed;
            if (!claimed[index]) {
                unclaimed++;
            }
        }
        Ungrouped = Unclaimed(claimed: claimed, count: unclaimed, rules: rules);
    }

    /// <summary>Gets the rules the order covers, in the order the compiler produced them.</summary>
    public CompiledRule[] Rules { get; }
    /// <summary>Gets the rules no group claims, in document order. A claimed rule runs only under its group's pass or
    /// cursor, so evaluating the whole rule array beside the groups would fire every member twice per tick.</summary>
    public CompiledRule[] Ungrouped { get; }

    /// <summary>Returns the order over rules and the compiled groups that claim them.</summary>
    /// <param name="rules">The compiled rules, in the order <see cref="CompiledRuleGroup.Members"/> indexes.</param>
    /// <param name="groups">The compiled groups over those rules.</param>
    /// <returns>The order.</returns>
    public static RuleEvaluationOrder Of(CompiledRule[] rules, CompiledRuleGroup[] groups) {
        ArgumentNullException.ThrowIfNull(argument: groups);
        ArgumentNullException.ThrowIfNull(argument: rules);

        var claimed = new bool[rules.Length];

        foreach (var group in groups) {
            foreach (var member in group.Members) {
                if (((uint)member) < ((uint)claimed.Length)) {
                    claimed[member] = true;
                }
            }
        }

        return new(claimed: claimed, rules: rules);
    }
    /// <summary>Returns the order over rules and the authored groups that name them, for a caller whose groups did not
    /// compile and so carry no member indices.</summary>
    /// <param name="rules">The compiled rules.</param>
    /// <param name="groups">The authored groups; a rule is claimed when any step names it.</param>
    /// <returns>The order.</returns>
    public static RuleEvaluationOrder Named(CompiledRule[] rules, IReadOnlyList<RuleGroupDeclaration>? groups) {
        ArgumentNullException.ThrowIfNull(argument: rules);

        var claimed = new bool[rules.Length];

        if (groups is { Count: > 0 }) {
            var names = new HashSet<string>(comparer: StringComparer.Ordinal);

            foreach (var group in groups) {
                foreach (var step in (group?.Steps ?? [])) {
                    _ = names.Add(item: (step?.Rule.Value ?? string.Empty));
                }
            }
            for (var index = 0; (index < rules.Length); index++) {
                claimed[index] = names.Contains(item: rules[index].Name);
            }
        }

        return new(claimed: claimed, rules: rules);
    }

    /// <summary>Returns where one rule of <see cref="Rules"/> runs. An ungrouped rule's line and writes sit at its
    /// position among the ungrouped rules. A grouped rule's line may run in any pass of its group, so it may run
    /// anywhere (<c>-1</c>), and its writes land after every ungrouped rule and before every interaction.</summary>
    /// <param name="index">The rule's index in <see cref="Rules"/>.</param>
    /// <returns>The rule's place.</returns>
    public RulePlace Place(int index) {
        if (m_claimed[index]) {
            return new(Line: -1L, Writer: Ungrouped.Length);
        }

        var rank = ((long)m_rank[index]);

        return new(Line: rank, Writer: rank);
    }
    /// <summary>Returns where one interaction runs: after every rule, in the interactions' own order.</summary>
    /// <param name="index">The interaction's index in the host's interaction array.</param>
    /// <returns>The interaction's place.</returns>
    public long InteractionPlace(int index) => ((Ungrouped.Length + 1L) + index);

    private static CompiledRule[] Unclaimed(bool[] claimed, int count, CompiledRule[] rules) {
        if (count == rules.Length) {
            return rules;
        }

        var unclaimed = new CompiledRule[count];
        var next = 0;

        for (var index = 0; (index < rules.Length); index++) {
            if (!claimed[index]) {
                unclaimed[next++] = rules[index];
            }
        }

        return unclaimed;
    }
}
