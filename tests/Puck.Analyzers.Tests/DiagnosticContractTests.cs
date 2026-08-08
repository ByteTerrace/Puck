using Xunit;

namespace Puck.Analyzers.Tests;

/// <summary>
/// The diagnostics the analyzer advertises, each with the case that must raise it and the neighbouring case that
/// must not. These are the contract; everything else in this suite is about the ways the contract can be walked
/// around.
/// </summary>
public sealed class DiagnosticContractTests {
    [Fact]
    public void RecordedFingerprintThatStillMatchesReportsNothing() {
        var source = Sources.BrandedMethod();

        var result = Harness.Run(
            source: source,
            manifestJson: Manifest.Of(new ManifestEntry {
                Id = Sources.TargetId,
                Sha256 = Harness.Fingerprint(source: source, id: Sources.TargetId),
                Symbol = Sources.TargetSymbol,
            }));

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        Assert.Empty(collection: result.Ids);
    }

    [Fact]
    public void EditedBodyRaisesFingerprintMismatchCarryingTheRecomputedHash() {
        var edited = Sources.BrandedMethod(body: "        return 2;");
        var expected = Harness.Fingerprint(source: edited, id: Sources.TargetId);

        var result = Harness.Run(
            source: edited,
            manifestJson: Manifest.Of(new ManifestEntry {
                Id = Sources.TargetId,
                Sha256 = Harness.Fingerprint(source: Sources.BrandedMethod(), id: Sources.TargetId),
                Symbol = Sources.TargetSymbol,
            }));

        var diagnostic = result.Single(id: "VER001");

        Assert.Contains(expectedSubstring: $"(id '{Sources.TargetId}')", actualString: diagnostic.GetMessage());
        Assert.Contains(expectedSubstring: expected, actualString: diagnostic.GetMessage());
    }

    [Fact]
    public void BrandWithNoManifestEntryRaisesFingerprintMismatch() {
        var result = Harness.Run(source: Sources.BrandedMethod(), manifestJson: Manifest.Empty);

        Assert.Equal(expected: new[] { "VER001" }, actual: result.Ids);
    }

    [Fact]
    public void ManifestEntryNoBrandClaimsRaisesUnclaimedEntry() {
        var result = Harness.Run(
            source: Sources.Unbranded(),
            manifestJson: Manifest.Of(new ManifestEntry {
                Id = "deleted-brand",
                Sha256 = new string(c: '0', count: 64),
                Symbol = "M:Subject.Assembly.Subject.Gone",
            }));

        var diagnostic = result.Single(id: "VER002");

        Assert.Contains(expectedSubstring: "deleted-brand", actualString: diagnostic.GetMessage());
    }

    [Fact]
    public void ManifestEntryAnEncounteredBrandClaimsRaisesNoUnclaimedEntry() {
        var source = Sources.BrandedMethod();

        var result = Harness.Run(
            source: source,
            manifestJson: Manifest.Of(new ManifestEntry {
                Id = Sources.TargetId,
                Sha256 = Harness.Fingerprint(source: source, id: Sources.TargetId),
                Symbol = Sources.TargetSymbol,
            }));

        Assert.Empty(collection: result.WithId(id: "VER002"));
    }

    [Fact]
    public void PartialBrandedTypeIsRefusedRatherThanFingerprinted() {
        var source = """
            namespace Subject.Assembly;

            [VerifiedCode("target")]
            internal static partial class Subject {
                public static int Target() {
                    return 1;
                }
            }

            """;

        var result = Harness.Run(source: source, manifestJson: Manifest.Empty);

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        Assert.Equal(expected: new[] { "VER003" }, actual: result.Ids);
    }

    [Fact]
    public void BrandedDeclarationCarryingAPreprocessorDirectiveIsRefused() {
        var source = Sources.BrandedMethod(body: """
            #if NEVER
                    return 0;
            #else
                    return 1;
            #endif
            """);

        var result = Harness.Run(source: source, manifestJson: Manifest.Empty);

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        Assert.Equal(expected: new[] { "VER003" }, actual: result.Ids);
    }

