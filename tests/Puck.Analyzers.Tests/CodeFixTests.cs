using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Xunit;

namespace Puck.Analyzers.Tests;

/// <summary>
/// The one-click repair: when a fingerprint drifts, the fix offers to write the recomputed hash into the ledger.
/// These cases ask whether the offer is honest — whether it appears only when it can act, edits the entry it
/// names, writes the value it promised, and leaves the rest of the authored file exactly as it found it.
/// </summary>
public sealed class CodeFixTests {
    private static readonly string StaleHash = new(c: '0', count: 64);
    // Two drifted bodies, chosen by what their recomputed fingerprint starts with. Roughly five in eight recomputed
    // hashes begin with a digit, and a digit-leading hash is the one a substitution pattern would swallow, so both
    // shapes are exercised rather than only whichever the first drifted body happened to produce.
    private static readonly (string Source, string Hash) LetterLeadingDrift = FindDrift(predicate: char.IsAsciiLetter);
    private static readonly (string Source, string Hash) DigitLeadingDrift = FindDrift(predicate: char.IsAsciiDigit);

    private static (string Source, string Hash) FindDrift(Func<char, bool> predicate) {
        for (var value = 0; (value < 200); value++) {
            var source = Sources.BrandedMethod(body: $"        return {value.ToString(provider: CultureInfo.InvariantCulture)};");
            var hash = Harness.Fingerprint(source: source, id: Sources.TargetId);

            if (predicate(arg: hash[0])) {
                return (Source: source, Hash: hash);
            }
        }

        throw new InvalidOperationException(message: "No drifted body produced a fingerprint with the requested leading character.");
    }
    private static string StaleLedger(string id = Sources.TargetId, string? extraMembers = null) =>
        Manifest.Of(new ManifestEntry { ExtraMembers = extraMembers, Id = id, Sha256 = StaleHash, Symbol = Sources.TargetSymbol });
    private static async Task<(ImmutableArray<Diagnostic> Diagnostics, ImmutableArray<CodeAction> Actions)> DiagnoseAndOfferAsync(FixSubject subject, CancellationToken cancellationToken) {
        var diagnostics = await FixHarness.DiagnoseAsync(solution: subject.Solution, projectId: subject.Solution.ProjectIds[0], cancellationToken: cancellationToken);
        var mismatches = diagnostics.Where(predicate: diagnostic => string.Equals(a: diagnostic.Id, b: "VER001", comparisonType: StringComparison.Ordinal)).ToImmutableArray();

        if (mismatches.Length != 1) {
            return (Diagnostics: diagnostics, Actions: []);
        }

        return (Diagnostics: diagnostics, Actions: await FixHarness.ActionsAsync(solution: subject.Solution, documentId: subject.SourceId, diagnostic: mismatches[0], cancellationToken: cancellationToken));
    }
    private static async Task<string> RepairAsync(FixSubject subject, CancellationToken cancellationToken) {
        var (_, actions) = await DiagnoseAndOfferAsync(cancellationToken: cancellationToken, subject: subject);
        var changed = await FixHarness.ApplyAsync(action: Assert.Single(collection: actions), cancellationToken: cancellationToken);

        return (await FixHarness.ManifestTextAsync(solution: changed!, manifestId: subject.ManifestId, cancellationToken: cancellationToken)).ToString();
    }

