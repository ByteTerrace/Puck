using System.Text;
using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Decompiler;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The projection law: source and document are two views of one thing, so printing a document and
/// compiling the print returns the same document, and formatting a source never moves what it compiles to.</summary>
/// <remarks>It runs over every shipped <c>.puck</c> source and over one generated source per construct
/// <see cref="ConstructRegistry"/> registers, so a construct the printer cannot reproduce fails under its own
/// name.</remarks>
public class ProjectionLawTests {
    /// <summary>Sources whose document prints back longer than <c>PUCK047</c>'s source limit, so the print half of
    /// the law cannot close on them. They stay under the format half and under the regeneration gate.
    /// <see cref="APrintExcludedSourceStillOutgrowsTheParser"/> fails when an exclusion stops being
    /// necessary.</summary>
    private static readonly string[] PrintExclusions = ["moth-courtyard.puck"];

    public static TheoryData<string> PrintExcludedSources() => new(values: PrintExclusions);
    [MemberData(nameof(PrintExcludedSources))]
    [Theory]
    public void APrintExcludedSourceStillOutgrowsTheParser(string relativePath) {
        var printed = WorldDecompiler.Decompile(root: ShippedWorlds.Compile(relativePath: relativePath).RequireJson());
        var diagnostics = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: printed
        ).Diagnostics;

        Assert.Contains(
            collection: diagnostics,
            filter: static diagnostic => (diagnostic.Code == PuckDiagnosticCodes.EvaluationLimit)
        );
    }
}
