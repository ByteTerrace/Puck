using Xunit;

namespace Puck.Analyzers.Tests;

/// <summary>
/// Exercises <see cref="CodeDerivedCacheAnalyzer"/>: a raw weak table held in a field or auto-property is refused, a
/// table nested in another's type is seen, and the door type and a field marked the way
/// <c>build/SideTableAttribute.cs</c> marks one pass.
/// </summary>
public sealed class CodeDerivedCacheAnalyzerTests {
    private const string Marks = """
        using System.Runtime.CompilerServices;

        namespace Puck {
            [System.AttributeUsage(System.AttributeTargets.Field | System.AttributeTargets.Property)]
            internal sealed class SideTableAttribute(string reason) : System.Attribute { public string Reason { get; } = reason; }
        }

        namespace Puck.Abstractions {
            public sealed class CodeDerivedTable<TKey, TValue> where TKey : class where TValue : class? {
                private sealed class Generation { public ConditionalWeakTable<TKey, TValue> Values { get; } = new(); }
                private readonly ConditionalWeakTable<TKey, TValue> m_values = new();
            }
        }

        """;

    // The fixtures compile without xUnit: a compilation that references it is a test assembly, which the rule skips.
    private static AnalysisResult Run(string body) {
        var compilation = Harness.Compile(
            assemblyName: Harness.DefaultAssemblyName,
            sources: new SourceFile(Name: "Subject.cs", Text: (Marks + "namespace Subject {\n" + body + "\n}\n"))
        );
        var result = Harness.Analyze(
            analyzer: new CodeDerivedCacheAnalyzer(),
            compilation: compilation.WithReferences(references: Harness.RuntimeReferences.Where(predicate: static reference => !(reference.Display ?? string.Empty).Contains(value: "xunit", comparisonType: StringComparison.OrdinalIgnoreCase)))
        );

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        Assert.Empty(collection: result.WithId(id: "AD0001"));
        return result;
    }

    [Fact]
    public void ARawWeakTableFieldIsRefused() {
        var result = Run(body: """
            internal static class Compiled {
                private static readonly ConditionalWeakTable<string, object> Cache = new();
            }
            """);

        Assert.Contains(actualString: result.Single(id: "CACHE001").GetMessage(), comparisonType: StringComparison.Ordinal, expectedSubstring: "'Compiled.Cache'");
    }

    [Fact]
    public void AnAutoPropertyAndANestedTableAreRefused() {
        var result = Run(body: """
            internal sealed class Compiled {
                public ConditionalWeakTable<string, object> Held { get; } = new();
                private readonly Puck.Abstractions.CodeDerivedTable<string, ConditionalWeakTable<string, object>> m_nested = new();
                public ConditionalWeakTable<string, object> Served => Held;
            }
            """);

        Assert.Equal(expected: ["CACHE001", "CACHE001"], actual: result.Ids);
    }

    [Fact]
    public void TheDoorAndAMarkedSideTablePass() {
        var result = Run(body: """
            internal static class Attached {
                [Puck.SideTable(reason: "records the origin a reader noted")]
                private static readonly ConditionalWeakTable<string, object> Origins = new();
                private static readonly Puck.Abstractions.CodeDerivedTable<string, object> Derived = new();
            }
            """);

        Assert.Empty(collection: result.WithId(id: "CACHE001"));
    }
}
