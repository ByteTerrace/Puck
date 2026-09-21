using Parlot.Fluent;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;

namespace Puck.Transpiler.Parsing;

public static partial class PuckParser {
    private static bool TryMatchStabilizeKeyword(ParseContext context) {
        var cursor = context.Scanner.Cursor;
        var saved = cursor.Position;

        if (TryMatchKeyword(context: context, keyword: "stabilize") && SkipSpacesOnLine(context: context)) {
            if (TryReadName(admitted: NameForms.Identifier | NameForms.String, context: context, spelling: out _, text: out _)) {
                cursor.ResetPosition(position: saved);
                return TryMatchKeyword(context: context, keyword: "stabilize");
            }
        }

        cursor.ResetPosition(position: saved);
        return false;
    }
    private static bool TryMatchWorkflowKeyword(ParseContext context) {
        var cursor = context.Scanner.Cursor;
        var saved = cursor.Position;

        if (TryMatchKeyword(context: context, keyword: "workflow") && SkipSpacesOnLine(context: context)) {
            if (TryReadName(admitted: NameForms.Identifier | NameForms.String, context: context, spelling: out _, text: out _)) {
                cursor.ResetPosition(position: saved);
                return TryMatchKeyword(context: context, keyword: "workflow");
            }
        }

        cursor.ResetPosition(position: saved);
        return false;
    }
    private static StabilizeGroupNode ParseStabilizeBlock(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics) {
        var cursor = context.Scanner.Cursor;

        SkipWhiteSpace(context: context);
        if (!TryReadName(admitted: NameForms.Identifier | NameForms.String, context: context, spelling: out _, text: out var name)) {
            throw CreateException(context: context, message: "Expected a group name after 'stabilize'");
        }

        ExpressionNode? maxPasses = null;
        ExpressionNode? undo = null;
        PredicateNode? untilCondition = null;

        SkipWhiteSpace(context: context);
        while (!cursor.Eof && (cursor.Current != '{')) {
            if (TryMatchKeyword(context: context, keyword: "maxPasses")) {
                SkipWhiteSpace(context: context);
                if (TryConsume(c: '(', context: context)) {
                    maxPasses = ParseExpression(context: context);
                    SkipWhiteSpace(context: context);
                    if (!TryConsume(c: ')', context: context)) {
                        throw CreateException(context: context, message: $"Expected ')' after maxPasses in stabilize '{name}'");
                    }
                } else if (TryConsume(c: ':', context: context) || TryConsume(c: '=', context: context)) {
                    maxPasses = ParseExpression(context: context);
                }
            } else if (TryMatchKeyword(context: context, keyword: "undo")) {
                if (undo is not null) {
                    throw CreateException(context: context, message: "An undo modifier may be declared only once");
                }
                undo = ParseUndoModifier(context: context);
            } else if (TryMatchKeyword(context: context, keyword: "until")) {
                SkipWhiteSpace(context: context);
                untilCondition = ParseGate(context: context, diagnostics: diagnostics);
            } else {
                break;
            }
            SkipWhiteSpace(context: context);
        }

        if (!TryConsume(c: '{', context: context)) {
            throw CreateException(context: context, message: $"Expected '{{' starting body for stabilize '{name}'");
        }

        var statements = new List<StatementNode>();

        SkipWhiteSpace(context: context);
        while (!cursor.Eof && (cursor.Current != '}')) {
            var stmt = ParseRuleScopeBodyStatement(context: context, diagnostics: diagnostics);

            if (stmt is not null) {
                statements.Add(item: stmt);
            }
            ConsumeSeparator(context: context);
            SkipWhiteSpace(context: context);
        }

        if (!TryConsume(c: '}', context: context)) {
            throw CreateException(context: context, message: $"Expected '}}' closing body for stabilize '{name}'");
        }

        var len = (cursor.Offset - startOffset);

        return new StabilizeGroupNode(
            Column: col,
            Length: len,
            Line: line,
            MaxPasses: maxPasses,
            Name: name,
            Offset: startOffset,
            Statements: statements,
            Undo: undo,
            UntilCondition: untilCondition
        );
    }
    private static WorkflowNode ParseWorkflowBlock(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics) {
        var cursor = context.Scanner.Cursor;

        SkipWhiteSpace(context: context);
        if (!TryReadName(admitted: NameForms.Identifier | NameForms.String, context: context, spelling: out _, text: out var name)) {
            throw CreateException(context: context, message: "Expected a workflow name after 'workflow'");
        }

        SkipWhiteSpace(context: context);
        ExpressionNode? undo = null;

        if (TryMatchKeyword(context: context, keyword: "undo")) {
            undo = ParseUndoModifier(context: context);
            SkipWhiteSpace(context: context);
        }
        if (!TryConsume(c: '{', context: context)) {
            throw CreateException(context: context, message: $"Expected '{{' starting body for workflow '{name}'");
        }

        var steps = new List<WorkflowStepNode>();

        SkipWhiteSpace(context: context);
        while (!cursor.Eof && (cursor.Current != '}')) {
            var step = ParseWorkflowStep(context: context, diagnostics: diagnostics);

            if (step is not null) {
                steps.Add(item: step);
            }
            ConsumeSeparator(context: context);
            SkipWhiteSpace(context: context);
        }

        if (!TryConsume(c: '}', context: context)) {
            throw CreateException(context: context, message: $"Expected '}}' closing body for workflow '{name}'");
        }

        var len = (cursor.Offset - startOffset);

        return new WorkflowNode(
            Column: col,
            Length: len,
            Line: line,
            Name: name,
            Offset: startOffset,
            Steps: steps,
            Undo: undo
        );
    }
    private static ExpressionNode ParseUndoModifier(ParseContext context) {
        SkipWhiteSpace(context: context);
        if (!TryConsume(c: '(', context: context)) {
            throw CreateException(context: context, message: "Expected '(' after undo");
        }
        var undo = ParseExpression(context: context);

        SkipWhiteSpace(context: context);
        if (!TryConsume(c: ')', context: context)) {
            throw CreateException(context: context, message: "Expected ')' after undo declaration");
        }
        return undo;
    }
    private static WorkflowStepNode? ParseWorkflowStep(ParseContext context, DiagnosticBag? diagnostics) {
        var cursor = context.Scanner.Cursor;

        SkipWhiteSpace(context: context);
        var startOffset = cursor.Offset;

        var (line, col) = GetLineAndColumn(buffer: context.Scanner.Buffer, offset: startOffset);

        if (TryMatchKeyword(context: context, keyword: "repeatStep")) {
            SkipWhiteSpace(context: context);
            if (!TryReadName(admitted: NameForms.Identifier | NameForms.String, context: context, spelling: out _, text: out var stepName)) {
                throw CreateException(context: context, message: "Expected a step name after 'repeatStep'");
            }

            PredicateNode? untilCond = null;

            SkipWhiteSpace(context: context);
            if (TryMatchKeyword(context: context, keyword: "until")) {
                SkipWhiteSpace(context: context);
                untilCond = ParseGate(context: context, diagnostics: diagnostics);
            }

            var stmts = ParseStepBody(context: context, diagnostics: diagnostics, stepName: stepName);
            var len = (cursor.Offset - startOffset);

            return new WorkflowStepNode(
                Column: col,
                ForEachCollection: null,
                ForEachVariable: null,
                Kind: WorkflowStepKind.Repeat,
                Length: len,
                Line: line,
                Name: stepName,
                Offset: startOffset,
                Statements: stmts,
                UntilCondition: untilCond
            );
        }

        if (TryMatchKeyword(context: context, keyword: "forEachStep")) {
            SkipWhiteSpace(context: context);
            if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var forEachVar)) {
                throw CreateException(context: context, message: "Expected a variable name after 'forEachStep'");
            }

