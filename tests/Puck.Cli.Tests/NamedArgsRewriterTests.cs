using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using Puck.Cli.Format.Rewriters;

using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Pins the named-arguments rewrite over calls whose receiver is reached through <c>?.</c>. Such a call binds only
/// inside its conditional access, so the rewriter's re-bind probe has to carry the whole access chain; each case
/// compiles in memory against the runtime's own assemblies, with no project build.
/// </summary>
public sealed class NamedArgsRewriterTests {
    private const string Receiver = """
        namespace Sample;

        internal sealed class Box {
            public Box? Inner;

            public int Take(int b, string a) => b;
        }

        """;

    private static readonly MetadataReference[] References = ((string)AppContext.GetData(name: "TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(separator: Path.PathSeparator)
        .Select(selector: static path => MetadataReference.CreateFromFile(path: path))
        .ToArray<MetadataReference>();

    private static string Rewrite(string body) {
        var tree = CSharpSyntaxTree.ParseText(text: (Receiver + body));
        var compilation = CSharpCompilation.Create(
            assemblyName: "Sample",
            options: new CSharpCompilationOptions(outputKind: OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable),
            references: References,
            syntaxTrees: [tree]
        );

        Assert.Empty(collection: compilation.GetDiagnostics().Where(predicate: static diagnostic => (diagnostic.Severity == DiagnosticSeverity.Error)));

        return new NamedArgsRewriter(model: compilation.GetSemanticModel(syntaxTree: tree)).Visit(node: tree.GetRoot())!.ToFullString();
    }

    [InlineData("box?.Take(1, \"x\")", "box?.Take(a: \"x\", b: 1)")]
    [InlineData("box?.Inner?.Take(1, \"x\")", "box?.Inner?.Take(a: \"x\", b: 1)")]
    [InlineData("box?.Inner!.Take(1, \"x\")", "box?.Inner!.Take(a: \"x\", b: 1)")]
    [InlineData("box!.Take(1, \"x\")", "box!.Take(a: \"x\", b: 1)")]
    [Theory]
    public void ACallThroughAConditionalAccessIsNamedLikeAnyOther(string call, string named) {
        var rewritten = Rewrite(body: $$"""
            internal static class Use {
                public static int? Run(Box? box) => {{call}};
            }

            """);

        Assert.Contains(
            actualString: rewritten,
            expectedSubstring: named
        );
    }
}
