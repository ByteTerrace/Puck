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
