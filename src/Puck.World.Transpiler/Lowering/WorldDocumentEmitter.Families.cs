using System.Text.Json.Nodes;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.State;
using Puck.Transpiler.Lowering;

namespace Puck.World.Transpiler.Lowering;

public static partial class WorldDocumentEmitter {
    // Indices are the family indices the members carry -- what `<family>[i]` selects on. Every family reaches the
    // document's `state.families` member and keeps its `<family>[i]` spelling.
    internal sealed record StateFamilyInfo(
        string Name,
        int Size,
        IReadOnlyList<string> MemberNames,
        IReadOnlyList<int> Indices
    );

    internal static Dictionary<string, T> GetOrCreateScopeDictionary<T>(DocumentScope scope, string key) {
        if (!scope.Annotations.TryGetValue(key: key, value: out var obj) || (obj is not Dictionary<string, T> dict)) {
            dict = new Dictionary<string, T>(comparer: StringComparer.Ordinal);
            scope.Annotations[key] = dict;
        }

        return dict;
    }
    internal static Dictionary<string, StateFamilyInfo> GetOrCreateStateFamilies(DocumentScope scope) =>
        GetOrCreateScopeDictionary<StateFamilyInfo>(key: "StateFamilies", scope: scope);
    internal static void IndexStateFamilies(IReadOnlyList<StatementNode> statements, DocumentScope scope) {
        var families = GetOrCreateStateFamilies(scope: scope);

        foreach (var stmt in statements) {
            switch (stmt) {
                case StateTableDeclarationNode table when IsFamily(members: table.FamilyMembers, size: table.FamilySize):
                    RegisterFamily(members: table.FamilyMembers, name: table.Name, sizeExpr: table.FamilySize, span: table.Span, families: families, scope: scope);
                    break;

                case StateSlotDeclarationNode slot when IsFamily(members: slot.FamilyMembers, size: slot.FamilySize):
                    RegisterFamily(members: slot.FamilyMembers, name: slot.Name, sizeExpr: slot.FamilySize, span: slot.Span, families: families, scope: scope);
                    break;

                case StatePileDeclarationNode pile when IsFamily(members: pile.FamilyMembers, size: pile.FamilySize):
                    RegisterFamily(members: pile.FamilyMembers, name: pile.Name, sizeExpr: pile.FamilySize, span: pile.Span, families: families, scope: scope);
                    break;

                case StateGridDeclarationNode grid when IsFamily(members: grid.FamilyMembers, size: grid.FamilySize):
                    RegisterFamily(members: grid.FamilyMembers, name: grid.Name, sizeExpr: grid.FamilySize, span: grid.Span, families: families, scope: scope);
                    break;

                case BlockNode block:
                    IndexStateFamilies(statements: block.Statements, scope: scope);
                    break;
            }
        }
    }

