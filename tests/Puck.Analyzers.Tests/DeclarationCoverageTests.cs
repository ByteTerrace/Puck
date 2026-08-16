using Xunit;

namespace Puck.Analyzers.Tests;

/// <summary>
/// Every place the marker attribute is legal, and every way its name can be spelled, checked against one question:
/// is the brand accounted for? Each case brands a declaration and offers an empty ledger, so a placement the
/// analyzer covers reports a fingerprint mismatch, a placement it cannot record is refused outright, and silence
/// is a failure — a brand nothing accounts for asserts a proof nothing checks.
/// </summary>
public sealed class DeclarationCoverageTests {
    private static void AssertBrandWasSeen(string source, string sourceName = "Subject.cs") {
        var result = Harness.Run(source: source, manifestJson: Manifest.Empty, sourceName: sourceName);

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        Assert.Equal(expected: new[] { "VER001" }, actual: result.Ids);
    }
    /// <summary>A placement the attribute is legal on but nothing can record: refused outright rather than left standing unenforced.</summary>
    private static void AssertBrandWasRefusedAsUnrecordable(string source, string placement) {
        var result = Harness.Run(source: source, manifestJson: Manifest.Empty);

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        Assert.Equal(expected: new[] { "VER007" }, actual: result.Ids);
        Assert.Contains(expectedSubstring: placement, actualString: result.Single(id: "VER007").GetMessage());
    }

