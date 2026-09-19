using System.Text.Json.Nodes;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;
using RuleGroupCapacity = Puck.State.Rules.RuleGroupCapacity;

namespace Puck.World.Transpiler.Lowering;

public static partial class WorldDocumentEmitter {
    /// <summary>Lowers a <c>stabilize</c> group into a fixpoint entry of the document's <c>ruleGroups</c> array and
    /// its member rules into <c>rules</c>.</summary>
    /// <remarks>The body is a rule scope: its <c>when</c>, <c>local</c> and property statements apply to every member,
    /// and each <c>rule</c> block becomes one member named <c>&lt;group&gt;_&lt;rule&gt;</c>. <c>maxPasses(N)</c> is
    /// the pass ceiling whose breach is a counted refusal; <c>until</c> arms the group while its gate reads false, so
    /// it lowers to the gate's negation.</remarks>
    private static void LowerStabilizeGroup(
        JsonObject parent,
        StabilizeGroupNode stabilizeNode,
        DocumentScope scope,
        RuleScopeContext? parentContext = null
    ) {
        var groupName = PrefixedName(
            name: stabilizeNode.Name,
            prefix: parentContext?.Prefix
        );

        if (string.IsNullOrEmpty(value: groupName)) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.RuleGroupShapeInadmissible,
                message: "a stabilize group needs a name; the group's name is what its refusals and its checkpoint progress are counted under.",
                span: stabilizeNode.Span
            );