    private static bool IsFamily(ExpressionNode? size, IReadOnlyList<FamilyMemberNode>? members) => ((size is not null) || (members is { Count: > 0 }));
    private static void RegisterFamily(string name, ExpressionNode? sizeExpr, IReadOnlyList<FamilyMemberNode>? members, SourceSpan span, Dictionary<string, StateFamilyInfo> families, DocumentScope scope) {
        if (members is { Count: > 0 }) {
            if (ExpandFamilyMembers(items: members, name: name, scope: scope, span: span) is { } declared) {
                families[name] = declared;
            }

            return;
        }

        var size = EvaluateFamilySize(name: name, scope: scope, sizeExpr: sizeExpr, span: span);

        if (size <= 0) {
            return;
        }

        var memberNames = new string[size];
        var indices = new int[size];

        for (var i = 0; (i < size); i++) {
            indices[i] = i;
            memberNames[i] = $"{name}{i}";
        }

        families[name] = new StateFamilyInfo(
            Indices: indices,
            MemberNames: memberNames,
            Name: name,
            Size: size
        );
    }
    // A member list is either index ranges (a gap is an index nothing names) or member rows named outright, and the
    // two may not be mixed: a named row carries no family index to select it by.
    private static StateFamilyInfo? ExpandFamilyMembers(IReadOnlyList<FamilyMemberNode> items, string name, SourceSpan span, DocumentScope scope) {
        var indices = new List<int>();
        var memberNames = new List<string>();
        var seen = new HashSet<int>();
        var named = 0;

        void Refuse(string detail) => scope.Diagnostics.ReportError(
            code: PuckDiagnosticCodes.FamilyMembersInvalid,
            message: $"Family '{name}' {detail}",
            span: span
        );

        foreach (var item in items) {
            if (item.Row is { } row) {
                named++;
                indices.Add(item: memberNames.Count);
                memberNames.Add(item: row);

                continue;
            }
            if (item.First is not { } firstExpr) {
                Refuse(detail: "declares an empty member item");

                return null;
            }

            if (EvaluateFamilyIndex(expression: firstExpr, name: name, scope: scope, span: span) is not { } first) {
                return null;
            }

            var last = first;

            if (item.Last is { } lastExpr) {
                if (EvaluateFamilyIndex(expression: lastExpr, name: name, scope: scope, span: span) is not { } evaluated) {
                    return null;
                }

                last = evaluated;
            }
            if (last < first) {
                Refuse(detail: $"declares the member range {first}..{last}, which must be least first");

                return null;
            }

            // Both ends lie inside the row ceiling, so the length fits and the walk is by count: an inclusive walk
            // to the last index has no end when that index is the widest the counter holds.
            var length = ((last - first) + 1);

            if ((memberNames.Count + length) > StateCapacity.MaxRows) {
                Refuse(detail: $"declares more than the {StateCapacity.MaxRows} rows a document holds");

                return null;
            }

            for (var offset = 0; (offset < length); offset++) {
                var index = (first + offset);

                if (!seen.Add(item: index)) {
                    Refuse(detail: $"declares member index {index} twice");

                    return null;
                }

                indices.Add(item: index);
                memberNames.Add(item: $"{name}{index}");
            }
        }

        if ((named > 0) && (named != memberNames.Count)) {
            Refuse(detail: "mixes named member rows with index ranges; a named row carries no family index to select it by");

            return null;
        }
        if (memberNames.Count == 0) {
            Refuse(detail: "declares no members");

            return null;
        }

        return new StateFamilyInfo(
            Indices: indices,
            MemberNames: memberNames,
            Name: name,
            Size: memberNames.Count
        );
    }
    // A family index selects a row, so it lies inside the row ceiling; an index outside it is refused as written
    // rather than brought inside.
    private static int? EvaluateFamilyIndex(ExpressionNode expression, string name, SourceSpan span, DocumentScope scope) {
        var evaluated = DocumentLowering.LowerValue(expr: expression, scope: scope);

        if (
            (evaluated is JsonValue value) &&
            DocumentNumbers.TryInteger(node: value, number: out var parsed) &&
            (parsed >= 0L) &&
            (parsed < StateCapacity.MaxRows)
        ) {
            return ((int)parsed);
        }

        scope.Diagnostics.ReportError(
            code: PuckDiagnosticCodes.FamilyMembersInvalid,
            message: $"Family '{name}' declares the member index '{evaluated}', which must evaluate at compile time to an integer in 0..{(StateCapacity.MaxRows - 1)}",
            span: span
        );

        return null;
    }

    internal static void EmitStateFamilies(JsonObject root, DocumentScope scope) {
        var families = GetOrCreateStateFamilies(scope: scope);
        var ordered = families.Values
            .OrderBy(keySelector: static family => family.Name, comparer: StringComparer.Ordinal)
            .ToArray();

        if (ordered.Length == 0) {
            return;
        }

        if (root["state"] is not JsonObject state) {
            state = [];
            root["state"] = state;
        }
        if (state["families"] is not JsonArray rows) {
            rows = [];
            state["families"] = rows;
        }

        foreach (var family in ordered) {
            var row = new JsonObject {
                ["name"] = JsonValue.Create(value: family.Name),
                ["size"] = JsonValue.Create(value: family.Size),
            };
            var contiguous = true;
            var conventional = true;

            for (var index = 0; (index < family.Size); index++) {
                contiguous &= (family.Indices[index] == index);
                conventional &= (family.MemberNames[index] == $"{family.Name}{family.Indices[index]}");
            }

            if (!contiguous) {
                var indices = new JsonArray();

                foreach (var index in family.Indices) {
                    indices.AppendNode(item: JsonValue.Create(value: index));
                }

                row["indices"] = indices;
            }
            if (!conventional) {
                var members = new JsonArray();

                foreach (var member in family.MemberNames) {
                    members.AppendNode(item: JsonValue.Create(value: member));
                }

                row["members"] = members;
            }

            rows.AppendNode(item: row);
        }
    }

