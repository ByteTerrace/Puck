using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Puck.Cli.Scan;

// Syntax facts more than one analyzer reads.
internal static class ScanSyntax {
    // The nesting of the types a node sits in, outermost first, as an author writes it: `Outer.Inner`.
    public static string TypePath(SyntaxNode node) =>
        string.Join(
            separator: ".",
            values: node.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(selector: static type => type.Identifier.ValueText)
        );
}