    [Fact]
    public async Task FixWritesTheRecomputedHashIntoTheEntryItNames() {
        using var subject = FixHarness.Create(source: LetterLeadingDrift.Source, manifestJson: StaleLedger());

        var text = await RepairAsync(subject: subject, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: LetterLeadingDrift.Hash, actual: Manifest.RecordedHash(id: Sources.TargetId, json: text));
    }
    [Fact]
    public async Task LedgerTheFixProducedSatisfiesTheAnalyzer() {
        var cancellationToken = TestContext.Current.CancellationToken;

        using var subject = FixHarness.Create(source: LetterLeadingDrift.Source, manifestJson: StaleLedger());

        var (_, actions) = await DiagnoseAndOfferAsync(cancellationToken: cancellationToken, subject: subject);
        var changed = await FixHarness.ApplyAsync(action: Assert.Single(collection: actions), cancellationToken: cancellationToken);
        var after = await FixHarness.DiagnoseAsync(solution: changed!, projectId: changed!.ProjectIds[0], cancellationToken: cancellationToken);

        Assert.Empty(collection: after);
    }
    [Fact]
    public async Task FixChangesNothingButTheRecordedHash() {
        var before = StaleLedger();

        using var subject = FixHarness.Create(source: LetterLeadingDrift.Source, manifestJson: before);

        var after = await RepairAsync(subject: subject, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: before.Replace(comparisonType: StringComparison.Ordinal, newValue: LetterLeadingDrift.Hash, oldValue: StaleHash), actual: after);
    }
    [Fact]
    public async Task FixWritesADigitLeadingHashIntactAndLeavesTheEntryParseable() {
        var cancellationToken = TestContext.Current.CancellationToken;

        using var subject = FixHarness.Create(source: DigitLeadingDrift.Source, manifestJson: StaleLedger());

        var text = await RepairAsync(cancellationToken: cancellationToken, subject: subject);

        Assert.Contains(actualString: text, expectedSubstring: "\"sha256\"");
        Assert.Equal(expected: DigitLeadingDrift.Hash, actual: Manifest.RecordedHash(id: Sources.TargetId, json: text));
        Assert.Equal(expected: StaleLedger().Replace(comparisonType: StringComparison.Ordinal, newValue: DigitLeadingDrift.Hash, oldValue: StaleHash), actual: text);
    }
    [Fact]
    public async Task FixPreservesTheLedgersEncoding() {
        var cancellationToken = TestContext.Current.CancellationToken;

        using var subject = FixHarness.Create(source: LetterLeadingDrift.Source, manifestJson: StaleLedger(), encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var (_, actions) = await DiagnoseAndOfferAsync(cancellationToken: cancellationToken, subject: subject);
        var changed = await FixHarness.ApplyAsync(action: Assert.Single(collection: actions), cancellationToken: cancellationToken);
        var text = await FixHarness.ManifestTextAsync(solution: changed!, manifestId: subject.ManifestId, cancellationToken: cancellationToken);

        Assert.Equal(expected: 3, actual: text.Encoding!.GetPreamble().Length);
    }
    [Fact]
    public async Task FixPreservesCarriageReturnLineEndings() {
        using var subject = FixHarness.Create(source: LetterLeadingDrift.Source, manifestJson: StaleLedger());

        var text = await RepairAsync(subject: subject, cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain(expectedSubstring: "\n", actualString: text.Replace(comparisonType: StringComparison.Ordinal, newValue: string.Empty, oldValue: "\r\n"));
    }
    [Fact]
    public async Task FixPreservesLineFeedOnlyLineEndings() {
        using var subject = FixHarness.Create(source: LetterLeadingDrift.Source, manifestJson: StaleLedger().Replace(comparisonType: StringComparison.Ordinal, newValue: "\n", oldValue: "\r\n"));

        var text = await RepairAsync(subject: subject, cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain(actualString: text, expectedSubstring: "\r");
    }
    [Fact]
    public void FixIsOfferedForFingerprintDriftAlone() =>
        Assert.Equal(expected: new[] { "VER001" }, actual: new VerifiedCodeCodeFixProvider().FixableDiagnosticIds.ToArray());
    [Fact]
    public async Task NoFixIsOfferedForAnUnclaimedEntry() {
        var cancellationToken = TestContext.Current.CancellationToken;

        using var subject = FixHarness.Create(source: Sources.Unbranded(), manifestJson: StaleLedger(id: "deleted-brand"));

        var diagnostics = await FixHarness.DiagnoseAsync(solution: subject.Solution, projectId: subject.Solution.ProjectIds[0], cancellationToken: cancellationToken);
        var unclaimed = Assert.Single(collection: diagnostics);
        var actions = await FixHarness.ActionsAsync(solution: subject.Solution, documentId: subject.SourceId, diagnostic: unclaimed, cancellationToken: cancellationToken);

        Assert.Equal(expected: "VER002", actual: unclaimed.Id);
        Assert.Empty(collection: actions);
    }
    [Fact]
    public async Task NoFixIsOfferedForABrandTheLedgerDoesNotRecord() {
        var cancellationToken = TestContext.Current.CancellationToken;

        using var subject = FixHarness.Create(source: Sources.BrandedMethod(), manifestJson: Manifest.Empty);

        var (diagnostics, actions) = await DiagnoseAndOfferAsync(cancellationToken: cancellationToken, subject: subject);

        // Rewriting a hash cannot record an entry that is not there, and an action reporting success having
        // changed nothing is worse than no action.
        Assert.Contains(collection: diagnostics, filter: diagnostic => string.Equals(a: diagnostic.Id, b: "VER001", comparisonType: StringComparison.Ordinal));
        Assert.Empty(collection: actions);
    }
    [Fact]
    public async Task NoFixIsOfferedForAnUnusableRecordedHashBecauseTheLedgerIsRefusedFirst() {
        var cancellationToken = TestContext.Current.CancellationToken;
        var before = Manifest.Of(new ManifestEntry { Id = Sources.TargetId, Sha256 = "not-hex", Symbol = Sources.TargetSymbol });

        using var subject = FixHarness.Create(source: LetterLeadingDrift.Source, manifestJson: before);

        var diagnostics = await FixHarness.DiagnoseAsync(solution: subject.Solution, projectId: subject.Solution.ProjectIds[0], cancellationToken: cancellationToken);

        // A hash that cannot be a fingerprint is a schema failure on the ledger, not a drift on the brand, so
        // there is no repair to offer and nothing pretends otherwise.
        Assert.Equal(expected: new[] { "VER006" }, actual: diagnostics.Select(selector: diagnostic => diagnostic.Id).ToArray());
    }
    [Fact]
    public async Task FixIsOfferedWhenTheBrandIdContainsAnApostrophe() {
        var cancellationToken = TestContext.Current.CancellationToken;
        var source = Sources.BrandedMethod(attribute: "[VerifiedCode(\"target's\")]", body: "        return 2;");
        var hash = Harness.Fingerprint(source: source, id: "target's");

        using var subject = FixHarness.Create(source: source, manifestJson: StaleLedger(id: "target's"));

        // The id and hash travel as diagnostic properties, so display text an unusual id reshapes cannot break
        // the repair.
        var text = await RepairAsync(cancellationToken: cancellationToken, subject: subject);

        Assert.Equal(expected: hash, actual: Manifest.RecordedHash(id: "target's", json: text));
    }
    [Fact]
    public async Task FixEditsTheLedgersOwnEntryNotAnUnrelatedObjectSharingItsKey() {
        var decoyHash = new string(c: 'a', count: 64);

        var ledger = $$"""
            {
                "format": 1,
                "notes": {
                    "target": {
                        "sha256": "{{decoyHash}}"
                    }
                },
                "entries": {
                    "target": {
                        "assembly": "Subject.Assembly",
                        "symbol": "M:Subject.Assembly.Subject.Target",
                        "algorithm": "csharp-tokens-v1",
                        "sha256": "{{StaleHash}}",
                        "basis": [ "exhaustive" ],
                        "dependencies": [],
                        "laws": []
                    }
                }
            }

            """;

        using var subject = FixHarness.Create(source: LetterLeadingDrift.Source, manifestJson: ledger);

        var text = await RepairAsync(subject: subject, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(actualString: text, expectedSubstring: decoyHash);
        Assert.Equal(expected: LetterLeadingDrift.Hash, actual: Manifest.RecordedHash(id: Sources.TargetId, json: text));
    }
    [Fact]
    public async Task FixLeavesEveryHashNestedInsideTheEntryAlone() {
        var nestedHash = new string(c: 'b', count: 64);

        using var subject = FixHarness.Create(
            source: LetterLeadingDrift.Source,
            manifestJson: StaleLedger(extraMembers: $"            \"provenance\": {{ \"sha256\": \"{nestedHash}\" }}"));

        var text = await RepairAsync(subject: subject, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(actualString: text, expectedSubstring: nestedHash);
        Assert.Equal(expected: 1, actual: (text.Split(separator: LetterLeadingDrift.Hash).Length - 1));
        Assert.Equal(expected: LetterLeadingDrift.Hash, actual: Manifest.RecordedHash(id: Sources.TargetId, json: text));
    }
    [Fact]
    public async Task NoFixIsOfferedWhenTwoDocumentsAreNamedLikeTheLedger() {
        var cancellationToken = TestContext.Current.CancellationToken;
        var secondHash = new string(c: 'c', count: 64);

        using var subject = FixHarness.Create(source: LetterLeadingDrift.Source, manifestJson: StaleLedger());

        var secondId = DocumentId.CreateNewId(projectId: subject.Solution.ProjectIds[0]);

        var withSecond = FixHarness.AddSecondManifest(
            solution: subject.Solution,
            manifestId: secondId,
            manifestJson: Manifest.Of(new ManifestEntry { Id = Sources.TargetId, Sha256 = secondHash, Symbol = Sources.TargetSymbol }),
            path: Path.Combine(path1: Path.GetTempPath(), path2: "second", path3: Harness.ManifestFileName));

        var diagnostics = await FixHarness.DiagnoseAsync(solution: withSecond, projectId: withSecond.ProjectIds[0], cancellationToken: cancellationToken);

        // Which file is the ledger is ambiguous, so the analyzer reads neither and the repair rewrites neither.
        Assert.Equal(expected: new[] { "VER006" }, actual: diagnostics.Select(selector: diagnostic => diagnostic.Id).ToArray());

        var actions = await FixHarness.ActionsAsync(solution: withSecond, documentId: subject.SourceId, diagnostic: diagnostics[0], cancellationToken: cancellationToken);

        Assert.Empty(collection: actions);
    }
    [Fact]
    public async Task FixLeavesTheSamePhysicalLedgerStaleInEveryOtherProjectThatLinksIt() {
        var cancellationToken = TestContext.Current.CancellationToken;

        using var subject = FixHarness.Create(source: LetterLeadingDrift.Source, manifestJson: StaleLedger());

        // One physical VerifiedCode.json is linked into every project, so another project holds its own document
        // over the same path. The repair edits one document; keeping the file and the other project in step is the
        // host's job, and this records where that boundary falls.
        var linkedManifestId = subject.AddLinkedProject(assemblyName: "Other.Assembly", source: Sources.Unbranded(), manifestJson: StaleLedger());

        var (_, actions) = await DiagnoseAndOfferAsync(cancellationToken: cancellationToken, subject: subject);
        var changed = await FixHarness.ApplyAsync(action: Assert.Single(collection: actions), cancellationToken: cancellationToken);

        var edited = (await FixHarness.ManifestTextAsync(solution: changed!, manifestId: subject.ManifestId, cancellationToken: cancellationToken)).ToString();
        var linked = (await FixHarness.ManifestTextAsync(cancellationToken: cancellationToken, manifestId: linkedManifestId, solution: changed!)).ToString();

        Assert.Equal(expected: LetterLeadingDrift.Hash, actual: Manifest.RecordedHash(id: Sources.TargetId, json: edited));
        Assert.Equal(expected: StaleHash, actual: Manifest.RecordedHash(id: Sources.TargetId, json: linked));
    }
    [Fact]
    public async Task FixAllWritesEveryDriftedBrandsRecomputedHash() {
        var cancellationToken = TestContext.Current.CancellationToken;

        var source = """
            namespace Subject.Assembly;

            internal static class Subject {
                [VerifiedCode("alpha")]
                public static int Alpha() {
                    return 1;
                }

                [VerifiedCode("beta")]
                public static int Beta() {
                    return 2;
                }
            }

            """;

        var before = Manifest.Of(
            new ManifestEntry { Id = "alpha", Sha256 = StaleHash, Symbol = "M:Subject.Assembly.Subject.Alpha" },
            new ManifestEntry { Id = "beta", Sha256 = new string(c: 'd', count: 64), Symbol = "M:Subject.Assembly.Subject.Beta" });

        using var subject = FixHarness.Create(source: source, manifestJson: before);

        var diagnostics = await FixHarness.DiagnoseAsync(solution: subject.Solution, projectId: subject.Solution.ProjectIds[0], cancellationToken: cancellationToken);
        var mismatches = diagnostics.Where(predicate: diagnostic => string.Equals(a: diagnostic.Id, b: "VER001", comparisonType: StringComparison.Ordinal)).ToImmutableArray();

        Assert.Equal(expected: 2, actual: mismatches.Length);

        var changed = await FixHarness.FixAllAsync(
            solution: subject.Solution,
            documentId: subject.SourceId,
            diagnostics: mismatches,
            equivalenceKey: "UpdateVerifiedCodeBrand",
            cancellationToken: cancellationToken);

        var after = (await FixHarness.ManifestTextAsync(solution: changed!, manifestId: subject.ManifestId, cancellationToken: cancellationToken)).ToString();

        var expected = mismatches
            .OrderBy(keySelector: diagnostic => diagnostic.Properties["VerifiedCodeId"], comparer: StringComparer.Ordinal)
            .Select(selector: diagnostic => diagnostic.Properties["VerifiedCodeHash"])
            .ToArray();

        Assert.Equal(expected: expected, actual: new[] { Manifest.RecordedHash(id: "alpha", json: after), Manifest.RecordedHash(id: "beta", json: after) });
        Assert.NotEqual(actual: after, expected: before);
    }
}
