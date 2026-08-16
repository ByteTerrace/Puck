using Xunit;

namespace Puck.Analyzers.Tests;

/// <summary>
/// What the recorded fingerprint covers and what it leaves out. Two questions run through these cases: does an edit
/// that changes behaviour always change the hash, and does an edit that changes only the brand's own metadata leave
/// the hash alone. The second matters as much as the first — a hash that moves when nothing behavioural moved
/// teaches people to re-record brands without re-reading them.
/// </summary>
public sealed class FingerprintTests {
    private static string HashOf(string source) =>
        Harness.Fingerprint(source: source, id: Sources.TargetId);

    private const string ForeignAttribute = """
        namespace Evil {
            [AttributeUsage(validOn: AttributeTargets.Method, AllowMultiple = false)]
            internal sealed class VerifiedCodeAttribute : Attribute {
                public VerifiedCodeAttribute(string payload) {
                    Payload = payload;
                }

                public string Payload { get; }
            }
        }

        """;

    /// <summary>The ledger's own bit-mix entry, so a case varies the source rather than the record.</summary>
    private static ManifestEntry BitMixEntry() =>
        new() {
            Assembly = Sources.BitMixAssemblyName,
            Dependencies = Sources.BitMixDependencies,
            Id = Sources.BitMixId,
            Sha256 = Sources.BitMixRecordedHash,
            Symbol = Sources.BitMixSymbol,
        };

    [Fact]
    public void AlgorithmStillProducesTheHashTheCommittedLedgerRecords() {
        var hash = Harness.Fingerprint(
            source: Sources.BitMix(),
            id: Sources.BitMixId,
            assemblyName: Sources.BitMixAssemblyName,
            symbol: Sources.BitMixSymbol,
            dependencies: Sources.BitMixDependencies);

        // Pins csharp-tokens-v1 itself against the value the committed ledger carries: the algorithm frames each
        // token's kind and byte count as little-endian Int32s, so this hash is the same on every architecture.
        Assert.Equal(actual: hash, expected: Sources.BitMixRecordedHash);
    }
    [Fact]
    public void ChangingAConstantTheBrandedBodyDependsOnBreaksTheRecordedHash() {
        // The multiplier the proof rests on being odd becomes even. It is declared outside the branded method, so
        // only the entry's declared dependencies bring it under the seal — and they do.
        var result = Harness.Run(
            source: Sources.BitMix(firstMultiplier: "2U"),
            manifestJson: Manifest.Of(BitMixEntry()),
            assemblyName: Sources.BitMixAssemblyName);

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        Assert.Equal(expected: new[] { "VER001" }, actual: result.Ids);
    }
    [Fact]
    public void EditingTheFileAroundABrandAndItsDependenciesKeepsTheRecordedHash() {
        // The other half of the same contract: a seal that moved for any edit anywhere would teach people to
        // re-record brands without re-reading them.
        var result = Harness.Run(
            source: Sources.BitMix(extraMembers: """

                public static uint Unrelated(uint value) {
                    return (value + 1U);
                }
            """),
            manifestJson: Manifest.Of(BitMixEntry()),
            assemblyName: Sources.BitMixAssemblyName);

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        Assert.Empty(collection: result.Ids);
    }
    [Fact]
    public void DependencyOrderInTheLedgerDoesNotChangeTheHash() {
        var reversed = Sources.BitMixDependencies.Reverse().ToArray();

        Assert.Equal(
            expected: Harness.Fingerprint(source: Sources.BitMix(), id: Sources.BitMixId, assemblyName: Sources.BitMixAssemblyName, symbol: Sources.BitMixSymbol, dependencies: Sources.BitMixDependencies),
            actual: Harness.Fingerprint(source: Sources.BitMix(), id: Sources.BitMixId, assemblyName: Sources.BitMixAssemblyName, symbol: Sources.BitMixSymbol, dependencies: reversed));
    }
    [Fact]
    public void DroppingADependencyFromTheLedgerChangesTheHash() {
        var narrowed = Sources.BitMixDependencies.Where(predicate: dependency => !dependency.EndsWith(comparisonType: StringComparison.Ordinal, value: ".FirstMultiplier")).ToArray();

        // The declared ids are inside the seal, so narrowing what a brand claims to rest on is itself a drift —
        // otherwise the list could be emptied to make a subverted constant pass again.
        Assert.NotEqual(
            expected: Harness.Fingerprint(source: Sources.BitMix(), id: Sources.BitMixId, assemblyName: Sources.BitMixAssemblyName, symbol: Sources.BitMixSymbol, dependencies: Sources.BitMixDependencies),
            actual: Harness.Fingerprint(source: Sources.BitMix(), id: Sources.BitMixId, assemblyName: Sources.BitMixAssemblyName, symbol: Sources.BitMixSymbol, dependencies: narrowed));
    }
    [Fact]
    public void ChangingADependencysTypeChangesTheHash() {
        static string Constant(string type) =>
            $$"""
            namespace Subject.Assembly;

            internal static class Subject {
                public const {{type}} Scale = 16;

                [VerifiedCode("target")]
                public static long Target(long value) {
                    return (value * Scale);
                }
            }

            """;

        // A field's declaring syntax is only its declarator, which carries no type at all; the walk has to reach
        // the field declaration it shares for `const int` and `const long` to be different declarations.
        Assert.NotEqual(
            expected: DependentHashOf(source: Constant(type: "int"), dependencies: ["F:Subject.Assembly.Subject.Scale"]),
            actual: DependentHashOf(source: Constant(type: "long"), dependencies: ["F:Subject.Assembly.Subject.Scale"]));
    }
    [Fact]
    public void EditingAConstantSharingOneFieldDeclarationWithADependencyDoesNotChangeTheHash() {
        static string Shared(string sibling) =>
            $$"""
            namespace Subject.Assembly;

            internal static class Subject {
                public const int Scale = 16, Unrelated = {{sibling}};

                [VerifiedCode("target")]
                public static long Target(long value) {
                    return (value * Scale);
                }
            }

            """;

        // Several constants can share one field declaration. The dependency is the one declarator plus the type and
        // modifiers it shares, never its neighbours — a sibling edit has never moved a fingerprint here.
        Assert.Equal(
            expected: DependentHashOf(source: Shared(sibling: "1"), dependencies: ["F:Subject.Assembly.Subject.Scale"]),
            actual: DependentHashOf(source: Shared(sibling: "2"), dependencies: ["F:Subject.Assembly.Subject.Scale"]));
    }
    [Fact]
    public void ChangingTheRepresentationARecordStructsOperatorRestsOnChangesTheHash() {
        static string Representation(string carrier) =>
            $$"""
            namespace Subject.Assembly;

            internal readonly record struct Subject({{carrier}} Value) {
                [VerifiedCode("target")]
                public static Subject operator +(Subject x, Subject y) =>
                    new(Value: unchecked((x.Value + y.Value)));
            }

            """;

        // The additive operators' whole argument is that the carrier encodes linearly. Naming the positional
        // property brings the carrier's width under the seal even though the operator body never spells it.
        Assert.NotEqual(
            expected: DependentHashOf(source: Representation(carrier: "int"), dependencies: ["P:Subject.Assembly.Subject.Value"]),
            actual: DependentHashOf(source: Representation(carrier: "long"), dependencies: ["P:Subject.Assembly.Subject.Value"]));
    }

