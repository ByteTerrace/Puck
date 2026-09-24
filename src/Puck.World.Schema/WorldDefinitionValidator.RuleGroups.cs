using RuleGroupCapacity = Puck.State.Rules.RuleGroupCapacity;
using RuleGroupShape = Puck.State.Rules.RuleGroupShape;
using RuleGroupStepPolicy = Puck.State.Rules.RuleGroupStepPolicy;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // The shape the `ruleGroups` member must have for the rule compiler to reach it at all: a unique name that is
    // not a rule's, members the document declares, and a rule claimed at most once. What a group MEANS — its write
    // set, its trigger's operands, its cost — is `RuleCompiler.CompileGroups`, run below once the shape holds, so
    // a trigger naming something unresolvable is an aggregated validation error rather than a throw at install.
    private static Puck.State.Rules.CompiledRuleGroup[] ValidateRuleGroups(WorldDefinition definition, Puck.State.Rules.CompiledRule[] rules, List<string> errors, ref WorldFactsCompileContext? context) {
        if (definition.RuleGroupsRaw is not { Count: > 0 } groups) {
            return [];
        }

        var before = errors.Count;

        var claimed = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        var declared = new HashSet<string>(comparer: StringComparer.Ordinal);
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var rule in (definition.Rules ?? [])) {
            if (rule is not null) {
                _ = declared.Add(item: rule.Name.Value);
            }
        }

        for (var index = 0; (index < groups.Count); index++) {
            var path = $"ruleGroups[{index}]";

            if (groups[index] is not { } group) {
                errors.Add(item: $"{path} is null.");

                continue;
            }

            var name = group.Name.Value;

            if (!names.Add(item: name)) {
                errors.Add(item: $"{path}.name '{name}' is declared twice.");
            }
            if (declared.Contains(item: name)) {
                errors.Add(item: $"{path}.name '{name}' is also a rule's name — a group and a rule never share one.");
            }
            if (!Enum.IsDefined(value: group.Shape)) {
                errors.Add(item: $"{path}.shape '{group.Shape}' is not a declared rule-group shape.");
            }
            if (group.Steps is not { Count: > 0 } steps) {
                errors.Add(item: $"{path}.steps declares no member — list at least one rule, or drop the group.");

                continue;
            }
            if (steps.Count > RuleGroupCapacity.MaxMembers) {
                errors.Add(item: $"{path}.steps claims {steps.Count} members, past the {RuleGroupCapacity.MaxMembers}-member ceiling.");
            }
            if (group.Shape == RuleGroupShape.Fixpoint) {
                if ((group.Passes is { } passes) && ((passes < 1) || (passes > RuleGroupCapacity.MaxPasses))) {
                    errors.Add(item: $"{path}.passes is {passes}, outside 1..{RuleGroupCapacity.MaxPasses}.");
                }
            } else if (group.Passes is not null) {
                errors.Add(item: $"{path}.passes is declared on a staged group — a staged group advances one step at a time and counts no passes.");
            }

            for (var step = 0; (step < steps.Count); step++) {
                var member = $"{path}.steps[{step}]";

                if (steps[step] is not { } declaredStep) {
                    errors.Add(item: $"{member} is null.");

                    continue;
                }

                var rule = declaredStep.Rule.Value;

                if (!declared.Contains(item: rule)) {
                    errors.Add(item: $"{member}.rule '{rule}' names no declared rule.");
                }
                if (!claimed.TryAdd(key: rule, value: name)) {
                    errors.Add(item: $"{member}.rule '{rule}' is already claimed by group '{claimed[rule]}' — a rule belongs to at most one group.");
                }
                if (!Enum.IsDefined(value: declaredStep.OnRefusal)) {
                    errors.Add(item: $"{member}.onRefusal '{declaredStep.OnRefusal}' is not a declared step policy.");
                } else if ((group.Shape == RuleGroupShape.Fixpoint) && (declaredStep.OnRefusal != RuleGroupStepPolicy.Stall)) {
                    errors.Add(item: $"{member}.onRefusal is declared on a fixpoint group — a step policy governs a staged cursor.");
                }
            }
        }

        // A group indexes the compiled rule array, so a rules section that itself refused compilation leaves
        // nothing to resolve members against; the rule refusal is already collected and stands alone.
        if (
            (errors.Count != before) ||
            (rules.Length == 0)
        ) {
            return [];
        }

        try {
            return Puck.State.Rules.RuleCompiler.CompileGroups(
                context: context ??= WorldFactsCompiler.Context(definition: definition),
                groups: groups,
                rules: rules
            );
        } catch (RuleException exception) {
            errors.Add(item: LocateRuleRefusal(
                definition: definition,
                exception: exception
            ));

            return [];
        }
    }
    // A declared cell set must name rows the document declares, because the algebra resolves a source by name at
    // lowering time and a name that resolves to nothing has no width to combine or complement against.
    private static void ValidateCellSets(WorldDefinition definition, List<string> errors) {
        if (definition.SetsRaw is not { Count: > 0 } sets) {
            return;
        }

        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        var rows = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var row in (definition.StateRaw?.World ?? [])) {
            if (row is not null) {
                _ = rows.Add(item: row.Name.Value);
            }
        }

        var families = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var family in (definition.StateRaw?.Families ?? [])) {
            if (family is not null) {
                _ = families.Add(item: family.Name.Value);
            }
        }

        for (var index = 0; (index < sets.Count); index++) {
            var path = $"sets[{index}]";

            if (sets[index] is not { } declared) {
                errors.Add(item: $"{path} is null.");

                continue;
            }
            if (!names.Add(item: declared.Name.Value)) {
                errors.Add(item: $"{path}.name '{declared.Name.Value}' is declared twice.");
            }
            // boardCombine and writeSet read a set of positions from one bare name, a board row's or a set's.
            if (rows.Contains(item: declared.Name.Value)) {
                errors.Add(item: $"{path}.name '{declared.Name.Value}' is also a state.world row; a transform reading a set of positions names one or the other.");
            }
            if (declared.Set is null) {
                errors.Add(item: $"{path}.set is null.");

                continue;
            }

            Walk(
                expression: declared.Set,
                path: $"{path}.set"
            );
        }

        void Walk(CellSetExpression expression, string path) {
            switch (expression) {
                case CellSetExpression.Board board when !rows.Contains(item: board.Row.Value):
                    errors.Add(item: $"{path} reads board '{board.Row.Value}', which state.world does not declare.");
                    break;

                case CellSetExpression.Zone zone when !rows.Contains(item: zone.Row.Value):
                    errors.Add(item: $"{path} reads zone '{zone.Row.Value}', which state.world does not declare.");
                    break;

                case CellSetExpression.Family family when !families.Contains(item: family.Name.Value):
                    errors.Add(item: $"{path} reads family '{family.Name.Value}', which state.families does not declare.");
                    break;

                case CellSetExpression.Any any:
                    for (var item = 0; (item < any.Items.Count); item++) {
                        Walk(expression: any.Items[item], path: $"{path}[{item}]");
                    }

                    break;

                case CellSetExpression.Both both:
                    for (var item = 0; (item < both.Items.Count); item++) {
                        Walk(expression: both.Items[item], path: $"{path}[{item}]");
                    }

                    break;

                case CellSetExpression.Complement complement:
                    Walk(expression: complement.Item, path: path);
                    break;

                default:
                    break;
            }
        }
    }
}
