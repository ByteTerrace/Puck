using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.State.Rules;

/// <summary>How a rule group runs its members.</summary>
[JsonConverter(typeof(StrictEnumConverter<RuleGroupShape>))]
public enum RuleGroupShape : byte {
    /// <summary>One pass per tick over every member, repeated until a pass leaves no row of the group's write set
    /// changed or the pass ceiling is reached.</summary>
    Fixpoint,

    /// <summary>A cursor over ordered steps: a step advances when its own effects commit.</summary>
    Staged,
}
/// <summary>What a staged group does with a step whose effects refuse.</summary>
[JsonConverter(typeof(StrictEnumConverter<RuleGroupStepPolicy>))]
public enum RuleGroupStepPolicy : byte {
    /// <summary>The cursor stays on the step and retries it next tick.</summary>
    Stall,

    /// <summary>The cursor advances past the step's own refusal.</summary>
    Skip,
}
/// <summary>One step of a staged group: the rule it runs and what a refusal does to the cursor.</summary>
/// <param name="Rule">The member rule's name.</param>
/// <param name="OnRefusal">What a refusal does to the cursor.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuleGroupStep(CellName Rule, RuleGroupStepPolicy OnRefusal = RuleGroupStepPolicy.Stall);
/// <summary>One authored rule group: the members it claims, its shape, its pass ceiling, and the gate that arms
/// it.</summary>
/// <param name="Name">The group's stable name — unique within the section, and never a rule's name.</param>
/// <param name="Shape">Fixpoint or staged.</param>
/// <param name="Steps">The members, in authored order; for a staged group this is the step sequence, and the last
/// step is terminal.</param>
/// <param name="Passes">The pass ceiling a fixpoint group breaches into a counted refusal, or
/// <see langword="null"/> for <see cref="RuleGroupCapacity.DefaultPasses"/>.</param>
/// <param name="Trigger">The gate that arms the group, or <see langword="null"/> for always.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuleGroupDeclaration(CellName Name, RuleGroupShape Shape, IReadOnlyList<RuleGroupStep> Steps, int? Passes = null, ActionPredicate? Trigger = null);
/// <summary>Hard bounds for rule groups. A fixpoint group runs one pass a tick, so a tick's work is its members',
/// which the work sheet prices as the rules they are; its passes count ticks.</summary>
public static class RuleGroupCapacity {
    /// <summary>The pass ceiling a fixpoint group takes when it authors none.</summary>
    public const int DefaultPasses = 8;
    /// <summary>The most members one group may claim: one index and one policy each in the compiled group.</summary>
    public const int MaxMembers = 256;
    /// <summary>The most passes one fixpoint group may author: the ticks a cascade may take to settle before the
    /// group reports a breach.</summary>
    public const int MaxPasses = 256;
}
/// <summary>One compiled rule group: which compiled rules it claims, how it runs them, the rows a pass must leave
/// unchanged to close, its pass ceiling, and the gate that arms it.</summary>
/// <param name="Name">The group's stable name.</param>
/// <param name="Shape">Fixpoint or staged.</param>
/// <param name="Members">The indices into the compiled rule array, in authored order.</param>
/// <param name="Policies">Per member, what a refusal does to a staged cursor; parallel to
/// <paramref name="Members"/>.</param>
/// <param name="WriteRows">The catalog ordinals of every row the members write, ascending and deduplicated. A
/// fixpoint pass closes when no row version in this set moved during the pass.</param>
/// <param name="Passes">The pass ceiling, whose breach is a counted refusal naming the group.</param>
/// <param name="Trigger">The compiled gate that arms the group; empty means always.</param>
/// <param name="TerminalStep">The index into <paramref name="Members"/> of the staged group's last step, or
/// <c>-1</c> for a fixpoint group.</param>
/// <param name="Describe">The group's read-back spelling.</param>
/// <param name="Needs">What the trigger's operands need of a host: its facets, a tick read, and any host-owned
/// row, computed exactly as a rule's are.</param>
/// <param name="Locals">The locals the trigger's expression keys minted, in evaluation order. The trigger's
/// local keys address these slots, so whoever evaluates the trigger evaluates these first.</param>
public sealed record CompiledRuleGroup(string Name, RuleGroupShape Shape, int[] Members, RuleGroupStepPolicy[] Policies, int[] WriteRows, int Passes, GateToken[] Trigger, int TerminalStep, string Describe, RuleNeeds Needs, CompiledRuleLocal[] Locals);
public static partial class RuleCompiler {
    /// <summary>Compiles every declared rule group against an already-compiled rule array, checking that each
    /// claims a unique, unreserved name, names only declared rules, and claims no rule another group claims.</summary>
    /// <param name="groups">The authored groups.</param>
    /// <param name="rules">The compiled rules, in the order <see cref="CompileAll"/> produced them.</param>
    /// <param name="context">The compile context.</param>
    /// <returns>The compiled groups, in document order.</returns>
    /// <exception cref="RuleException">A group's declaration is malformed.</exception>
    public static CompiledRuleGroup[] CompileGroups(IReadOnlyList<RuleGroupDeclaration>? groups, IReadOnlyList<CompiledRule> rules, RuleCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);
        ArgumentNullException.ThrowIfNull(argument: rules);

        if (groups is not { Count: > 0 }) {
            return [];
        }

        var byRule = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        var claimed = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        var compiled = new CompiledRuleGroup[groups.Count];
        var seen = new HashSet<string>(
            capacity: groups.Count,
            comparer: StringComparer.Ordinal
        );

        for (var index = 0; (index < rules.Count); index++) {
            byRule[rules[index].Name] = index;
        }

