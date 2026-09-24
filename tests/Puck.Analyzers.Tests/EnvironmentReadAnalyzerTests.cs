using Xunit;

namespace Puck.Analyzers.Tests;

/// <summary>
/// Exercises <see cref="EnvironmentReadAnalyzer"/> over small compilations: a Puck switch read from the environment
/// is refused, an allowlisted operating-system or CI value passes where its entry admits the reading assembly, and a
/// read the gate cannot name is refused.
/// </summary>
public sealed class EnvironmentReadAnalyzerTests {
    private const string Id = "ENV001";

    private static AnalysisResult Run(string body, string assemblyName = Harness.DefaultAssemblyName) {
        var result = Harness.Analyze(
            analyzer: new EnvironmentReadAnalyzer(),
            compilation: Harness.Compile(
                assemblyName: assemblyName,
                sources: new SourceFile(
                    Name: "Subject.cs",
                    Text: ("namespace Subject.Assembly;\n\n" + body)
                )
            )
        );

        Assert.True(
            condition: result.CompilesCleanly,
            userMessage: result.CompilerErrorText
        );

        return result;
    }

    [Fact]
    public void APuckSwitchIsRefused() {
        var result = Run(body: """
            public static class Subject {
                public static bool Debug => (System.Environment.GetEnvironmentVariable("PUCK_D3D12_DEBUG") is not null);
            }

            """);

        var diagnostic = result.Single(id: Id);

        Assert.Equal(
            actual: diagnostic.Severity,
            expected: Microsoft.CodeAnalysis.DiagnosticSeverity.Error
        );
        Assert.Contains(
            actualString: diagnostic.GetMessage(),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "reads 'PUCK_D3D12_DEBUG', which is not allowlisted for Subject.Assembly"
        );
    }
    [Fact]
    public void AnAllowlistedOperatingSystemReadPasses() {
        var result = Run(body: """
            public static class Subject {
                private const string SearchPath = "PATH";

                public static string? Path => System.Environment.GetEnvironmentVariable(SearchPath);
                public static string? PathForUser => System.Environment.GetEnvironmentVariable("PATH", System.EnvironmentVariableTarget.User);
                public static string Expanded => System.Environment.ExpandEnvironmentVariables("%PATH%");
            }

            """);

        Assert.Empty(collection: result.Analyzer);
    }
    [Fact]
    public void AnAllowlistedReadIsRefusedOutsideTheAssembliesItsEntryAdmits() {
        const string Body = """
            public static class Subject {
                public static string? Sha => System.Environment.GetEnvironmentVariable("GITHUB_SHA");
            }

            """;

        Assert.Empty(collection: Run(
            assemblyName: "Puck.Cli",
            body: Body
        ).Analyzer);
        Assert.Contains(
            actualString: Run(body: Body).Single(id: Id).GetMessage(),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "reads 'GITHUB_SHA', which is not allowlisted for Subject.Assembly"
        );
    }
    [Fact]
    public void ANameThatIsNotAConstantIsRefused() {
        var result = Run(body: """
            public static class Subject {
                public static string? Read(string name) => System.Environment.GetEnvironmentVariable(name);
                public static string Expand(string text) => System.Environment.ExpandEnvironmentVariables(text);
            }

            """);

        var messages = result.WithId(id: Id).Select(selector: static diagnostic => diagnostic.GetMessage()).ToArray();

        Assert.Equal(
            actual: messages.Length,
            expected: 2
        );
        Assert.Contains(
            collection: messages,
            filter: static message => message.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "reads a variable whose name is not a compile-time constant"
            )
        );
        Assert.Contains(
            collection: messages,
            filter: static message => message.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "expands a string that is not a compile-time constant"
            )
        );
    }
    [Fact]
    public void ReadingTheWholeEnvironmentIsRefused() {
        var result = Run(body: """
            public static class Subject {
                public static int Count => System.Environment.GetEnvironmentVariables().Count;
            }

            """);

        Assert.Contains(
            actualString: result.Single(id: Id).GetMessage(),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "reads every environment variable at once"
        );
    }
    [Fact]
    public void AnExpansionOfAnUnlistedVariableIsRefused() {
        var result = Run(body: """
            public static class Subject {
                public static string Expanded => System.Environment.ExpandEnvironmentVariables("%PATH%;%PUCK_SKY_PREVIEW_DIR%");
            }

            """);

        Assert.Contains(
            actualString: result.Single(id: Id).GetMessage(),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "expands '%PUCK_SKY_PREVIEW_DIR%'"
        );
    }
    [Fact]
    public void WritingAnEnvironmentIsNotARead() {
        var result = Run(body: """
            public static class Subject {
                public static void Write(System.Diagnostics.ProcessStartInfo info) {
                    info.Environment["PUCK_ANYTHING"] = "1";
                    System.Environment.SetEnvironmentVariable("PUCK_ANYTHING", null);
                }
            }

            """);

        Assert.Empty(collection: result.Analyzer);
    }
    [Fact]
    public void EveryAllowlistEntryStatesItsReason() {
        Assert.All(
            action: static entry => Assert.False(
                condition: string.IsNullOrWhiteSpace(value: entry.Value.Reason),
                userMessage: $"{entry.Key} has no reason"
            ),
            collection: EnvironmentReadAllowlist.Entries
        );
        Assert.DoesNotContain(
            collection: EnvironmentReadAllowlist.Entries.Keys,
            filter: static name => name.StartsWith(
                comparisonType: StringComparison.OrdinalIgnoreCase,
                value: "PUCK"
            )
        );
    }
}
