using Microsoft.CodeAnalysis;
using Xunit;

namespace Puck.Analyzers.Tests;

/// <summary>
/// Exercises <see cref="GpuTraitAnalyzer"/> over small compilations: a test class that reaches a way onto the GPU
/// without <c>[Trait("Category", "Gpu")]</c> is refused, however the reach is spelled and however many helpers it
/// passes through, and the trait, or the mark on a helper, admits it. The fixtures declare the marker the way
/// <c>build/OpensGpuDeviceAttribute.cs</c> does and reference the real xUnit <c>TraitAttribute</c>.
/// </summary>
public sealed class GpuTraitAnalyzerTests {
    private const string Id = "GPU001";
    private const string Marker = """
        namespace Puck {
            [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Constructor | System.AttributeTargets.Method | System.AttributeTargets.Property)]
            internal sealed class OpensGpuDeviceAttribute : System.Attribute;
        }

        namespace Native {
            [Puck.OpensGpuDevice]
            public sealed class NativeDeviceApi;
            public sealed class SoftwareDeviceApi;
            public static class Devices {
                [Puck.OpensGpuDevice]
                public static object Open() => new object();
                [Puck.OpensGpuDevice]
                public static object Default => new object();
                public static object Software() => new object();
            }
        }

        """;

    private static AnalysisResult Run(string body, IEnumerable<MetadataReference>? references = null) {
        var compilation = Harness.Compile(
            assemblyName: Harness.DefaultAssemblyName,
            sources: new SourceFile(
                Name: "Subject.cs",
                Text: (((((Marker + "namespace Subject.Assembly {\nusing Native;\n") + ((references is null) ? "using Xunit;\n" : string.Empty)) + "\n") + body) + "\n}\n")
            )
        );

        if (references is not null) {
            compilation = compilation.WithReferences(references: references);
        }

        var result = Harness.Analyze(
            analyzer: new GpuTraitAnalyzer(),
            compilation: compilation
        );

        Assert.True(
            condition: result.CompilesCleanly,
            userMessage: result.CompilerErrorText
        );

        return result;
    }

    [Fact]
    public void ATestClassConstructingAMarkedTypeWithoutTheTraitIsRefused() {
        var result = Run(body: """
            public sealed class SubjectTests {
                [Fact]
                public void Opens() => _ = new NativeDeviceApi();
            }
            """);

        var diagnostic = result.Single(id: Id);

        Assert.Equal(
            actual: diagnostic.Severity,
            expected: DiagnosticSeverity.Error
        );
        Assert.Contains(
            actualString: diagnostic.GetMessage(),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "'SubjectTests.Opens()' reaches the GPU through 'NativeDeviceApi'"
        );
    }
    [Fact]
    public void TheGpuTraitAdmitsTheClassItsNestedTypesAndItsLambdas() {
        var result = Run(body: """
            [Trait("Category", "Gpu")]
            public sealed partial class SubjectTests {
                private readonly System.Func<object> m_open = () => Devices.Open();

                [Fact]
                public void Opens() => _ = new NativeDeviceApi();

                private sealed class Fixture {
                    public object Device { get; } = Devices.Default;
                }
            }
            public sealed partial class SubjectTests {
                [Fact]
                public void OpensInAnotherPart() {
                    NativeDeviceApi api = new();
                    _ = api;
                }
            }
            """);

        Assert.Empty(collection: result.Analyzer);
    }
    [Fact]
    public void EverySpellingOfAReachIsRefused() {
        var result = Run(body: """
            public sealed class SubjectTests {
                [Fact]
                public void TargetTyped() {
                    NativeDeviceApi api = new();
                    _ = api;
                }
                [Fact]
                public void Called() => _ = Devices.Open();
                [Fact]
                public void Read() => _ = Devices.Default;
                [Fact]
                public void Grouped() {
                    System.Func<object> open = Devices.Open;
                    _ = open;
                }
            }
            """);

        Assert.Equal(
            actual: result.WithId(id: Id).Length,
            expected: 4
        );
    }
    [Fact]
    public void AHelperCarriesTheMarkAndHandsTheObligationToItsCallers() {
        const string Helper = """
            public static class TestDevices {
                [Puck.OpensGpuDevice]
                public static object Hardware() => new NativeDeviceApi();
                public static object Warp() => new SoftwareDeviceApi();
            }

            """;

        var refused = Run(body: (Helper + """
            public sealed class SubjectTests {
                [Fact]
                public void Opens() => _ = TestDevices.Hardware();
                [Fact]
                public void RunsOnTheCpu() => _ = TestDevices.Warp();
            }
            """));
        var admitted = Run(body: (Helper + """
            [Trait("Category", "Gpu")]
            public sealed class SubjectTests {
                [Fact]
                public void Opens() => _ = TestDevices.Hardware();
            }
            """));

        Assert.Contains(
            actualString: refused.Single(id: Id).GetMessage(),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "through 'TestDevices.Hardware()'"
        );
        Assert.Empty(collection: admitted.Analyzer);
    }
    [Fact]
    public void AnUnmarkedHelperThatReachesTheGpuIsRefused() {
        var result = Run(body: """
            public static class TestDevices {
                public static object Hardware() => new NativeDeviceApi();
            }
            """);

        Assert.Contains(
            actualString: result.Single(id: Id).GetMessage(),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "mark 'TestDevices.Hardware()' [OpensGpuDevice]"
        );
    }
    [Fact]
    public void AnotherCategoryDoesNotAdmitAReach() {
        var result = Run(body: """
            [Trait("Category", "Docker")]
            public sealed class SubjectTests {
                [Fact]
                public void Opens() => _ = new NativeDeviceApi();
            }
            """);

        _ = result.Single(id: Id);
    }
    [Fact]
    public void ACompilationWithoutXunitIsNotATestAssembly() {
        var result = Run(
            body: """
                public static class Host {
                    public static object Open() => new NativeDeviceApi();
                }
                """,
            references: Harness.RuntimeReferences.Where(predicate: static reference => !(System.IO.Path.GetFileName(path: reference.Display) ?? string.Empty).StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: "xunit"))
        );

        Assert.Empty(collection: result.Analyzer);
    }
}