    [Fact]
    public void ShorthandSpellingOnAMethodIsSeen() =>
        AssertBrandWasSeen(source: Sources.BrandedMethod());
    [Fact]
    public void NamespaceQualifiedSpellingOnAMethodIsSeen() =>
        AssertBrandWasSeen(source: Sources.BrandedMethod(attribute: "[Puck.VerifiedCode(\"target\")]"));
    [Fact]
    public void AttributeSuffixedSpellingOnAMethodIsSeen() =>
        AssertBrandWasSeen(source: Sources.BrandedMethod(attribute: "[VerifiedCodeAttribute(\"target\")]"));
    [Fact]
    public void FullyQualifiedAndSuffixedSpellingOnAMethodIsSeen() =>
        AssertBrandWasSeen(source: Sources.BrandedMethod(attribute: "[Puck.VerifiedCodeAttribute(\"target\")]"));
    [Fact]
    public void AliasedSpellingOnAMethodIsSeen() {
        var source = """
            using VC = Puck.VerifiedCodeAttribute;

            namespace Subject.Assembly;

            internal static class Subject {
                [VC("target")]
                public static int Target() {
                    return 1;
                }
            }

            """;

        AssertBrandWasSeen(source: source);
    }
    [Fact]
    public void BrandSharingAnAttributeListWithAnotherAttributeIsSeen() =>
        AssertBrandWasSeen(source: Sources.BrandedMethod(attribute: "[Obsolete(\"gone\"), VerifiedCode(\"target\")]"));
    [Fact]
    public void BrandInASecondAttributeListIsSeen() =>
        AssertBrandWasSeen(source: Sources.BrandedMethod(attribute: "[Obsolete(\"gone\")]\r\n    [VerifiedCode(\"target\")]"));
    [Fact]
    public void BrandOnAPropertyGetterIsSeen() {
        var source = """
            namespace Subject.Assembly;

            internal static class Subject {
                public static int Target {
                    [VerifiedCode("target")]
                    get => 1;
                }
            }

            """;

        AssertBrandWasSeen(source: source);
    }
    [Fact]
    public void BrandOnAPropertySetterIsSeen() {
        var source = """
            namespace Subject.Assembly;

            internal static class Subject {
                private static int m_value;

                public static int Target {
                    get => m_value;
                    [VerifiedCode("target")]
                    set => m_value = value;
                }
            }

            """;

        AssertBrandWasSeen(source: source);
    }
    [Fact]
    public void BrandOnAConstructorIsSeen() {
        var source = """
            namespace Subject.Assembly;

            internal sealed class Subject {
                [VerifiedCode("target")]
                public Subject() {
                }
            }

            """;

        AssertBrandWasSeen(source: source);
    }
    [Fact]
    public void BrandOnAnOperatorIsSeen() {
        var source = """
            namespace Subject.Assembly;

            internal readonly struct Subject {
                [VerifiedCode("target")]
                public static Subject operator +(Subject left, Subject right) {
                    return left;
                }
            }

            """;

        AssertBrandWasSeen(source: source);
    }
    [Fact]
    public void BrandOnAnImplicitConversionOperatorIsSeen() {
        var source = """
            namespace Subject.Assembly;

            internal readonly struct Subject {
                [VerifiedCode("target")]
                public static implicit operator int(Subject value) {
                    return 0;
                }
            }

            """;

        AssertBrandWasSeen(source: source);
    }
    [Fact]
    public void BrandOnAnExplicitConversionOperatorIsSeen() {
        var source = """
            namespace Subject.Assembly;

            internal readonly struct Subject {
                [VerifiedCode("target")]
                public static explicit operator long(Subject value) {
                    return 0L;
                }
            }

            """;

        AssertBrandWasSeen(source: source);
    }
    [Fact]
    public void BrandOnADestructorIsSeen() {
        var source = """
            namespace Subject.Assembly;

            internal sealed class Subject {
                [VerifiedCode("target")]
                ~Subject() {
                }
            }

            """;

        AssertBrandWasSeen(source: source);
    }
    [Fact]
    public void BrandOnAClassIsSeen() {
        var source = """
            namespace Subject.Assembly;

            [VerifiedCode("target")]
            internal sealed class Subject {
            }

            """;

        AssertBrandWasSeen(source: source);
    }
    [Fact]
    public void BrandOnAStructIsSeen() {
        var source = """
            namespace Subject.Assembly;

            [VerifiedCode("target")]
            internal readonly struct Subject {
            }

            """;

        AssertBrandWasSeen(source: source);
    }
    [Fact]
    public void BrandOnARecordClassIsSeen() {
        var source = """
            namespace Subject.Assembly;

            [VerifiedCode("target")]
            internal sealed record Subject(int Value);

            """;

        AssertBrandWasSeen(source: source);
    }
    [Fact]
    public void BrandOnARecordStructIsSeen() {
        var source = """
            namespace Subject.Assembly;

            [VerifiedCode("target")]
            internal readonly record struct Subject(int Value);

            """;

        AssertBrandWasSeen(source: source);
    }
    [Fact]
    public void BrandOnARecordsPrimaryConstructorIsSeen() {
        var source = """
            namespace Subject.Assembly;

            [method: VerifiedCode("target")]
            internal sealed record Subject(int Value);

            """;

        AssertBrandWasSeen(source: source);
    }
    [Fact]
    public void BrandOnAClassPrimaryConstructorIsSeen() {
        var source = """
            namespace Subject.Assembly;

            [method: VerifiedCode("target")]
            internal sealed class Subject(int value) {
                public int Value => value;
            }

            """;

        AssertBrandWasSeen(source: source);
    }
    [Fact]
    public void BrandOnAGenericMethodIsSeen() {
        var source = """
            namespace Subject.Assembly;

            internal static class Subject {
                [VerifiedCode("target")]
                public static TValue Target<TValue>(TValue value) {
                    return value;
                }
            }

            """;

        AssertBrandWasSeen(source: source);
    }
    [Fact]
    public void BrandOnAnExplicitInterfaceImplementationIsSeen() {
        var source = """
            namespace Subject.Assembly;

            internal interface ITarget {
                int Target();
            }

            internal sealed class Subject : ITarget {
                [VerifiedCode("target")]
                int ITarget.Target() {
                    return 1;
                }
            }

            """;

        AssertBrandWasSeen(source: source);
    }
    [Fact]
    public void BrandOnAMethodOfANestedTypeIsSeen() {
        var source = """
            namespace Subject.Assembly;

            internal static class Subject {
                internal static class Inner {
                    [VerifiedCode("target")]
                    public static int Target() {
                        return 1;
                    }
                }
            }

            """;

        AssertBrandWasSeen(source: source);
    }
    [Fact]
    public void BrandInAGeneratedFileIsSeen() =>
        AssertBrandWasSeen(source: Sources.BrandedMethod(), sourceName: "Subject.g.cs");
    [Fact]
    public void BrandInAFileCarryingTheAutoGeneratedHeaderIsSeen() {
        var source = """
            // <auto-generated/>
            namespace Subject.Assembly;

            internal static class Subject {
                [VerifiedCode("target")]
                public static int Target() {
                    return 1;
                }
            }

            """;

        AssertBrandWasSeen(source: source);
    }
    [Fact]
    public void BrandOnALocalFunctionIsRefused() {
        var source = """
            namespace Subject.Assembly;

            internal static class Subject {
                public static int Caller() {
                    [VerifiedCode("target")]
                    static int Target() {
                        return 1;
                    }

                    return Target();
                }
            }

            """;

        AssertBrandWasRefusedAsUnrecordable(placement: "a local function", source: source);
    }
    [Fact]
    public void BrandOnALambdaIsRefused() {
        var source = """
            namespace Subject.Assembly;

            internal static class Subject {
                public static Func<int> Caller() {
                    return [VerifiedCode("target")] () => 1;
                }
            }

            """;

        AssertBrandWasRefusedAsUnrecordable(placement: "a lambda", source: source);
    }
    [Fact]
    public void BrandOnAParameterisedLambdaIsRefused() {
        var source = """
            namespace Subject.Assembly;

            internal static class Subject {
                public static Func<int, int> Caller() {
                    return [VerifiedCode("target")] (int value) => value;
                }
            }

            """;

        AssertBrandWasRefusedAsUnrecordable(placement: "a lambda", source: source);
    }
    [Fact]
    public void BrandWithAnEmptyIdIsIgnoredRatherThanTracked() {
        var result = Harness.Run(source: Sources.BrandedMethod(attribute: "[VerifiedCode(\"\")]"), manifestJson: Manifest.Empty);

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        Assert.Empty(collection: result.Ids);
    }
}