    [Fact]
    public void OrdinaryBrandedDeclarationIsNotRefusedAsUnfingerprintable() {
        var result = Harness.Run(source: Sources.BrandedMethod(), manifestJson: Manifest.Empty);

        Assert.Empty(collection: result.WithId(id: "VER003"));
    }

    [Fact]
    public void AttributeBasisDisagreeingWithTheRecordedBasisRaisesBasisMismatch() {
        var source = Sources.BrandedMethod(attribute: "[VerifiedCode(\"target\", Basis = \"exhaustive\")]");

        var result = Harness.Run(
            source: source,
            manifestJson: Manifest.Of(new ManifestEntry {
                Basis = ["exact-by-proof"],
                Id = Sources.TargetId,
                Sha256 = Harness.Fingerprint(source: source, id: Sources.TargetId),
                Symbol = Sources.TargetSymbol,
            }));

        var diagnostic = result.Single(id: "VER004");

        Assert.Contains(expectedSubstring: "exhaustive", actualString: diagnostic.GetMessage());
        Assert.Contains(expectedSubstring: "exact-by-proof", actualString: diagnostic.GetMessage());
    }

    [Fact]
    public void AttributeBasisAgreeingUpToOrderAndSpacingRaisesNoBasisMismatch() {
        var source = Sources.BrandedMethod(attribute: "[VerifiedCode(\"target\", Basis = \" exhaustive ,exact-by-proof \")]");

        var result = Harness.Run(
            source: source,
            manifestJson: Manifest.Of(new ManifestEntry {
                Basis = ["exact-by-proof", "exhaustive"],
                Id = Sources.TargetId,
                Sha256 = Harness.Fingerprint(source: source, id: Sources.TargetId),
                Symbol = Sources.TargetSymbol,
            }));

        Assert.Empty(collection: result.Ids);
    }

    [Fact]
    public void AttributeOmittingBasisRaisesNoBasisMismatch() {
        var source = Sources.BrandedMethod();

        var result = Harness.Run(
            source: source,
            manifestJson: Manifest.Of(new ManifestEntry {
                Basis = ["exact-by-proof", "exhaustive"],
                Id = Sources.TargetId,
                Sha256 = Harness.Fingerprint(source: source, id: Sources.TargetId),
                Symbol = Sources.TargetSymbol,
            }));

        Assert.Empty(collection: result.Ids);
    }

    [Fact]
    public void UnclaimedEntryNamingAnAssemblyThatDeclaresNoMatchingNamespaceRaisesConventionViolated() {
        var source = """
            namespace Elsewhere;

            internal static class Subject {
                public static int Target() {
                    return 1;
                }
            }

            """;

        var result = Harness.Run(
            source: source,
            manifestJson: Manifest.Of(new ManifestEntry {
                Id = "deleted-brand",
                Sha256 = new string(c: '0', count: 64),
                Symbol = "M:Subject.Assembly.Subject.Gone",
            }));

        var diagnostic = result.Single(id: "VER005");

        Assert.Contains(expectedSubstring: Harness.DefaultAssemblyName, actualString: diagnostic.GetMessage());
        Assert.Empty(collection: result.WithId(id: "VER002"));
    }

    [Fact]
    public void EntryNamingADependencyNothingDeclaresRaisesUnresolvableDependency() {
        var result = Harness.Run(
            source: Sources.BrandedMethod(),
            manifestJson: Manifest.Of(new ManifestEntry {
                Dependencies = ["F:Subject.Assembly.Subject.Nowhere"],
                Id = Sources.TargetId,
                Sha256 = new string(c: '0', count: 64),
                Symbol = Sources.TargetSymbol,
            }));

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);