    private static string DependentHashOf(string source, IReadOnlyList<string> dependencies) =>
        Harness.Fingerprint(source: source, id: Sources.TargetId, symbol: Sources.TargetSymbol, dependencies: dependencies);

    [Fact]
    public void EditingTheBrandedBodyChangesTheHash() =>
        Assert.NotEqual(expected: HashOf(source: Sources.BrandedMethod()), actual: HashOf(source: Sources.BrandedMethod(body: "        return 2;")));
    [Fact]
    public void RenamingAParameterChangesTheHash() {
        var before = Sources.InType(members: """
            [VerifiedCode("target")]
            public static int Target(int value) {
                return value;
            }
        """);

        var after = Sources.InType(members: """
            [VerifiedCode("target")]
            public static int Target(int amount) {
                return amount;
            }
        """);

        Assert.NotEqual(expected: HashOf(source: before), actual: HashOf(source: after));
    }
    [Fact]
    public void CommentsAndWhitespaceDoNotChangeTheHash() {
        var commented = Sources.InType(members: """

            /// <summary>Documented after the fact.</summary>
            [VerifiedCode("target")]
            public static int Target() {
                // An explanatory comment.
                return 1;
            }

        """);

        Assert.Equal(expected: HashOf(source: Sources.BrandedMethod()), actual: HashOf(source: commented));
    }
    [Fact]
    public void EditingASiblingMemberDoesNotChangeTheHash() {
        var withSibling = Sources.InType(members: """
            public static int Unrelated() {
                return 99;
            }

            [VerifiedCode("target")]
            public static int Target() {
                return 1;
            }
        """);

        Assert.Equal(expected: HashOf(source: Sources.BrandedMethod()), actual: HashOf(source: withSibling));
    }
    [Fact]
    public void EditingTheBrandsOwnBasisDoesNotChangeAMethodsHash() =>
        Assert.Equal(
            expected: HashOf(source: Sources.BrandedMethod(attribute: "[VerifiedCode(\"target\", Basis = \"exhaustive\")]")),
            actual: HashOf(source: Sources.BrandedMethod(attribute: "[VerifiedCode(\"target\", Basis = \"exact-by-proof\")]")));
    [Fact]
    public void EditingTheBrandsOwnLawsDoesNotChangeAMethodsHash() =>
        Assert.Equal(
            expected: HashOf(source: Sources.BrandedMethod(attribute: "[VerifiedCode(\"target\", Laws = \"one\")]")),
            actual: HashOf(source: Sources.BrandedMethod(attribute: "[VerifiedCode(\"target\", Laws = \"one, two\")]")));
    [Fact]
    public void EditingTheBrandsOwnBasisDoesNotChangeAConstructorsHash() {
        static string Constructor(string basis) =>
            $$"""
            namespace Subject.Assembly;

            internal sealed class Subject {
                [VerifiedCode("target", Basis = "{{basis}}")]
                public Subject() {
                }
            }

            """;

        Assert.Equal(expected: HashOf(source: Constructor(basis: "exhaustive")), actual: HashOf(source: Constructor(basis: "exact-by-proof")));
    }
    [Fact]
    public void EditingTheBrandsOwnBasisDoesNotChangeAnOperatorsHash() {
        static string Operator(string basis) =>
            $$"""
            namespace Subject.Assembly;

            internal readonly struct Subject {
                [VerifiedCode("target", Basis = "{{basis}}")]
                public static Subject operator +(Subject left, Subject right) {
                    return left;
                }
            }

            """;

        Assert.Equal(expected: HashOf(source: Operator(basis: "exhaustive")), actual: HashOf(source: Operator(basis: "exact-by-proof")));
    }
    [Fact]
    public void EditingTheBrandsOwnBasisDoesNotChangeAConversionOperatorsHash() {
        static string Conversion(string basis) =>
            $$"""
            namespace Subject.Assembly;

            internal readonly struct Subject {
                [VerifiedCode("target", Basis = "{{basis}}")]
                public static explicit operator long(Subject value) {
                    return 0L;
                }
            }

            """;

        Assert.Equal(expected: HashOf(source: Conversion(basis: "exhaustive")), actual: HashOf(source: Conversion(basis: "exact-by-proof")));
    }
    [Fact]
    public void EditingTheBrandsOwnBasisDoesNotChangeAClassesHash() {
        static string Class(string basis) =>
            $$"""
            namespace Subject.Assembly;

            [VerifiedCode("target", Basis = "{{basis}}")]
            internal sealed class Subject {
            }

            """;

        Assert.Equal(expected: HashOf(source: Class(basis: "exhaustive")), actual: HashOf(source: Class(basis: "exact-by-proof")));
    }
    [Fact]
    public void EditingTheBrandsOwnBasisDoesNotChangeAStructsHash() {
        static string Struct(string basis) =>
            $$"""
            namespace Subject.Assembly;

            [VerifiedCode("target", Basis = "{{basis}}")]
            internal readonly struct Subject {
            }

            """;

        Assert.Equal(expected: HashOf(source: Struct(basis: "exhaustive")), actual: HashOf(source: Struct(basis: "exact-by-proof")));
    }
    [Fact]
    public void EditingTheBrandsOwnBasisDoesNotChangeARecordsHash() {
        static string Record(string basis) =>
            $$"""
            namespace Subject.Assembly;

            [VerifiedCode("target", Basis = "{{basis}}")]
            internal sealed record Subject(int Value);

            """;

        Assert.Equal(expected: HashOf(source: Record(basis: "exhaustive")), actual: HashOf(source: Record(basis: "exact-by-proof")));
    }
    [Fact]
    public void EditingTheBrandsOwnBasisDoesNotChangeARecordsPrimaryConstructorHash() {
        static string Record(string basis) =>
            $$"""
            namespace Subject.Assembly;

            [method: VerifiedCode("target", Basis = "{{basis}}")]
            internal sealed record Subject(int Value);

            """;

        Assert.Equal(expected: HashOf(source: Record(basis: "exhaustive")), actual: HashOf(source: Record(basis: "exact-by-proof")));
    }
    [Fact]
    public void EditingTheBrandsOwnBasisDoesNotChangeTheHashWhenTheBrandIsAliased() =>
        Assert.Equal(expected: HashOf(source: Aliased(basis: "exhaustive")), actual: HashOf(source: Aliased(basis: "exact-by-proof")));
    [Fact]
    public void EditingAnAliasedBrandsBasisDemandsNoReVerificationWhenTheLedgerAgrees() {
        var result = Harness.Run(
            source: Aliased(basis: "exact-by-proof"),
            manifestJson: Manifest.Of(new ManifestEntry {
                Basis = ["exact-by-proof"],
                Id = Sources.TargetId,
                Sha256 = HashOf(source: Aliased(basis: "exhaustive")),
                Symbol = Sources.TargetSymbol,
            }));

        Assert.Empty(collection: result.Ids);
    }
    [Fact]
    public void EditingABrandedGettersBasisDemandsNoReVerificationWhenTheLedgerAgrees() {
        var result = Harness.Run(
            source: BrandedGetter(basis: "exact-by-proof"),
            manifestJson: Manifest.Of(new ManifestEntry {
                Basis = ["exact-by-proof"],
                Id = Sources.TargetId,
                Sha256 = HashOf(source: BrandedGetter(basis: "exhaustive")),
                Symbol = "M:Subject.Assembly.Subject.get_Target",
            }));

        Assert.Empty(collection: result.Ids);
    }