            return;
        }

        var context = LowerGroupScope(
            headerWhen: null,
            parentContext: parentContext,
            prefix: groupName,
            scope: scope,
            statements: stabilizeNode.Statements
        );
        var steps = new JsonArray();

        foreach (var statement in stabilizeNode.Statements) {
            if (statement is not RuleBlockNode member) {
                continue;
            }

            // A member's name is whatever the rule lowering resolves it to, so an interpolated name reaches the step
            // rather than the empty string the header wrote.
            var memberName = (DocumentLowering.ResolveRuleName(
                rule: member,
                scope: scope
            ) ?? member.Name);

            LowerRuleBlock(
                parent: parent,
                rule: member,
                scope: scope,
                scopeContext: context
            );
            steps.AppendNode(item: new JsonObject {
                ["rule"] = JsonValue.Create(value: PrefixedName(
                    name: memberName,
                    prefix: groupName
                )),
            });
        }

        if (steps.Count == 0) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.RuleGroupShapeInadmissible,
                message: $"stabilize group '{groupName}' declares no rule; write at least one `rule` block in its body, or drop the group.",
                span: stabilizeNode.Span
            );

            return;
        }

        var group = new JsonObject {
            ["name"] = JsonValue.Create(value: groupName),
            ["shape"] = JsonValue.Create(value: "Fixpoint"),
            ["steps"] = steps,
        };

        if (stabilizeNode.MaxPasses is { } passesExpr) {
            var passes = EvaluateGroupPasses(
                expression: passesExpr,
                name: groupName,
                scope: scope,
                span: stabilizeNode.Span
            );

            if (passes <= 0) {
                return;
            }

            group["passes"] = JsonValue.Create(value: passes);
        }
        if (stabilizeNode.UntilCondition is { } until) {
            group["trigger"] = new JsonObject {
                ["$type"] = JsonValue.Create(value: "not"),
                ["predicate"] = LowerPredicate(
                    node: until,
                    scope: scope
                ),
            };
        }

        AppendRuleGroup(
            group: group,
            parent: parent,
            scope: scope,
            span: stabilizeNode.Span
        );
    }
    /// <summary>Folds a group body's shared <c>when</c>, <c>local</c> and property statements into the scope context
    /// its member rules lower under.</summary>
    /// <param name="statements">The group body, in source order.</param>
    /// <param name="prefix">The member rules' name prefix — the group's own name.</param>
    /// <param name="headerWhen">A gate authored on the group's header, or <see langword="null"/>.</param>
    /// <param name="scope">The document scope.</param>
    /// <param name="parentContext">The enclosing rule scope, or <see langword="null"/>.</param>
    /// <returns>The context every member rule lowers under.</returns>
    private static RuleScopeContext LowerGroupScope(
        IReadOnlyList<StatementNode> statements,
        string prefix,
        PredicateNode? headerWhen,
        DocumentScope scope,
        RuleScopeContext? parentContext
    ) {
        var locals = new List<JsonObject>();
        var gate = (parentContext?.Gate?.DeepClone() as JsonObject);
        var properties = new Dictionary<string, JsonNode?>(comparer: StringComparer.Ordinal);

        if (parentContext?.Locals is { Count: > 0 } inherited) {
            foreach (var local in inherited) {
                locals.Add(item: ((JsonObject)local.DeepClone()));
            }
        }
        if (parentContext?.Properties is { Count: > 0 } inheritedProperties) {
            foreach (var (name, value) in inheritedProperties) {
                properties[name] = value?.DeepClone();
            }
        }
        if (headerWhen is not null) {
            gate = CombineGates(
                existing: gate,
                newGate: LowerPredicate(
                    node: headerWhen,
                    scope: scope
                )
            );
        }

        foreach (var statement in statements) {
            switch (statement) {
                case WhenStatementNode when1:
                    gate = CombineGates(
                        existing: gate,
                        newGate: LowerPredicate(
                            node: when1.Predicate,
                            scope: scope
                        )
                    );
                    break;

                case LocalStatementNode local:
                    locals.Add(item: LowerLocal(local: local));
                    break;

                case PropertyNode property:
                    var lowered = LowerExpression(
                        expr: property.Value,
                        fieldKey: property.Name,
                        scope: scope
                    );

                    properties[property.Name] = lowered;
                    break;
            }
        }

        return new RuleScopeContext(
            Locals: ((locals.Count > 0)
                ? locals
                : null),
            Gate: gate,
            Prefix: prefix,
            Properties: ((properties.Count > 0)
                ? properties
                : null)
        );
    }
    /// <summary>Appends one lowered group to the document's <c>ruleGroups</c> array, creating it on first use, and
    /// registers the entry's own pointer so a refusal about the group names the line that declared it.</summary>
    /// <param name="parent">The document object the group belongs to.</param>
    /// <param name="group">The lowered group.</param>
    /// <param name="scope">The document scope whose source map the pointer is registered in.</param>
    /// <param name="span">The span of the <c>stabilize</c> or <c>workflow</c> statement that declared it.</param>
    private static void AppendRuleGroup(JsonObject parent, JsonObject group, DocumentScope scope, SourceSpan span) {
        if (parent["ruleGroups"] is not JsonArray groups) {
            groups = [];
            parent["ruleGroups"] = groups;
        }

        scope.SourceMap?.Register(
            jsonPointer: $"{scope.CurrentPointer}/ruleGroups/{groups.Count}",
            span: span
        );
        groups.AppendNode(item: group);
    }
    /// <summary>Joins a scope prefix to a name the way a rule scope names its member rules.</summary>
    /// <param name="prefix">The enclosing prefix, or <see langword="null"/>/empty for none.</param>
    /// <param name="name">The name being prefixed.</param>
    /// <returns>The joined name.</returns>
    private static string PrefixedName(string? prefix, string name) => (string.IsNullOrEmpty(value: prefix)
        ? name
        : (string.IsNullOrEmpty(value: name)
            ? prefix
            : $"{prefix}_{name}")
    );
    /// <summary>Evaluates a group's authored pass ceiling at compile time.</summary>
    /// <param name="expression">The authored ceiling.</param>
    /// <param name="name">The group's name, for the refusal.</param>
    /// <param name="span">The group's span, for the refusal.</param>
    /// <param name="scope">The document scope.</param>
    /// <returns>The ceiling, or <c>0</c> when it was refused.</returns>
    private static int EvaluateGroupPasses(ExpressionNode expression, string name, SourceSpan span, DocumentScope scope) {
        var evaluated = DocumentLowering.LowerValue(
            expr: expression,
            scope: scope
        );
        var passes = 0;

        if ((evaluated is JsonValue value) && DocumentNumbers.TryInteger(
            node: value,
            number: out var parsed
        )) {
            passes = ((int)Math.Clamp(
                max: int.MaxValue,
                min: int.MinValue,
                value: parsed
            ));
        }

        if ((passes >= 1) && (passes <= RuleGroupCapacity.MaxPasses)) {
            return passes;
        }

        scope.Diagnostics.ReportError(
            code: PuckDiagnosticCodes.RuleGroupShapeInadmissible,
            message: $"stabilize group '{name}' declares maxPasses '{evaluated}', which must evaluate to an integer in 1..{RuleGroupCapacity.MaxPasses} at compile time.",
            span: span
        );

        return 0;
    }
}
