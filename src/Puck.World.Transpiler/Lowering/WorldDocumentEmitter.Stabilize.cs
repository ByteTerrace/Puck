using System.Text.Json.Nodes;
using Puck.State;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;
using RuleGroupCapacity = Puck.State.Rules.RuleGroupCapacity;

namespace Puck.World.Transpiler.Lowering;

public static partial class WorldDocumentEmitter {
    /// <summary>Lowers a <c>stabilize</c> group into a fixpoint entry of the document's <c>ruleGroups</c> array and
    /// its member rules into <c>rules</c>.</summary>
    /// <remarks>The body is a rule scope: its <c>when</c>, <c>local</c> and property statements apply to every member,
    /// and each <c>rule</c> block becomes one member named <c>&lt;group&gt;$&lt;rule&gt;</c>. <c>maxPasses(N)</c> is
    /// the pass ceiling whose breach is a counted refusal; <c>until</c> arms the group while its gate reads false, so
    /// it lowers to the gate's negation.</remarks>
    private static void LowerStabilizeGroup(
        JsonObject parent,
        StabilizeGroupNode stabilizeNode,
        DocumentScope scope,
        RuleScopeContext? parentContext = null
    ) {
        RefuseReservedScopedName(
            name: stabilizeNode.Name,
            prefix: parentContext?.Prefix,
            scope: scope,
            span: stabilizeNode.Span
        );

        var groupName = PrefixedName(
            name: stabilizeNode.Name,
            prefix: parentContext?.Prefix,
            scope: scope
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

        foreach (var (member, memberScope) in GroupMembers(scope: scope, statements: stabilizeNode.Statements)) {
            // A member's name is whatever the rule lowering resolves it to, so an interpolated name reaches the step
            // rather than the empty string the header wrote.
            var memberName = (DocumentLowering.ResolveRuleName(
                rule: member,
                scope: memberScope
            ) ?? member.Name);

            LowerRuleBlock(
                parent: parent,
                rule: member,
                scope: memberScope,
                scopeContext: context
            );
            steps.AppendNode(item: new JsonObject {
                ["rule"] = JsonValue.Create(value: PrefixedName(
                    name: memberName,
                    prefix: groupName,
                    scope: scope
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

        if (stabilizeNode.Undo is { } undo) {
            group["undo"] = LowerExpression(expr: undo, fieldKey: "undo", scope: scope);
        }

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
    // A group's members, in source order, each with the scope it lowers under: a `rule` block as written, and the
    // rules a compile-time `for` or a template invocation in the body stamps, which lower under the loop's or the
    // template's bindings exactly as the same statements would at a document's top level.
    private static IEnumerable<(RuleBlockNode Member, DocumentScope Scope)> GroupMembers(IReadOnlyList<StatementNode> statements, DocumentScope scope) {
        foreach (var statement in statements) {
            switch (statement) {
                case RuleBlockNode member:
                    yield return (member, scope);
                    break;
                case ForStatementNode loop:
                    foreach (var (expanded, iteration) in DocumentLowering.ExpandForStatements(loop: loop, scope: scope)) {
                        foreach (var nested in GroupMembers(scope: iteration, statements: [expanded])) {
                            yield return nested;
                        }
                    }
                    break;
                case ExpressionStatementNode { IsUse: false, Expression: CallExpressionNode call } when (scope.Templates.TryGetValue(key: call.Name, value: out var template) && !template.IsModule): {
                        var stamped = new List<(StatementNode Statement, DocumentScope Scope)>();

                        DocumentLowering.ExpandTemplate(
                            call: call,
                            scope: scope,
                            sink: (body, _, invocation) => stamped.Add(item: (body, invocation)),
                            target: new JsonObject()
                        );
                        foreach (var (body, invocation) in stamped) {
                            foreach (var nested in GroupMembers(scope: invocation, statements: [body])) {
                                yield return nested;
                            }
                        }
                        break;
                    }
            }
        }
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
                    locals.Add(item: LowerLocal(local: local, scope: scope));
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
    /// <summary>Joins a scope prefix to a name the way a rule scope names what it holds: the generated
    /// <c>prefix$name</c> (<see cref="GeneratedName.Append"/>), which no author-written rule or group can spell.</summary>
    /// <param name="prefix">The enclosing prefix, or <see langword="null"/>/empty for none.</param>
    /// <param name="name">The name being prefixed. One carrying <c>$</c> is refused where it was written
    /// (<see cref="RefuseReservedScopedName"/>) and is returned unjoined.</param>
    /// <param name="scope">The document scope, which records the joined name as one this compilation generated.</param>
    /// <returns>The joined name.</returns>
    private static string PrefixedName(string? prefix, string name, DocumentScope scope) => ((string.IsNullOrEmpty(value: prefix) || name.Contains(value: GeneratedName.Joiner))
        ? name
        : (string.IsNullOrEmpty(value: name)
            ? prefix
            : Generated(
                name: GeneratedName.Append(
                name: prefix,
                part: name
            ),
                scope: scope
            ))
    );
    /// <summary>Reports PUCK113 for a rule, scope or group name the author wrote in the spelling Puck reserves for
    /// the names it generates: a <c>$</c> inside it, or, beneath a scope, a <c>$</c> anywhere, since the scope joins
    /// its own name to it with one. A name refused here is refused once: the document-wide check that follows
    /// lowering passes over it.</summary>
    /// <param name="name">The name as written.</param>
    /// <param name="prefix">The enclosing scope's prefix, or <see langword="null"/>/empty at the top level.</param>
    /// <param name="scope">The document scope.</param>
    /// <param name="span">The span that wrote the name.</param>
    private static void RefuseReservedScopedName(string name, string? prefix, DocumentScope scope, SourceSpan span) {
        if (GeneratedName.TryValidateAuthored(
            name: name,
            reason: out var reason
        )) {
            if (string.IsNullOrEmpty(value: prefix) || !name.Contains(value: GeneratedName.Joiner)) {
                return;
            }

            reason = $"'{name}' carries '{GeneratedName.Joiner}', which the enclosing scope '{prefix}' would join its own name to it with; write a name without '{GeneratedName.Joiner}'";
        }

        scope.Diagnostics.ReportError(
            code: PuckDiagnosticCodes.GeneratedNameReserved,
            message: reason,
            span: span
        );
        _ = GetOrCreateGeneratedNames(scope: scope).Add(item: name);
    }
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
