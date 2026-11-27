using System.Text.Json.Nodes;
using Puck.Assets.Documents;
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
    // Families written where the scope already knows their names are indexed before anything lowers, so a reference
    // written above its declaration resolves. A family a loop or a computed name declares is registered when its
    // section expands it (`ExpandFamily`), before any of its members lower.
    internal static void IndexStateFamilies(IReadOnlyList<StatementNode> statements, DocumentScope scope) {
        var families = GetOrCreateStateFamilies(scope: scope);

        foreach (var stmt in statements) {
            if (FamilyOf(statement: stmt) is { NameExpression: null } family) {
                RegisterFamily(members: family.Members, name: family.Name, sizeExpr: family.Size, span: stmt.Span, families: families, scope: scope);
            } else if (stmt is BlockNode block) {
                IndexStateFamilies(statements: block.Statements, scope: scope);
            }
        }
    }

    // What a row declaration says about the family it declares, or null for a single row.
    private sealed record FamilyDeclaration(string Name, ExpressionNode? NameExpression, ExpressionNode? Size, IReadOnlyList<FamilyMemberNode>? Members, string? MemberIndex);

    private static FamilyDeclaration? FamilyOf(StatementNode statement) {
        var family = (statement switch {
            StateTableDeclarationNode table => new FamilyDeclaration(MemberIndex: table.MemberIndex, Members: table.FamilyMembers, Name: table.Name, NameExpression: table.NameExpression, Size: table.FamilySize),
            StateSlotDeclarationNode slot => new FamilyDeclaration(MemberIndex: slot.MemberIndex, Members: slot.FamilyMembers, Name: slot.Name, NameExpression: slot.NameExpression, Size: slot.FamilySize),
            StatePileDeclarationNode pile => new FamilyDeclaration(MemberIndex: pile.MemberIndex, Members: pile.FamilyMembers, Name: pile.Name, NameExpression: pile.NameExpression, Size: pile.FamilySize),
            StateGridDeclarationNode grid => new FamilyDeclaration(MemberIndex: grid.MemberIndex, Members: grid.FamilyMembers, Name: grid.Name, NameExpression: grid.NameExpression, Size: grid.FamilySize),
            _ => null,
        });

        return (((family is not null) && IsFamily(members: family.Members, size: family.Size)) ? family : null);
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
    // A family declaration as the single-row declarations of its members, each named for its member and paired with the
    // scope it lowers in: the declaration's own, with `as`'s binder (when written) bound to the member's family index,
    // so a member's modifiers, cells and value may differ by member. The family registers here when nothing indexed it
    // before, so its member names, its `state.families` entry and every `<family>[i]` reference agree.
    internal static IEnumerable<(StatementNode Statement, DocumentScope Scope)> ExpandFamily(StatementNode declaration, DocumentScope scope) {
        if (FamilyOf(statement: declaration) is not { } family) {
            yield return (declaration, scope);

            yield break;
        }

        var families = GetOrCreateStateFamilies(scope: scope);

        if (!families.ContainsKey(key: family.Name)) {
            RegisterFamily(families: families, members: family.Members, name: family.Name, scope: scope, sizeExpr: family.Size, span: declaration.Span);
        }
        if (!families.TryGetValue(key: family.Name, value: out var registered)) {
            yield break;
        }

        for (var member = 0; (member < registered.Size); member++) {
            var memberScope = scope;

            if (family.MemberIndex is { } binder) {
                var locals = new Dictionary<string, JsonNode?>(dictionary: scope.Locals, comparer: StringComparer.Ordinal) {
                    [binder] = JsonValue.Create(value: ((long)registered.Indices[member])),
                };

                memberScope = scope.WithLocals(lambdaLocals: locals);
            }

            yield return (DocumentLowering.NamedDeclaration(member: true, name: registered.MemberNames[member], statement: declaration), memberScope);
        }
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