            SkipWhiteSpace(context: context);
            if (!TryMatchKeyword(context: context, keyword: "in")) {
                throw CreateException(context: context, message: $"Expected 'in' after 'forEachStep {forEachVar}'");
            }

            SkipWhiteSpace(context: context);
            var collection = ParseExpression(context: context);
            var stepName = forEachVar;

            var stmts = ParseStepBody(context: context, diagnostics: diagnostics, stepName: stepName);
            var len = (cursor.Offset - startOffset);

            return new WorkflowStepNode(
                Column: col,
                ForEachCollection: collection,
                ForEachVariable: forEachVar,
                Kind: WorkflowStepKind.ForEach,
                Length: len,
                Line: line,
                Name: stepName,
                Offset: startOffset,
                Statements: stmts,
                UntilCondition: null
            );
        }

        if (TryMatchKeyword(context: context, keyword: "step")) {
            SkipWhiteSpace(context: context);
            if (!TryReadName(admitted: NameForms.Identifier | NameForms.String, context: context, spelling: out _, text: out var stepName)) {
                throw CreateException(context: context, message: "Expected a step name after 'step'");
            }

            SkipWhiteSpace(context: context);

            var skip = TryMatchKeyword(context: context, keyword: "skip");
            var stmts = ParseStepBody(context: context, diagnostics: diagnostics, stepName: stepName);
            var len = (cursor.Offset - startOffset);

            return new WorkflowStepNode(
                Column: col,
                ForEachCollection: null,
                ForEachVariable: null,
                Kind: WorkflowStepKind.Linear,
                Length: len,
                Line: line,
                Name: stepName,
                Offset: startOffset,
                Skip: skip,
                Statements: stmts,
                UntilCondition: null
            );
        }

        throw CreateException(context: context, message: $"Expected 'step', 'repeatStep', or 'forEachStep' inside workflow at offset {startOffset}");
    }
    private static List<StatementNode> ParseStepBody(ParseContext context, DiagnosticBag? diagnostics, string stepName) {
        var cursor = context.Scanner.Cursor;

        SkipWhiteSpace(context: context);
        if (!TryConsume(c: '{', context: context)) {
            throw CreateException(context: context, message: $"Expected '{{' starting body for step '{stepName}'");
        }

        var statements = new List<StatementNode>();

        SkipWhiteSpace(context: context);
        while (!cursor.Eof && (cursor.Current != '}')) {
            var stmt = ParseRuleScopeBodyStatement(context: context, diagnostics: diagnostics);

            if (stmt is not null) {
                statements.Add(item: stmt);
            }
            ConsumeSeparator(context: context);
            SkipWhiteSpace(context: context);
        }

        if (!TryConsume(c: '}', context: context)) {
            throw CreateException(context: context, message: $"Expected '}}' closing body for step '{stepName}'");
        }

        return statements;
    }
}