    private static int EvaluateFamilySize(ExpressionNode? sizeExpr, string name, SourceSpan span, DocumentScope scope) {
        if (sizeExpr is null) {
            return 1;
        }

        var evaluated = DocumentLowering.LowerValue(expr: sizeExpr, scope: scope);

        // The size is read exactly and bounded before it is narrowed: the members of a family are rows, and a size
        // past the row ceiling is refused here rather than expanded for the validator to count.
        if (
            (evaluated is JsonValue value) &&
            DocumentNumbers.TryInteger(node: value, number: out var parsed) &&
            (parsed >= 1L) &&
            (parsed <= StateCapacity.MaxRows)
        ) {
            return ((int)parsed);
        }

        scope.Diagnostics.ReportError(
            code: PuckDiagnosticCodes.FamilySizeInvalid,
            message: $"Family size for '{name}' must evaluate at compile time to an integer in 1..{StateCapacity.MaxRows}, got '{evaluated}'",
            span: span
        );

        return 0;
    }
    private static IEnumerable<TNode> ExpandFamilyMembers<TNode>(
        string name,
        ExpressionNode? size,
        SourceSpan span,
        DocumentScope scope,
        Func<string, TNode> factory
    ) {
        foreach (var member in FamilyMemberNames(name: name, scope: scope, size: size, span: span)) {
            yield return factory(member);
        }
    }

    internal static IEnumerable<StateTableDeclarationNode> ExpandTableFamily(StateTableDeclarationNode table, DocumentScope scope) =>
        ExpandFamilyMembers(name: table.Name, size: table.FamilySize, span: table.Span, scope: scope, member => table with { FamilyMembers = null, FamilySize = null, Name = member });
    internal static IEnumerable<StateSlotDeclarationNode> ExpandSlotFamily(StateSlotDeclarationNode slot, DocumentScope scope) =>
        ExpandFamilyMembers(name: slot.Name, size: slot.FamilySize, span: slot.Span, scope: scope, member => slot with { FamilyMembers = null, FamilySize = null, Name = member });
    internal static IEnumerable<StatePileDeclarationNode> ExpandPileFamily(StatePileDeclarationNode pile, DocumentScope scope) =>
        ExpandFamilyMembers(name: pile.Name, size: pile.FamilySize, span: pile.Span, scope: scope, member => pile with { FamilyMembers = null, FamilySize = null, Name = member });
    internal static IEnumerable<StateGridDeclarationNode> ExpandGridFamily(StateGridDeclarationNode grid, DocumentScope scope) =>
        ExpandFamilyMembers(name: grid.Name, size: grid.FamilySize, span: grid.Span, scope: scope, member => grid with { FamilyMembers = null, FamilySize = null, Name = member });

    // The registered family is the one source of member names, so a declaration and every reference to it agree on
    // the gaps.
    private static IReadOnlyList<string> FamilyMemberNames(string name, ExpressionNode? size, SourceSpan span, DocumentScope scope) {
        if (GetOrCreateStateFamilies(scope: scope).TryGetValue(key: name, value: out var family)) {
            return family.MemberNames;
        }

        var count = EvaluateFamilySize(name: name, scope: scope, sizeExpr: size, span: span);

        if (count <= 0) {
            return [name];
        }

        var members = new string[count];

        for (var index = 0; (index < count); index++) {
            members[index] = $"{name}{index}";
        }

        return members;
    }

    // A row position a rule resolves live rather than by name: `$zones[i]`, or `<family>[i]`. The head is the whole
    // bracketed spelling and the remainder, if any, is the cell key inside the selected row.
    internal static bool TryLiveRowHead(string text, DocumentScope scope, out string head, out string? key) {
        head = text;
        key = null;

        var bracket = text.IndexOf(value: '[');

        if (bracket < 0) {
            return false;
        }

        var name = text[..bracket];

        if (!text.StartsWith(comparisonType: StringComparison.Ordinal, value: RuleFacts.LiveZonePrefix)) {
            if (!GetOrCreateStateFamilies(scope: scope).ContainsKey(key: name)) {
                return false;
            }
        }

        var close = text.IndexOf(value: ']');

        if (close < 0) {
            return false;
        }

        head = text[..(close + 1)];

        if ((close + 1) >= text.Length) {
            return true;
        }
        if (text[(close + 1)] != '[') {
            return false;
        }

        var last = text.LastIndexOf(value: ']');

        key = ((last > (close + 1)) ? text[(close + 2)..last] : text[(close + 2)..]);

        return true;
    }

    // Row references already carry their row/key split. Only a declared family needs source binding here;
    // ordinary references take no parser or builder allocation.
    // A refusal the binding draws (an index at a family gap) is reported at the span the reference was written at.
    private static string BindRowReference(string text, SourceSpan span, DocumentScope scope) {
        if (scope.TryQualify(qualified: out var instanceRow, reference: text)) {
            return instanceRow;
        }
        if ((GetOrCreateStateFamilies(scope: scope).Count == 0) || !text.Contains(value: '[')) {
            return text;
        }
        var operand = Puck.Transpiler.Parsing.PuckParser.CreateOperand(
            column: span.Column,
            form: DocumentValueForm.Name,
            length: span.Length,
            line: span.Line,
            offset: span.Offset,
            text: text
        );

        return DocumentReference(text: BindOperand(operand: operand, scope: scope));
    }
}
