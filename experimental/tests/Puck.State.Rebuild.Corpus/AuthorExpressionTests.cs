using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Puck.Transpiler.Lowering;
using Puck.World.Transpiler;
using Puck.World.Transpiler.Decompiler;
using Puck.World.Transpiler.Validation;
using Xunit;

namespace Puck.State.Rebuild.Corpus;

/// <summary>The author-expression gate: every authored <c>.puck</c> source compiles clean, decompiles, and recompiles
/// to the same document.</summary>
/// <remarks>A rebuild of the state substrate may change the lowered document freely, but not what an author wrote:
/// the invariant this asserts is that compile and decompile stay mutual inverses over the whole shipped corpus. A
/// source emitting several worlds decompiles as one composition; a module fragment emits no world of its own and is
/// held through the sources that import it. It runs entirely on the transpiler, so it needs no booted world.</remarks>
public partial class AuthorExpressionTests {
    [GeneratedRegex(pattern: "^\\s*import\\s+\"(?<path>[^\"]+)\"", options: RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.Multiline)]
    private static partial Regex ImportLine();
    public static TheoryData<string> ExemptPaths() => CorpusSources.Exempt();
    public static TheoryData<string> PackagePaths() => new(values: CorpusSources.Packages());
    /// <summary>Holds each round-trip exemption to the reason it was granted. An exemption that stops being necessary
    /// fails here instead of sitting unnoticed, so the ledger only shrinks.</summary>
    /// <param name="relativePath">A repository-relative forward-slashed source path.</param>
    [MemberData(nameof(ExemptPaths))]
    [Theory]
    public void ExemptSourceStillFailsForItsReason(string relativePath) {
        var failure = RoundTrip(decompiled: out var decompiled, relativePath: relativePath);

        Assert.True(
            condition: (failure is not null),
            userMessage: $"{relativePath} now round-trips: drop it from CorpusSources.RoundTripExemptions."
        );

        switch (CorpusSources.RoundTripExemptions[relativePath]) {
            case CorpusExemption.DecompiledPastTheSourceLimit:
                Assert.True(
                    condition: ((decompiled?.Length ?? 0) > DocumentEvaluationBudget.TextLimit),
                    userMessage: $"{relativePath} fails the round trip for another reason than its exemption names, since it decompiles to {decompiled?.Length} characters, within the {DocumentEvaluationBudget.TextLimit}-character source limit:{Environment.NewLine}{failure}"
                );

                break;
            default:
                Assert.Fail(message: $"{relativePath}: no check holds exemption '{CorpusSources.RoundTripExemptions[relativePath]}' to its reason");

                break;
        }
    }
    /// <summary>A package source that emits no world of its own is a module fragment, and it is held through a source
    /// of the same package that imports it rather than on its own.</summary>
    /// <param name="relativePath">A repository-relative forward-slashed package source path.</param>
    [MemberData(nameof(PackagePaths))]
    [Theory]
    public void EveryPackageFragmentIsImportedByASourceOfItsPackage(string relativePath) {
        var absolutePath = RepositoryPaths.Resolve(relativePath: relativePath);
        var compiled = Compile(relativePath: relativePath, sourceText: File.ReadAllText(path: absolutePath));

        if (!IsFragment(compilation: compiled)) {
            return;
        }

        var importers = CorpusSources.Packages()
            .Where(predicate: other => (other != relativePath))
            .Where(predicate: other => ImportLine().Matches(input: File.ReadAllText(path: RepositoryPaths.Resolve(relativePath: other))).Any(predicate: match => string.Equals(
                a: Path.GetFullPath(path: Path.Combine(path1: Path.GetDirectoryName(path: RepositoryPaths.Resolve(relativePath: other))!, path2: match.Groups["path"].Value)),
                b: Path.GetFullPath(path: absolutePath),
                comparisonType: StringComparison.OrdinalIgnoreCase
            )));

        Assert.True(
            condition: importers.Any(),
            userMessage: $"{relativePath} emits no world and no package source imports it, so nothing holds it to the round trip"
        );
    }
}