    private static string Aliased(string basis) =>
        $$"""
        using VC = Puck.VerifiedCodeAttribute;

        namespace Subject.Assembly;

        internal static class Subject {
            [VC("target", Basis = "{{basis}}")]
            public static int Target() {
                return 1;
            }
        }

        """;
    private static string BrandedGetter(string basis) =>
        $$"""
        namespace Subject.Assembly;

        internal static class Subject {
            public static int Target {
                [VerifiedCode("target", Basis = "{{basis}}")]
                get => 1;
            }
        }

        """;

    [Fact]
    public void EditingTheBrandsOwnBasisDoesNotChangeAnAccessorsHash() =>
        Assert.Equal(expected: HashOf(source: BrandedGetter(basis: "exhaustive")), actual: HashOf(source: BrandedGetter(basis: "exact-by-proof")));
    [Fact]
    public void EditingTheBrandsOwnBasisDoesNotChangeADestructorsHash() {
        static string Destructor(string basis) =>
            $$"""
            namespace Subject.Assembly;

            internal sealed class Subject {
                [VerifiedCode("target", Basis = "{{basis}}")]
                ~Subject() {
                }
            }

            """;

        Assert.Equal(expected: HashOf(source: Destructor(basis: "exhaustive")), actual: HashOf(source: Destructor(basis: "exact-by-proof")));
    }
    [Fact]
    public void EditingAnUnrelatedAttributeThatSharesTheBrandsNameChangesTheHash() {
        static string WithForeignAttribute(string payload) =>
            (ForeignAttribute + $$"""
            namespace Subject.Assembly {
                internal static class Subject {
                    [VerifiedCode("target"), Evil.VerifiedCode("{{payload}}")]
                    public static int Target() {
                        return 1;
                    }
                }
            }

            """);

        // Only the attribute the analyzer actually resolved is excluded, so a foreign attribute's payload — which a
        // generator or reflection may act on — stays sealed under the brand.
        Assert.NotEqual(expected: HashOf(source: WithForeignAttribute(payload: "payload-a")), actual: HashOf(source: WithForeignAttribute(payload: "payload-b")));
    }
    [Fact]
    public void EditingAnUnrelatedAttributeWithADifferentNameDoesChangeTheHash() =>
        Assert.NotEqual(
            expected: HashOf(source: Sources.BrandedMethod(attribute: "[Obsolete(\"a\"), VerifiedCode(\"target\")]")),
            actual: HashOf(source: Sources.BrandedMethod(attribute: "[Obsolete(\"b\"), VerifiedCode(\"target\")]")));
    [Fact]
    public void RenamingTheBrandIdLeavesTheOldEntryUnclaimedAndTheNewOneUnrecorded() {
        var result = Harness.Run(
            source: Sources.BrandedMethod(attribute: "[VerifiedCode(\"renamed\")]"),
            manifestJson: Manifest.Of(new ManifestEntry {
                Id = Sources.TargetId,
                Sha256 = HashOf(source: Sources.BrandedMethod()),
                Symbol = Sources.TargetSymbol,
            }));

        Assert.Equal(expected: new[] { "VER001", "VER002" }, actual: result.Ids);
    }
    [Fact]
    public void RemovingTheBrandWithoutRemovingItsEntryLeavesTheEntryUnclaimed() {
        var result = Harness.Run(
            source: Sources.Unbranded(),
            manifestJson: Manifest.Of(new ManifestEntry {
                Id = Sources.TargetId,
                Sha256 = HashOf(source: Sources.BrandedMethod()),
                Symbol = Sources.TargetSymbol,
            }));

        Assert.Equal(expected: new[] { "VER002" }, actual: result.Ids);
    }
}
