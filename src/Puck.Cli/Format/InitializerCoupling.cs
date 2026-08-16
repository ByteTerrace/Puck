using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Puck.Cli.Format;

// Initializer-coupling analysis shared by member-groups (which gathers coupled fields and properties as
// one block), member-order (which must not split that block), and member-spacing (which keeps it tight).
// Initializers run in declaration order per static-or-instance family. One non-inert initializer couples
// every initialized member in that family: even an inert right-hand side writes its declared storage,
// which another initializer may read.
internal static class InitializerCoupling {
    // One initialized declaration (a non-const field, field-like event, or property), in its static-or-
    // instance execution family. Identity is the first declared name — unique within a type, and stable
    // across a member-list rebuild (node references are not: re-wrapping a member list mints fresh red
    // nodes).
    public readonly record struct Initialized(string Name, bool IsStatic, bool IsInert, bool MovesWithOrganizer);

    // The coupled set: initialized fields and properties in a family that has more than one initialized
    // declaration and at least one non-inert initializer. The entire movable sequence travels in source
    // order, because a non-inert initializer may read storage whose own right-hand side is inert.
    public static HashSet<string> CoupledMemberNames(SyntaxList<MemberDeclarationSyntax> members) {
        var sequence = CollectInitialized(members: members);
        var result = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var family in ((ReadOnlySpan<bool>)[false, true,])) {
            var isStatic = family;
            var coupled = sequence.Where(predicate: entry => (entry.IsStatic == isStatic)).ToList();

            if ((coupled.Count >= 2) && coupled.Any(predicate: static entry => !entry.IsInert)) {
                result.UnionWith(other: coupled.Where(predicate: static entry => entry.MovesWithOrganizer).Select(selector: static entry => entry.Name));
            }
        }

        return result;
    }
    public static List<Initialized> CollectInitialized(SyntaxList<MemberDeclarationSyntax> members) {
        var sequence = new List<Initialized>();

        foreach (var member in members) {
            switch (member) {
                case FieldDeclarationSyntax field when !field.Modifiers.Any(predicate: static modifier => modifier.IsKind(kind: SyntaxKind.ConstKeyword)):
                    AddVariableDeclaration(
                        sequence: sequence,
                        declaration: field.Declaration,
                        isStatic: field.Modifiers.Any(predicate: static modifier => modifier.IsKind(kind: SyntaxKind.StaticKeyword)),
                        movesWithOrganizer: true);

                    break;
                case EventFieldDeclarationSyntax eventField:
                    AddVariableDeclaration(
                        sequence: sequence,
                        declaration: eventField.Declaration,
                        isStatic: eventField.Modifiers.Any(predicate: static modifier => modifier.IsKind(kind: SyntaxKind.StaticKeyword)),
                        movesWithOrganizer: false);

                    break;
                case PropertyDeclarationSyntax { Initializer.Value: { } value } property:
                    sequence.Add(item: new Initialized(
                        Name: property.Identifier.ValueText,
                        IsStatic: property.Modifiers.Any(predicate: static modifier => modifier.IsKind(kind: SyntaxKind.StaticKeyword)),
                        IsInert: IsInert(expression: value),
                        MovesWithOrganizer: true));

                    break;
                default:
                    break;
            }
        }

        return sequence;
    }

    private static void AddVariableDeclaration(List<Initialized> sequence, VariableDeclarationSyntax declaration, bool isStatic, bool movesWithOrganizer) {
        var initializers = declaration.Variables
            .Where(predicate: static variable => (variable.Initializer is not null))
            .Select(selector: static variable => variable.Initializer!.Value)
            .ToList();

        if (initializers.Count == 0) {
            return;
        }

        sequence.Add(item: new Initialized(
            Name: declaration.Variables[0].Identifier.ValueText,
            IsStatic: isStatic,
            IsInert: initializers.TrueForAll(match: IsInert),
            MovesWithOrganizer: movesWithOrganizer));
    }

    // Inert = provably order-independent: no side-effecting node AND no identifier at all, so the
    // expression neither writes state nor reads anything another initializer could have written.
    // Literals, arithmetic over literals, collection expressions of literals, `default`, and casts to
    // predefined types qualify; anything touching a name is conservatively treated as coupled-capable.
    public static bool IsInert(ExpressionSyntax expression) =>
        (!ExpressionSafety.HasSideEffect(expression: expression)
            && !expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any());
}