        for (var index = 0; (index < groups.Count); index++) {
            var group = groups[index];
            var name = (group?.Name.Value ?? string.Empty);

            RequireName(
                name: name,
                seen: seen,
                subject: "group"
            );
            if (byRule.ContainsKey(key: name)) {
                throw new RuleException(
                    detail: "carries a rule's name — a group and a rule never share one",
                    refusal: RuleRefusal.RuleGroupMalformed,
                    ruleName: name,
                    subject: "group"
                );
            }

            compiled[index] = CompileGroup(
                byRule: byRule,
                claimed: claimed,
                context: context,
                group: group!,
                rules: rules
            );
        }

        return compiled;
    }

    /// <summary>Returns the compiled rules no group claims, in the order they were compiled.</summary>
    /// <param name="rules">The compiled rules, in the order <see cref="CompileAll"/> produced them — the order
    /// <see cref="CompiledRuleGroup.Members"/> indexes.</param>
    /// <param name="groups">The compiled groups over those rules.</param>
    /// <returns>The unclaimed rules.</returns>
    /// <remarks>A claimed rule runs only under its group's pass or cursor, so a host evaluates this array directly
    /// and the group array through <c>EvaluateGroups</c>; evaluating the whole rule array beside the groups would
    /// fire every member twice per tick.</remarks>
    public static CompiledRule[] Ungrouped(CompiledRule[] rules, CompiledRuleGroup[] groups) {
        ArgumentNullException.ThrowIfNull(argument: groups);
        ArgumentNullException.ThrowIfNull(argument: rules);

        if (groups.Length == 0) {
            return rules;
        }

        var claimed = new bool[rules.Length];
        var count = rules.Length;

        foreach (var group in groups) {
            foreach (var member in group.Members) {
                if (
                    ((uint)member < (uint)claimed.Length) &&
                    !claimed[member]
                ) {
                    claimed[member] = true;
                    count--;
                }
            }
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

    private static CompiledRuleGroup CompileGroup(RuleGroupDeclaration group, IReadOnlyList<CompiledRule> rules, IReadOnlyDictionary<string, int> byRule, Dictionary<string, string> claimed, RuleCompileContext context) {
        var name = group.Name.Value;

        RuleException Malformed(string detail) => new(
            detail: detail,
            refusal: RuleRefusal.RuleGroupMalformed,
            ruleName: name,
            subject: "group"
        );

        if (group.Steps is not { Count: > 0 } steps) {
            throw Malformed(detail: "declares no members — list at least one rule, or drop the group");
        }
        if (steps.Count > RuleGroupCapacity.MaxMembers) {
            throw Malformed(detail: $"claims {steps.Count} members, exceeding the {RuleGroupCapacity.MaxMembers}-member ceiling");
        }

        var passes = (group.Passes ?? RuleGroupCapacity.DefaultPasses);

        if (group.Shape == RuleGroupShape.Fixpoint) {
            if (
                (passes < 1) ||
                (passes > RuleGroupCapacity.MaxPasses)
            ) {
                throw Malformed(detail: $"declares a pass ceiling of {passes}, outside 1..{RuleGroupCapacity.MaxPasses}");
            }
        } else if (group.Passes is not null) {
            throw Malformed(detail: "is staged and declares a pass ceiling — a staged group advances one step at a time and counts no passes");
        }

        var members = new int[steps.Count];
        var policies = new RuleGroupStepPolicy[steps.Count];
        var writeRows = new SortedSet<int>();

        for (var index = 0; (index < steps.Count); index++) {
            var step = (steps[index] ?? throw Malformed(detail: $"member {index} is null"));
            var memberName = step.Rule.Value;

            if (!byRule.TryGetValue(
                key: memberName,
                value: out var ordinal
            )) {
                throw Malformed(detail: $"names '{memberName}', which the section declares no rule for");
            }
            if (claimed.TryGetValue(
                key: memberName,
                value: out var owner
            )) {
                throw Malformed(detail: $"claims '{memberName}', which group '{owner}' already claims — a rule belongs to at most one group");
            }
            if (
                (group.Shape == RuleGroupShape.Fixpoint) &&
                (step.OnRefusal != RuleGroupStepPolicy.Stall)
            ) {
                throw Malformed(detail: $"is a fixpoint group and declares a step policy on '{memberName}' — a step policy governs a staged cursor");
            }

            claimed[memberName] = name;
            members[index] = ordinal;
            policies[index] = step.OnRefusal;

            var writes = new List<CellAccess>();

            rules[ordinal].CollectWrites(into: writes);
            foreach (var write in writes) {
                if (write.RowOrdinal >= 0) {
                    _ = writeRows.Add(item: write.RowOrdinal);
                }
            }
        }

        context.ClearScope();

        var trigger = CompileGate(
            context: context,
            predicate: group.Trigger,
            ruleName: name
        );

        // The trigger compiles in its own scope, so what that scope gathered is the group's: the locals its
        // expression keys minted, which the trigger's local keys address, and what its operands need of a host.
        var locals = AllLocals(
            context: context,
            declared: []
        );
        var needs = context.Needs.Build();

        context.ClearScope();

        return new CompiledRuleGroup(
            Locals: locals,
            Describe: name,
            Members: members,
            Name: name,
            Needs: needs,
            Passes: passes,
            Policies: policies,
            Shape: group.Shape,
            TerminalStep: ((group.Shape == RuleGroupShape.Staged)
                ? (members.Length - 1)
                : -1),
            Trigger: trigger,
            WriteRows: [.. writeRows]
        );
    }
}
