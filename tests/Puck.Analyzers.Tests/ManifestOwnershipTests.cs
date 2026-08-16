using Xunit;

namespace Puck.Analyzers.Tests;

/// <summary>
/// Whether a manifest entry identifies one declaration. The ledger's promise is that each entry records a proof of
/// one member; these cases ask what ties an entry to the member it was written for, and which compilation is held
/// responsible for an entry nothing claims.
/// </summary>
public sealed class ManifestOwnershipTests {
    [Fact]
    public void BrandedDeclarationMovedToAnotherTypeNoLongerSatisfiesTheEntryItLeft() {
        var moved = Sources.InType(
            members: """
                [VerifiedCode("target")]
                public static int Target() {
                    return 1;
                }
            """,
            typeName: "SomewhereElse");

        var result = Harness.Run(
            source: moved,
            manifestJson: Manifest.Of(new ManifestEntry {
                Id = Sources.TargetId,
                Sha256 = Harness.Fingerprint(source: Sources.BrandedMethod(), id: Sources.TargetId),
                Symbol = Sources.TargetSymbol,
            }));

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);

        // The tokens are identical, so the recorded fingerprint still matches; it is the recorded symbol that
        // no longer does.
        Assert.Equal(expected: new[] { "VER008" }, actual: result.Ids);
        Assert.Contains(expectedSubstring: "SomewhereElse", actualString: result.Single(id: "VER008").GetMessage());
    }
    [Fact]
    public void MovingABrandedTypeLeavesItsDeclaredDependenciesNamingNothing() {
        var result = Harness.Run(
            source: Sources.BitMix(typeName: "SomewhereElse"),
            manifestJson: Manifest.Of(new ManifestEntry {
                Assembly = Sources.BitMixAssemblyName,
                Dependencies = Sources.BitMixDependencies,
                Id = Sources.BitMixId,
                Sha256 = Sources.BitMixRecordedHash,
                Symbol = Sources.BitMixSymbol,
            }),
            assemblyName: Sources.BitMixAssemblyName);

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);

        // The constants the entry names moved with the type, so the entry names declarations this compilation does
        // not have. Refusing is the only honest answer: folding them as nothing would leave the brand covering
        // less than it says it does. Nothing downstream of the refusal is judged — there is no fingerprint to
        // compare and no reason to believe the entry describes this declaration at all.
        Assert.Equal(expected: new[] { "VER010" }, actual: result.Ids);
        Assert.Contains(expectedSubstring: "InvertibleBitMix.FirstMultiplier", actualString: result.Single(id: "VER010").GetMessage());
    }
    [Fact]
    public void TwoDeclarationsClaimingOneEntryAreBothRefused() {
        var source = """
            namespace Subject.Assembly;

            internal static class Alpha {
                [VerifiedCode("target")]
                public static int Target() {
                    return 1;
                }
            }

            internal static class Beta {
                [VerifiedCode("target")]
                public static int Target() {
                    return 1;
                }
            }

            """;

        var hash = Harness.Fingerprint(source: Sources.BrandedMethod(), id: Sources.TargetId);

        var result = Harness.Run(
            source: source,
            manifestJson: Manifest.Of(new ManifestEntry {
                Id = Sources.TargetId,
                Sha256 = hash,
                Symbol = "M:Subject.Assembly.Alpha.Target",
            }));

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);

        // The duplicate claim is named as its own failure, and Beta is named for claiming an entry recorded
        // for Alpha.
        Assert.Equal(expected: new[] { "VER008", "VER009" }, actual: result.Ids);
        Assert.Contains(expectedSubstring: "Beta.Target", actualString: result.Single(id: "VER009").GetMessage());
        Assert.Contains(expectedSubstring: "Alpha.Target", actualString: result.Single(id: "VER009").GetMessage());
    }
    [Fact]
    public void SecondClaimantWithDifferentTokensIsReportedAsADuplicateClaimAsWellAsADrift() {
        var source = """
            namespace Subject.Assembly;

            internal static class Alpha {
                [VerifiedCode("target")]
                public static int Target() {
                    return 1;
                }
            }

            internal static class Beta {
                [VerifiedCode("target")]
                public static int Target() {
                    return 2;
                }
            }

            """;

        var result = Harness.Run(
            source: source,
            manifestJson: Manifest.Of(new ManifestEntry {
                Id = Sources.TargetId,
                Sha256 = Harness.Fingerprint(source: Sources.BrandedMethod(), id: Sources.TargetId),
                Symbol = "M:Subject.Assembly.Alpha.Target",
            }));

        Assert.Equal(expected: new[] { "VER001", "VER008", "VER009" }, actual: result.Ids);
        Assert.Contains(expectedSubstring: "Beta.Target", actualString: result.Single(id: "VER001").GetMessage());
    }
    [Fact]
    public void EntryWhoseRecordedSymbolNamesAnAssemblyOtherThanItsOwnerIsRefused() {
        var result = Harness.Run(
            source: Sources.Unbranded(),
            manifestJson: Manifest.Of(new ManifestEntry {
                Id = "deleted-brand",
                Sha256 = new string(c: '0', count: 64),
                Symbol = "M:Nonexistent.Assembly.Subject.Gone",
            }));

        // Recording an owner is what makes an entry sweepable at all; an entry whose recorded symbol names some
        // other assembly would otherwise match no compilation and be swept by none.
        Assert.Equal(expected: new[] { "VER006" }, actual: result.Ids);
        Assert.Contains(expectedSubstring: "Nonexistent.Assembly", actualString: result.Single(id: "VER006").GetMessage());
    }
    [Fact]
    public void EntryRecordingNoSymbolIsRefused() {
        var result = Harness.Run(
            source: Sources.Unbranded(),
            manifestJson: Manifest.Of(new ManifestEntry {
                Id = "deleted-brand",
                Sha256 = new string(c: '0', count: 64),
                Symbol = "",
            }));

        Assert.Equal(expected: new[] { "VER006" }, actual: result.Ids);
        Assert.Contains(expectedSubstring: "documentation-comment id", actualString: result.Single(id: "VER006").GetMessage());
    }
    [Fact]
    public void EntryRecordingNoOwningAssemblyIsRefused() {
        var result = Harness.Run(
            source: Sources.Unbranded(),
            manifestJson: Manifest.Of(new ManifestEntry {
                Assembly = "",
                Id = "deleted-brand",
                Sha256 = new string(c: '0', count: 64),
                Symbol = "M:Subject.Assembly.Subject.Gone",
            }));

        Assert.Equal(expected: new[] { "VER006" }, actual: result.Ids);
    }
    [Fact]
    public void UnclaimedEntryBelongingToAnotherAssemblyIsLeftToThatAssemblysSweep() {
        var result = Harness.Run(
            source: Sources.Unbranded(),
            manifestJson: Manifest.Of(new ManifestEntry {
                Assembly = Sources.BitMixAssemblyName,
                Id = "someone-elses-brand",
                Sha256 = new string(c: '0', count: 64),
                Symbol = Sources.BitMixSymbol,
            }));

        // Narrowing to the owning compilation is deliberate: every other project would otherwise report every entry.
        Assert.Empty(collection: result.Ids);
    }
    [Fact]
    public void EntryClaimedByItsOwnAssemblyIsSweptThere() {
        var result = Harness.Run(
            source: Sources.BitMix(),
            manifestJson: Manifest.Of(new ManifestEntry {
                Assembly = Sources.BitMixAssemblyName,
                Dependencies = Sources.BitMixDependencies,
                Id = Sources.BitMixId,
                Sha256 = Sources.BitMixRecordedHash,
                Symbol = Sources.BitMixSymbol,
            }),
            assemblyName: Sources.BitMixAssemblyName);

        Assert.Empty(collection: result.Ids);
    }
    [Fact]
    public void CompilationWithoutTheMarkerAttributeStillSweepsTheEntriesItOwns() {
        var source = """
            namespace Subject.Assembly;

            internal static class Subject {
                public static int Target() {
                    return 1;
                }
            }

            """;

        var result = Harness.Analyze(
            compilation: Harness.CompileWithoutBrandAttribute(assemblyName: Harness.DefaultAssemblyName, sources: new SourceFile(Name: "Subject.cs", Text: source)),
            additionalFiles: new HarnessAdditionalText(
                path: Harness.ManifestPath,
                text: Manifest.Of(new ManifestEntry {
                    Id = "deleted-brand",
                    Sha256 = new string(c: '0', count: 64),
                    Symbol = "M:Subject.Assembly.Subject.Gone",
                })));

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);

        // A project that cannot resolve the marker attribute carries no brands the compiler will hand the analyzer,
        // but the entries it owns still record proofs, and their deletion is still its to report.
        Assert.Equal(expected: new[] { "VER002" }, actual: result.Ids);
    }
}
