using System.Text.Json.Nodes;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;

namespace Puck.World.Transpiler.Lowering;

public static partial class WorldDocumentEmitter {
    /// <summary>Lowers a <c>workflow</c> block into a staged entry of the document's <c>ruleGroups</c> array and its
    /// steps into <c>rules</c>.</summary>
    /// <remarks>Each <c>step</c>'s body is a rule body, lowered as one rule named
    /// <c>&lt;workflow&gt;_&lt;step&gt;</c>; the steps are the cursor's order and the last one is terminal. A step
    /// declaring <c>skip</c> advances the cursor past its own refusal instead of stalling on it.
    /// <c>repeatStep</c> and <c>forEachStep</c> are refused: a staged cursor advances on a committed firing and
    /// carries neither a repeat nor an iteration of its own.</remarks>
    private static void LowerWorkflow(
        JsonObject parent,
        WorkflowNode workflowNode,
        DocumentScope scope,
        RuleScopeContext? parentContext = null
    ) {
        var groupName = PrefixedName(
            name: workflowNode.Name,
            prefix: parentContext?.Prefix
        );

        if (string.IsNullOrEmpty(value: groupName)) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.RuleGroupShapeInadmissible,
                message: "a workflow needs a name; the workflow's name is what its cursor progress is checkpointed under.",
                span: workflowNode.Span
            );

            return;
        }

        var context = LowerGroupScope(
            headerWhen: null,
            parentContext: parentContext,
            prefix: groupName,
            scope: scope,
            statements: []
        );
        var named = new HashSet<string>(comparer: StringComparer.Ordinal);
        var refused = false;
        var steps = new JsonArray();

        foreach (var step in workflowNode.Steps) {
            if (step.Kind != WorkflowStepKind.Linear) {
                refused = true;
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.RuleGroupShapeInadmissible,
                    message: $"workflow '{groupName}' declares '{step.Name}' as a {((step.Kind == WorkflowStepKind.Repeat) ? "repeatStep" : "forEachStep")}; a staged cursor advances one step per committed firing and carries no repeat or iteration of its own. Write a `step` whose gate closes when the work is done.",
                    span: step.Span
                );

                continue;
            }
            if (step.Statements.Any(predicate: static statement => (statement is RuleBlockNode))) {
                refused = true;
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.RuleGroupShapeInadmissible,
                    message: $"workflow '{groupName}' step '{step.Name}' holds a `rule` block; a step's body IS one rule's body, so write its `when` and effect statements directly.",
                    span: step.Span
                );

                continue;
            }

            var body = new RuleBlockNode(
                Column: step.Column,
                Length: step.Length,
                Line: step.Line,
                Name: step.Name,
                Offset: step.Offset,
                Statements: step.Statements
            );
            // The step's name is whatever the rule lowering resolves its body's to, so the step and the rule it
            // names can never disagree.
            var stepName = (DocumentLowering.ResolveRuleName(
                rule: body,
                scope: scope
            ) ?? step.Name);

            if (!named.Add(item: stepName)) {
                refused = true;
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.RuleGroupShapeInadmissible,
                    message: $"workflow '{groupName}' declares the step '{stepName}' twice; a cursor addresses its steps by name, so each one is distinct.",
                    span: step.Span
                );

                continue;
            }

            LowerRuleBlock(
                parent: parent,
                rule: body,
                scope: scope,
                scopeContext: context
            );

            var lowered = new JsonObject {
                ["rule"] = JsonValue.Create(value: PrefixedName(
                    name: stepName,
                    prefix: groupName
                )),
            };

            if (step.Skip) {
                lowered["onRefusal"] = JsonValue.Create(value: "Skip");
            }

            steps.AppendNode(item: lowered);
        }

        if (steps.Count == 0) {
            if (refused) {
                return;
            }

            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.RuleGroupShapeInadmissible,
                message: $"workflow '{groupName}' declares no step; write at least one `step`, or drop the workflow.",
                span: workflowNode.Span
            );

            return;
        }

        AppendRuleGroup(
            group: new JsonObject {
                ["name"] = JsonValue.Create(value: groupName),
                ["shape"] = JsonValue.Create(value: "Staged"),
                ["steps"] = steps,
            },
            parent: parent,
            scope: scope,
            span: workflowNode.Span
        );
    }
}
