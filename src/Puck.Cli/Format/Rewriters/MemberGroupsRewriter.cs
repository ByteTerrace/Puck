using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Puck.Cli.Format.Rewriters;

// The member-grouping organizer (the `member-groups` pass, opt-in): fields, properties, and methods
// each gather at their first occurrence. Fields retain their storage-kind order; within each kind,
// accessibility scope groups first and declared name sorts second. Properties and methods likewise
// group by accessibility then sort by name, with overloads stable in source order. Complete declaration
// nodes move, so attributes and comments travel with the member they describe.
//
// Initializer-coupled fields and properties form their own source-ordered group: when an execution
// family has multiple initialized declarations and any initializer is non-inert (a call, a creation, an
// identifier read — anything that may observe order), every movable initializer in that family stays in
// source order because even an inert initializer writes storage another initializer may read.
//
// Struct and record-struct instance-field declarations stay fixed because their order is layout
// (sequential by default); constants and static fields have no instance-layout position and still group.
// Their properties and methods group too. On an unattributed struct this opt-in may therefore change
// compiler-generated auto-property backing-field order, but it never moves an explicit instance field.
// The pass refuses a whole TYPE rather than emit a reorganization that could change behavior:
//   - attributed types (the pass is syntax-only, so an attribute may be an alias for [StructLayout]);
//   - partial types (initializer order interleaves across parts this pass cannot see);
//   - a type whose members carry preprocessor directives;
//   - a coupled field or property that would cross a field-like event initializer (events never move).
// A type the guards refuse simply stays as written. Grouped output is already grouped, so the pass is
// idempotent; run the default set with it (or after it) so member-spacing renormalizes carried whitespace.
internal sealed class MemberGroupsRewriter : CSharpSyntaxRewriter {
    private enum Group {
        Coupled,
        Field,
        Property,
        Method,
    }
    private readonly record struct Entry(MemberDeclarationSyntax Member, int Index);

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node) => Fix(node: ((TypeDeclarationSyntax)base.VisitClassDeclaration(node: node)!));
    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node) => Fix(node: ((TypeDeclarationSyntax)base.VisitStructDeclaration(node: node)!));
    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node) => Fix(node: ((TypeDeclarationSyntax)base.VisitRecordDeclaration(node: node)!));

    private static TypeDeclarationSyntax Fix(TypeDeclarationSyntax node) {
        if (node.Modifiers.Any(predicate: static modifier => modifier.IsKind(kind: SyntaxKind.PartialKeyword))
            || (node.AttributeLists.Count > 0)
            || node.Members.Any(predicate: static member => member.ContainsDirectives)) {
            return node;
        }

        var members = node.Members;

        if (members.Count < 2) {
            return node;
        }

        var coupled = InitializerCoupling.CoupledMemberNames(members: members);
        var structLike = ((node is StructDeclarationSyntax)
            || ((node is RecordDeclarationSyntax record) && record.ClassOrStructKeyword.IsKind(kind: SyntaxKind.StructKeyword)));
        var groups = new Dictionary<Group, List<Entry>>();

        for (var index = 0; (index < members.Count); index++) {
            var member = members[index];
            var group = GroupOf(coupled: coupled, member: member, structLike: structLike);

            if (group is null) {
                continue;
            }

            if (!groups.TryGetValue(key: group.Value, value: out var entries)) {
                entries = [];
                groups.Add(key: group.Value, value: entries);
            }

            entries.Add(item: new Entry(Index: index, Member: member));
        }

        var ordered = groups.ToDictionary(
            keySelector: static pair => pair.Key,
            elementSelector: static pair => Order(group: pair.Key, entries: pair.Value));
        var emitted = new HashSet<Group>();
        var candidate = new List<MemberDeclarationSyntax>(capacity: members.Count);

        foreach (var member in members) {
            var group = GroupOf(coupled: coupled, member: member, structLike: structLike);

            if (group is null) {
                candidate.Add(item: member);
            } else if (emitted.Add(item: group.Value)) {
                candidate.AddRange(collection: ordered[group.Value]);
            }
        }

        if (candidate.SequenceEqual(second: members)) {
            return node;
        }

        if (!InitializerOrderIsPreserved(candidate: candidate, members: members)) {
            return node;
        }

        return node.WithMembers(members: SyntaxFactory.List(nodes: candidate));
    }
    private static Group? GroupOf(MemberDeclarationSyntax member, HashSet<string> coupled, bool structLike) => member switch {
        FieldDeclarationSyntax field when (CanMove(field: field, structLike: structLike) && coupled.Contains(item: NameOf(field: field))) => Group.Coupled,
        PropertyDeclarationSyntax property when coupled.Contains(item: property.Identifier.ValueText) => Group.Coupled,
        FieldDeclarationSyntax field when CanMove(field: field, structLike: structLike) => Group.Field,
        PropertyDeclarationSyntax => Group.Property,
        MethodDeclarationSyntax => Group.Method,
        _ => null,
    };
    private static List<MemberDeclarationSyntax> Order(Group group, List<Entry> entries) {
        IEnumerable<Entry> ordered = group switch {
            Group.Coupled => entries.OrderBy(keySelector: static entry => entry.Index),
            Group.Field => entries
                .OrderBy(keySelector: static entry => Rank(field: ((FieldDeclarationSyntax)entry.Member)))
                .ThenBy(keySelector: static entry => RewriteShaping.AccessibilityScope(member: entry.Member), comparer: StringComparer.Ordinal)
                .ThenBy(keySelector: static entry => NameOf(field: ((FieldDeclarationSyntax)entry.Member)), comparer: StringComparer.Ordinal)
                .ThenBy(keySelector: static entry => entry.Index),
            Group.Property => entries
                .OrderBy(keySelector: static entry => RewriteShaping.AccessibilityScope(member: entry.Member), comparer: StringComparer.Ordinal)
                .ThenBy(keySelector: static entry => ((PropertyDeclarationSyntax)entry.Member).Identifier.ValueText, comparer: StringComparer.Ordinal)
                .ThenBy(keySelector: static entry => entry.Index),
            Group.Method => entries
                .OrderBy(keySelector: static entry => RewriteShaping.AccessibilityScope(member: entry.Member), comparer: StringComparer.Ordinal)
                .ThenBy(keySelector: static entry => ((MethodDeclarationSyntax)entry.Member).Identifier.ValueText, comparer: StringComparer.Ordinal)
                .ThenBy(keySelector: static entry => entry.Index),
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(group), actualValue: group, message: "The member group is undefined."),
        };

        return ordered.Select(selector: static entry => entry.Member).ToList();
    }
    private static string NameOf(FieldDeclarationSyntax field) =>
        field.Declaration.Variables[0].Identifier.ValueText;
    private static bool CanMove(FieldDeclarationSyntax field, bool structLike) =>
        (!structLike || field.Modifiers.Any(predicate: static modifier =>
            (modifier.IsKind(kind: SyntaxKind.ConstKeyword) || modifier.IsKind(kind: SyntaxKind.StaticKeyword))));
    private static int Rank(FieldDeclarationSyntax field) {
        var isConst = false;
        var isStatic = false;
        var isReadonly = false;

        foreach (var modifier in field.Modifiers) {
            isConst |= modifier.IsKind(kind: SyntaxKind.ConstKeyword);
            isStatic |= modifier.IsKind(kind: SyntaxKind.StaticKeyword);
            isReadonly |= modifier.IsKind(kind: SyntaxKind.ReadOnlyKeyword);
        }

        return (isConst ? 0 : (isStatic ? (isReadonly ? 1 : 2) : (isReadonly ? 3 : 4)));
    }
    // The backstop over the final order: same-family initializers may swap only when BOTH right-hand
    // sides are inert. Movable couplings are source-ordered, so this fires when gathering them would
    // cross a field-like event initializer that cannot travel with the group.
    private static bool InitializerOrderIsPreserved(SyntaxList<MemberDeclarationSyntax> members, List<MemberDeclarationSyntax> candidate) {
        var oldSequence = InitializerCoupling.CollectInitialized(members: members);

        if (oldSequence.Count < 2) {
            return true;
        }

        var newSequence = InitializerCoupling.CollectInitialized(members: SyntaxFactory.List(nodes: candidate));

        for (var first = 0; (first < oldSequence.Count); first++) {
            for (var second = (first + 1); (second < oldSequence.Count); second++) {
                var a = oldSequence[first];
                var b = oldSequence[second];

                if (a.IsStatic != b.IsStatic) {
                    continue;
                }

                if ((IndexOf(sequence: newSequence, name: a.Name) > IndexOf(sequence: newSequence, name: b.Name))
                    && (!a.IsInert || !b.IsInert)) {
                    return false;
                }
            }
        }

        return true;
    }
    private static int IndexOf(List<InitializerCoupling.Initialized> sequence, string name) {
        for (var index = 0; (index < sequence.Count); index++) {
            if (string.Equals(a: sequence[index].Name, b: name, comparisonType: StringComparison.Ordinal)) {
                return index;
            }
        }

        return -1;
    }
}