        // Skipping the dependency would leave the entry claiming a reach the seal does not have, and the brand
        // would pass on a narrower hash than the one it advertises.
        Assert.Equal(expected: new[] { "VER010" }, actual: result.Ids);
        Assert.Contains(expectedSubstring: "Subject.Nowhere", actualString: result.Single(id: "VER010").GetMessage());
    }

    [Fact]
    public void EntryNamingADependencyInAPartialTypeIsRefusedRatherThanHalfWalked() {
        var source = """
            namespace Subject.Assembly;

            internal static partial class Shared {
            }

            internal static class Subject {
                [VerifiedCode("target")]
                public static int Target() {
                    return 1;
                }
            }

            """;

        var result = Harness.Run(
            source: source,
            manifestJson: Manifest.Of(new ManifestEntry {
                Dependencies = ["T:Subject.Assembly.Shared"],
                Id = Sources.TargetId,
                Sha256 = new string(c: '0', count: 64),
                Symbol = Sources.TargetSymbol,
            }));

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        Assert.Equal(expected: new[] { "VER010" }, actual: result.Ids);
        Assert.Contains(expectedSubstring: "partial", actualString: result.Single(id: "VER010").GetMessage());
    }

    [Fact]
    public void EntryRecordingADependencyThatIsNotADocumentationIdIsRefusedOnTheLedger() {
        var result = Harness.Run(
            source: Sources.BrandedMethod(),
            manifestJson: Manifest.Of(new ManifestEntry {
                Dependencies = ["Subject.Assembly.Subject.Scale"],
                Id = Sources.TargetId,
                Sha256 = new string(c: '0', count: 64),
                Symbol = Sources.TargetSymbol,
            }));

        Assert.Equal(expected: new[] { "VER006" }, actual: result.Ids);
        Assert.Contains(expectedSubstring: "documentation-comment id", actualString: result.Single(id: "VER006").GetMessage());
    }

    [Fact]
    public void EntryRecordingOneDependencyTwiceIsRefusedOnTheLedger() {
        var result = Harness.Run(
            source: Sources.BrandedMethod(),
            manifestJson: Manifest.Of(new ManifestEntry {
                Dependencies = ["F:Subject.Assembly.Subject.Scale", "F:Subject.Assembly.Subject.Scale"],
                Id = Sources.TargetId,
                Sha256 = new string(c: '0', count: 64),
                Symbol = Sources.TargetSymbol,
            }));

        Assert.Equal(expected: new[] { "VER006" }, actual: result.Ids);
        Assert.Contains(expectedSubstring: "more than once", actualString: result.Single(id: "VER006").GetMessage());
    }

    [Fact]
    public void EntryRecordingADependencyOutsideItsOwnAssemblyIsRefusedOnTheLedger() {
        var result = Harness.Run(
            source: Sources.BrandedMethod(),
            manifestJson: Manifest.Of(new ManifestEntry {
                Dependencies = ["T:System.Int64"],
                Id = Sources.TargetId,
                Sha256 = new string(c: '0', count: 64),
                Symbol = Sources.TargetSymbol,
            }));

        // One compilation sweeps one entry, and it can only walk its own source; a dependency it could never walk
        // is a ledger fault rather than a per-brand one.
        Assert.Equal(expected: new[] { "VER006" }, actual: result.Ids);
        Assert.Contains(expectedSubstring: "System.Int64", actualString: result.Single(id: "VER006").GetMessage());
    }

    [Fact]
    public void EntryDeclaringNoDependenciesIsNotRefused() {
        var source = Sources.BrandedMethod();

        var result = Harness.Run(
            source: source,
            manifestJson: Manifest.Of(new ManifestEntry {
                Id = Sources.TargetId,
                Sha256 = Harness.Fingerprint(source: source, id: Sources.TargetId),
                Symbol = Sources.TargetSymbol,
            }));

        // A body that reads nothing outside itself rests on nothing outside itself, and an empty list says so.
        Assert.Empty(collection: result.Ids);
    }

    [Fact]
    public void UnclaimedEntryInAnAssemblyThatDoesDeclareItsOwnNamespaceRaisesUnclaimedEntryNotConventionViolated() {
        var result = Harness.Run(
            source: Sources.Unbranded(),
            manifestJson: Manifest.Of(new ManifestEntry {
                Id = "deleted-brand",
                Sha256 = new string(c: '0', count: 64),
                Symbol = "M:Subject.Assembly.Subject.Gone",
            }));

        Assert.Equal(expected: new[] { "VER002" }, actual: result.Ids);
        Assert.Empty(collection: result.WithId(id: "VER005"));
    }
}
