using Puck.Testing;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Validation;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>CONTRACT UNDER TEST: <c>puck lint</c> reports a finding against the source that declares what it is
/// about. An unused constant an import brought in names the imported file, and so does a finding inside an imported
/// module's body; neither prints a line of the importing source. A use inside a group body, a pool loop, a claim or
/// an assignment key counts, and a pool field read through a binding is never taken for a row.</summary>
public sealed class LinterImportAndPoolLawTests : IDisposable {
    private readonly TemporaryDirectory m_directory = new();

    public void Dispose() => m_directory.Dispose();

    private DiagnosticBag Lint(string root, string imported) {
        _ = m_directory.WriteText(name: "imported.puck", text: imported);

        return WorldSourceDiagnostics.Diagnose(
            cancellationToken: TestContext.Current.CancellationToken,
            source: root,
            sourcePath: m_directory.WriteText(name: "root.puck", text: root)
        );
    }

    [Fact]
    public void AnImportedUnusedConstantIsReportedAgainstTheImportedSource() {
        var diagnostics = Lint(
            imported: """
                let spare = 3
                let spent = 2
                """,
            root: """
                import "imported.puck"

                let total = spent + 1

                state {
                  world {
                    slot score = total
                  }
                }
                """
        );
        var unused = diagnostics.Where(predicate: static diagnostic => (diagnostic.Code == PuckDiagnosticCodes.LintUnusedLet)).ToArray();

        Assert.True(condition: (unused.Length == 1), userMessage: string.Join(separator: Environment.NewLine, values: diagnostics.Select(selector: static diagnostic => $"{diagnostic.Code} {diagnostic.SourcePath} {diagnostic.Message}")));
        Assert.Contains(expectedSubstring: "'spare'", actualString: unused[0].Message);
        Assert.Equal(expected: m_directory.PathOf(name: "imported.puck"), actual: unused[0].SourcePath);
        // The finding sits on line 1 of the imported source; line 1 of the importing source is not its excerpt.
        Assert.DoesNotContain(expectedSubstring: "1 | import", actualString: diagnostics.FormatReport(filePath: "root.puck", sourceText: "import \"imported.puck\"\n"));
    }
    // The validation and the reference lint read one composition, so a basis that cannot be found is reported once.
    [Fact]
    public void ARefusedCompositionIsReportedOnce() {
        const string Root = "basis: \"missing/nowhere\"\n\nstate {\n  world {\n    slot a = 0\n  }\n}\n";
        var diagnostics = WorldSourceDiagnostics.Diagnose(
            cancellationToken: TestContext.Current.CancellationToken,
            source: Root,
            sourcePath: m_directory.WriteText(name: "root.puck", text: Root)
        );

        WorldSources.AssertRefusedAt(
            code: PuckDiagnosticCodes.CompositionRefused,
            diagnostics: diagnostics,
            label: "a missing basis",
            needle: "basis:",
            source: Root
        );
    }
    [Fact]
    public void ThePackageThatStampsItsRulesLintsCleanOfUnusedAndUnresolvedFindings() {
        var path = ShippedWorlds.PackageSourcePaths().Single(predicate: static path => path.EndsWith(comparisonType: StringComparison.Ordinal, value: "/worlds/rulepush/rulepush.puck"));
        var diagnostics = WorldSourceDiagnostics.Diagnose(
            cancellationToken: TestContext.Current.CancellationToken,
            source: File.ReadAllText(path: path),
            sourcePath: path
        );
        var findings = diagnostics.Where(predicate: static diagnostic => (diagnostic.Code is PuckDiagnosticCodes.LintUnusedLet or PuckDiagnosticCodes.LintUnresolvedState)).ToArray();

        Assert.True(
            condition: (findings.Length == 0),
            userMessage: string.Join(separator: Environment.NewLine, values: findings.Select(selector: static diagnostic => $"{diagnostic.SourcePath} {diagnostic.Span}: {diagnostic.Message}"))
        );
    }
}
