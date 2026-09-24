using System.Text.Json.Nodes;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Lowering;

namespace Puck.World.Transpiler.Lowering;

public static partial class WorldDocumentEmitter {
    internal sealed record RuleScopeContext(
        string Prefix,
        JsonObject? Gate,
        IReadOnlyList<JsonObject>? Locals,
        IReadOnlyDictionary<string, JsonNode?>? Properties
    );

    private static void LowerRuleScope(
        JsonObject parent,
        RuleScopeNode scopeNode,
        DocumentScope scope,
        RuleScopeContext? parentContext = null
    ) {
        RefuseReservedScopedName(
            name: scopeNode.Name,
            prefix: parentContext?.Prefix,
            scope: scope,
            span: scopeNode.Span
        );

        var prefix = PrefixedName(
            name: scopeNode.Name,
            prefix: parentContext?.Prefix,
            scope: scope
        );

        var combinedGate = (parentContext?.Gate?.DeepClone() as JsonObject);

        if (scopeNode.HeaderWhen is not null) {
            var headerGate = LowerPredicate(node: scopeNode.HeaderWhen.Predicate, scope: scope);

            combinedGate = CombineGates(existing: combinedGate, newGate: headerGate);
        }

        var localsList = new List<JsonObject>();

        if (parentContext?.Locals is { Count: > 0 } parentLocals) {
            foreach (var l in parentLocals) {
                localsList.Add(item: ((JsonObject)l.DeepClone()));
            }
        }

        var props = new Dictionary<string, JsonNode?>(comparer: StringComparer.Ordinal);

        if (parentContext?.Properties is { Count: > 0 } parentProps) {
            foreach (var (k, v) in parentProps) {
                props[k] = v?.DeepClone();
            }
        }

        // Process non-rule body statements first (when, local, properties)
        foreach (var stmt in scopeNode.Statements) {
            switch (stmt) {
                case WhenStatementNode whenStmt:
                    var bodyGate = LowerPredicate(node: whenStmt.Predicate, scope: scope);
                    combinedGate = CombineGates(existing: combinedGate, newGate: bodyGate);
                    break;

                case LocalStatementNode localStmt:
                    localsList.Add(item: LowerLocal(local: localStmt, scope: scope));
                    break;

                case PropertyNode propNode:
                    var loweredProp = LowerExpression(expr: propNode.Value, scope: scope, fieldKey: propNode.Name);
                    props[propNode.Name] = loweredProp;
                    break;
            }
        }

        var currentContext = new RuleScopeContext(
            Locals: ((localsList.Count > 0) ? localsList : null),
            Gate: combinedGate,
            Prefix: prefix,
            Properties: ((props.Count > 0) ? props : null)
        );

        // Process child rules and nested scopes
        foreach (var stmt in scopeNode.Statements) {
            switch (stmt) {
                case RuleBlockNode childRule:
                    LowerRuleBlock(
                        parent: parent,
                        rule: childRule,
                        scope: scope,
                        scopeContext: currentContext
                    );
                    break;

                case RuleScopeNode nestedScope:
                    LowerRuleScope(
                        parent: parent,
                        parentContext: currentContext,
                        scope: scope,
                        scopeNode: nestedScope
                    );
                    break;

                case StabilizeGroupNode stabilizeGroup:
                    LowerStabilizeGroup(
                        parent: parent,
                        parentContext: currentContext,
                        scope: scope,
                        stabilizeNode: stabilizeGroup
                    );
                    break;

                case WorkflowNode workflow:
                    LowerWorkflow(
                        parent: parent,
                        parentContext: currentContext,
                        scope: scope,
                        workflowNode: workflow
                    );
                    break;
            }
        }
    }

    // Conjunction is associative, so both sides are spliced flat: a scope gate over a member's own `and` chain is
    // `all[scope, A, B]`, never `all[scope, all[A, B]]`. Nesting would price the same work as two gates.
    internal static JsonObject? CombineGates(JsonObject? existing, JsonObject? newGate) {
        if (existing is null) {
            return newGate;
        }
        if (newGate is null) {
            return existing;
        }

        if ((existing["$type"]?.ToString() == "all") && (existing["predicates"] is JsonArray arr)) {
            AppendConjuncts(gate: newGate, into: arr);
            return existing;
        }

        var newAll = new JsonArray();

        AppendConjuncts(gate: existing, into: newAll);
        AppendConjuncts(gate: newGate, into: newAll);
        return new JsonObject {
            ["$type"] = "all",
            ["predicates"] = newAll,
        };
    }

    private static void AppendConjuncts(JsonArray into, JsonObject gate) {
        if ((gate["$type"]?.ToString() == "all") && (gate["predicates"] is JsonArray conjuncts)) {
            foreach (var conjunct in conjuncts) {
                into.AppendNode(item: conjunct?.DeepClone());
            }

            return;
        }

        into.AppendNode(item: gate.DeepClone());
    }
}
