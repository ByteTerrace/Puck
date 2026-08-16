using Xunit;

namespace Puck.Analyzers.Tests;

/// <summary>
/// What the analyzer does when the ledger itself is absent, unreadable, off-schema, or ambiguous. The ledger is the
/// only record that a brand ever existed, so every state here decides whether the sweep still runs at all — and
/// every one of them is refused on the ledger rather than absorbed into an empty one.
/// </summary>
public sealed class ManifestIntegrityTests {
    private static readonly string ShadowManifestPath = Path.Combine(path1: Path.GetTempPath(), path2: "shadow", path3: Harness.ManifestFileName);

    [Fact]
    public void AbsentManifestIsRefusedRatherThanReadAsAnEmptyLedger() {
        var result = Harness.RunWithoutManifest(source: Sources.Unbranded());

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        Assert.Equal(expected: new[] { "VER006" }, actual: result.Ids);
        Assert.Contains(expectedSubstring: "AdditionalFile", actualString: result.Single(id: "VER006").GetMessage());
    }
    [Fact]
    public void UnreadableManifestIsRefusedRatherThanReadAsAnEmptyLedger() {
        var result = Harness.Analyze(
            compilation: Harness.Compile(assemblyName: Harness.DefaultAssemblyName, sources: new SourceFile(Name: "Subject.cs", Text: Sources.Unbranded())),
            additionalFiles: new HarnessAdditionalText(path: Harness.ManifestPath, text: null));

        Assert.Equal(expected: new[] { "VER006" }, actual: result.Ids);
        Assert.Contains(expectedSubstring: "could not be read", actualString: result.Single(id: "VER006").GetMessage());
    }
    [Fact]
    public void AbsentManifestBlamesTheLedgerRatherThanEveryBrandItCannotJudge() {
        var result = Harness.RunWithoutManifest(source: Sources.BrandedMethod());

        // The brand cannot be judged without a ledger, so it is not accused of a drift that never happened.
        Assert.Equal(expected: new[] { "VER006" }, actual: result.Ids);
    }
    [Fact]
    public void MalformedManifestIsRefusedRatherThanReadAsAnEmptyLedger() {
        var result = Harness.Run(source: Sources.Unbranded(), manifestJson: Manifest.Malformed);

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        Assert.Equal(expected: new[] { "VER006" }, actual: result.Ids);
    }
    [Fact]
    public void ManifestWhoseRootIsNotAnObjectIsRefused() {
        var result = Harness.Run(source: Sources.Unbranded(), manifestJson: "[ 1, 2, 3 ]");

        Assert.Equal(expected: new[] { "VER006" }, actual: result.Ids);
    }
    [Fact]
    public void ManifestDiagnosticIsReportedOnTheLedgerFileItself() {
        var result = Harness.Run(source: Sources.Unbranded(), manifestJson: Manifest.Malformed);

        Assert.Equal(expected: Harness.ManifestPath, actual: result.Single(id: "VER006").Location.GetLineSpan().Path);
    }
    [Fact]
    public void OneOffSchemaEntryIsReportedWithoutDiscardingEveryOtherEntrysSweep() {
        var manifest = """
            {
                "format": 1,
                "entries": {
                    "deleted-brand": {
                        "assembly": "Subject.Assembly",
                        "symbol": "M:Subject.Assembly.Subject.Gone",
                        "algorithm": "csharp-tokens-v1",
                        "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
                        "basis": [ "exhaustive" ],
                        "dependencies": [],
                        "laws": []
                    },
                    "missing-laws": {
                        "assembly": "Subject.Assembly",
                        "symbol": "M:Subject.Assembly.Subject.Other",
                        "algorithm": "csharp-tokens-v1",
                        "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
                        "basis": [ "exhaustive" ],
                        "dependencies": []
                    }
                }
            }

            """;

        var result = Harness.Run(source: Sources.Unbranded(), manifestJson: manifest);

        // An entry-level failure is per-entry: "deleted-brand" is a perfectly good entry that nothing claims, and
        // its deletion is still reported alongside its neighbour's schema failure.
        Assert.Equal(expected: new[] { "VER002", "VER006" }, actual: result.Ids);
        Assert.Contains(expectedSubstring: "missing-laws", actualString: result.Single(id: "VER006").GetMessage());
        Assert.Contains(expectedSubstring: "deleted-brand", actualString: result.Single(id: "VER002").GetMessage());
    }
    [Fact]
    public void MalformedManifestStillFailsABrandedCompilation() {
        var result = Harness.Run(source: Sources.BrandedMethod(), manifestJson: Manifest.Malformed);

        Assert.Equal(expected: new[] { "VER006" }, actual: result.Ids);
    }
    [Fact]
    public void RepeatedEntryKeyIsRefusedRatherThanLettingTheLastCopyDecide() {
        var source = Sources.BrandedMethod();
        var hash = Harness.Fingerprint(source: source, id: Sources.TargetId);

        var manifest = $$"""
            {
                "format": 1,
                "entries": {
                    "target": {
                        "assembly": "Subject.Assembly",
                        "symbol": "M:Subject.Assembly.Subject.Target",
                        "algorithm": "csharp-tokens-v1",
                        "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
                        "basis": [ "exhaustive" ],
                        "dependencies": [],
                        "laws": []
                    },
                    "target": {
                        "assembly": "Subject.Assembly",
                        "symbol": "M:Subject.Assembly.Subject.Target",
                        "algorithm": "csharp-tokens-v1",
                        "sha256": "{{hash}}",
                        "basis": [ "exhaustive" ],
                        "dependencies": [],
                        "laws": []
                    }
                }
            }

            """;

        var result = Harness.Run(source: source, manifestJson: manifest);

        Assert.Equal(expected: new[] { "VER006" }, actual: result.Ids);
        Assert.Contains(expectedSubstring: "Duplicate object member 'target'", actualString: result.Single(id: "VER006").GetMessage());
    }
    [Fact]
    public void RepeatedRootMemberIsRefusedRatherThanLettingTheLastCopyReplaceTheLedger() {
        var manifest = """
            {
                "format": 1,
                "entries": {
                    "deleted-brand": {
                        "assembly": "Subject.Assembly",
                        "symbol": "M:Subject.Assembly.Subject.Gone",
                        "algorithm": "csharp-tokens-v1",
                        "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
                        "basis": [ "exhaustive" ],
                        "dependencies": [],
                        "laws": []
                    }
                },
                "entries": {}
            }

            """;

        var result = Harness.Run(source: Sources.Unbranded(), manifestJson: manifest);

        Assert.Equal(expected: new[] { "VER006" }, actual: result.Ids);
        Assert.Contains(expectedSubstring: "Duplicate object member 'entries'", actualString: result.Single(id: "VER006").GetMessage());
    }
    [Fact]
    public void ManifestDeclaringAnUnknownSchemaVersionIsRefused() {
        var source = Sources.BrandedMethod();

        var result = Harness.Run(
            source: source,
            manifestJson: Manifest.Of(format: "999", entries: new ManifestEntry {
                Id = Sources.TargetId,
                Sha256 = Harness.Fingerprint(source: source, id: Sources.TargetId),
                Symbol = Sources.TargetSymbol,
            }));

        Assert.Equal(expected: new[] { "VER006" }, actual: result.Ids);
        Assert.Contains(expectedSubstring: "999", actualString: result.Single(id: "VER006").GetMessage());
    }
    [Fact]
    public void ManifestDeclaringNoSchemaVersionIsRefused() {
        var result = Harness.Run(source: Sources.Unbranded(), manifestJson: "{ \"entries\": {} }");

        Assert.Equal(expected: new[] { "VER006" }, actual: result.Ids);
        Assert.Contains(expectedSubstring: "format", actualString: result.Single(id: "VER006").GetMessage());
    }
    [Fact]
    public void EntryNamingAnAlgorithmNothingImplementsIsRefused() {
        var source = Sources.BrandedMethod();

        var result = Harness.Run(
            source: source,
            manifestJson: Manifest.Of(new ManifestEntry {
                Algorithm = "not-a-real-algorithm",
                Id = Sources.TargetId,
                Sha256 = Harness.Fingerprint(source: source, id: Sources.TargetId),
                Symbol = Sources.TargetSymbol,
            }));

        Assert.Equal(expected: new[] { "VER006" }, actual: result.Ids);
        Assert.Contains(expectedSubstring: "not-a-real-algorithm", actualString: result.Single(id: "VER006").GetMessage());
    }
    [Fact]
    public void RecordedHashThatIsNotAHashIsReportedAsASchemaFailureNamingTheUnusableValue() {
        var source = Sources.BrandedMethod();

        var result = Harness.Run(
            source: source,
            manifestJson: Manifest.Of(new ManifestEntry {
                Id = Sources.TargetId,
                Sha256 = "not-hex",
                Symbol = Sources.TargetSymbol,
            }));

        Assert.Equal(expected: new[] { "VER006" }, actual: result.Ids);
        Assert.Contains(expectedSubstring: "not-hex", actualString: result.Single(id: "VER006").GetMessage());
    }
    [Fact]
    public void RecordedHashInUppercaseHexIsRefusedRatherThanReportedAsADrift() {
        var source = Sources.BrandedMethod();

        var result = Harness.Run(
            source: source,
            manifestJson: Manifest.Of(new ManifestEntry {
                Id = Sources.TargetId,
                Sha256 = Harness.Fingerprint(source: source, id: Sources.TargetId).ToUpperInvariant(),
                Symbol = Sources.TargetSymbol,
            }));

        // The analyzer only ever writes lowercase, so an uppercase record can never match; saying so on the ledger
        // is honest where a fingerprint drift would not be.
        Assert.Equal(expected: new[] { "VER006" }, actual: result.Ids);
    }
    [Fact]
    public void TwoFilesNamedLikeTheManifestAreRefusedRatherThanResolvedByOrder() {
        var source = Sources.BrandedMethod();
        var hash = Harness.Fingerprint(source: source, id: Sources.TargetId);

        var shadow = Manifest.Of(new ManifestEntry { Id = Sources.TargetId, Sha256 = hash, Symbol = "M:Subject.Assembly.Somewhere.Else" });
        var real = Manifest.Of(new ManifestEntry { Id = Sources.TargetId, Sha256 = new string(c: '0', count: 64), Symbol = Sources.TargetSymbol });

        var result = Harness.Analyze(
            compilation: Harness.Compile(assemblyName: Harness.DefaultAssemblyName, sources: new SourceFile(Name: "Subject.cs", Text: source)),
            additionalFiles: [
                new HarnessAdditionalText(path: ShadowManifestPath, text: shadow),
                new HarnessAdditionalText(path: Harness.ManifestPath, text: real),
            ]);

        Assert.Equal(expected: new[] { "VER006" }, actual: result.Ids);
        Assert.Contains(expectedSubstring: "ambiguous", actualString: result.Single(id: "VER006").GetMessage());
    }
    [Fact]
    public void ManifestFileNameIsMatchedWithoutRegardToCase() {
        var source = Sources.BrandedMethod();

        var result = Harness.Analyze(
            compilation: Harness.Compile(assemblyName: Harness.DefaultAssemblyName, sources: new SourceFile(Name: "Subject.cs", Text: source)),
            additionalFiles: new HarnessAdditionalText(
                path: Path.Combine(path1: Path.GetTempPath(), path2: "verifiedcode.JSON"),
                text: Manifest.Of(new ManifestEntry {
                    Id = Sources.TargetId,
                    Sha256 = Harness.Fingerprint(source: source, id: Sources.TargetId),
                    Symbol = Sources.TargetSymbol,
                })));

        Assert.Empty(collection: result.Ids);
    }
    [Fact]
    public void AdditionalFilesThatAreNotTheManifestAreIgnored() {
        var source = Sources.BrandedMethod();

        var result = Harness.Analyze(
            compilation: Harness.Compile(assemblyName: Harness.DefaultAssemblyName, sources: new SourceFile(Name: "Subject.cs", Text: source)),
            additionalFiles: [
                new HarnessAdditionalText(path: Path.Combine(path1: Path.GetTempPath(), path2: "CodeMetricsConfig.txt"), text: "not json"),
                new HarnessAdditionalText(
                    path: Harness.ManifestPath,
                    text: Manifest.Of(new ManifestEntry {
                        Id = Sources.TargetId,
                        Sha256 = Harness.Fingerprint(source: source, id: Sources.TargetId),
                        Symbol = Sources.TargetSymbol,
                    })),
            ]);

        Assert.Empty(collection: result.Ids);
    }
}
