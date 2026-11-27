using System.Reflection;
using Puck.Assets.Documents;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Parsing;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The diagnostics an author reads: each declared code has an entry on the reference page, a failed statement's
/// leftover closing brace is not reported as a second error, and a message names what the author wrote beside what
/// was expected.</summary>
public sealed class DiagnosticMessageLawTests {
    private static string ReferencePage() {
        var directory = AppContext.BaseDirectory;

        while (directory is not null) {
            var candidate = Path.Combine(directory, "docs", "reference", "diagnostics.md");

            if (File.Exists(path: candidate)) {
                return File.ReadAllText(path: candidate);
            }
            directory = Path.GetDirectoryName(path: directory);
        }

        throw new FileNotFoundException(message: "Could not locate docs/reference/diagnostics.md from the test runner.");
    }

    [Fact]
    public void EveryDeclaredCodeHasAnEntryOnTheReferencePage() {
        var page = ReferencePage();
        var missing = typeof(PuckDiagnosticCodes)
            .GetFields(bindingAttr: BindingFlags.Public | BindingFlags.Static)
            .Where(predicate: static field => field.IsLiteral && (field.GetRawConstantValue() is string code) && code.StartsWith(value: "PUCK", comparisonType: StringComparison.Ordinal))
            .Select(selector: static field => (string)field.GetRawConstantValue()!)
            .Where(predicate: code => !page.Contains(value: $"\n### {code}\n", comparisonType: StringComparison.Ordinal))
            .ToArray();

        Assert.True(condition: (missing.Length == 0), userMessage: $"docs/reference/diagnostics.md has no entry for {string.Join(separator: ", ", values: missing)}");
    }
    [Fact]
    public void AHelpPathAnchorsIntoTheEntryOfItsCode() {
        Assert.Equal(expected: "docs/reference/diagnostics.md#puck016", actual: PuckDiagnosticCodes.HelpPath(code: "PUCK016"));
        Assert.Equal(expected: "docs/reference/diagnostics.md#puck_lint_001", actual: PuckDiagnosticCodes.HelpPath(code: "PUCK_LINT_001"));
    }
    [Fact]
    public void AStrayClosingBraceAfterAFailedStatementIsNotReportedAgain() {
        var diagnostics = new DiagnosticBag();

        _ = PuckParser.ParseDocumentWithDiagnostics(diagnostics: diagnostics, source: "x: $\"{unclosed\"\n}\n");

        var error = Assert.Single(collection: diagnostics.Where(predicate: static diagnostic => (diagnostic.Severity == DiagnosticSeverity.Error)));

        Assert.Contains(expectedSubstring: "no '}' closes", actualString: error.Message);
    }
    [Fact]
    public void AClosingBraceWithNoOpenBlockSaysHowToFixIt() {
        var diagnostics = new DiagnosticBag();

        _ = PuckParser.ParseDocumentWithDiagnostics(diagnostics: diagnostics, source: "let a = 1\n}\n");

        var error = Assert.Single(collection: diagnostics.Where(predicate: static diagnostic => (diagnostic.Severity == DiagnosticSeverity.Error)));

        Assert.Contains(expectedSubstring: "where no block is open", actualString: error.Message);
        Assert.Contains(expectedSubstring: "Remove it", actualString: error.Message);
    }
    [Fact]
    public void AnExpectedTokenFaultNamesWhatTheSourceHoldsThere() {
        var diagnostics = new DiagnosticBag();

        _ = PuckParser.ParseDocumentWithDiagnostics(diagnostics: diagnostics, source: "for i in range(0, 2) let x = 1\n");

        var error = diagnostics.First(predicate: static diagnostic => (diagnostic.Severity == DiagnosticSeverity.Error));

        Assert.Contains(expectedSubstring: ", but found '", actualString: error.Message);
    }
    [Fact]
    public void AReportListsTheReferencePageUnderEachDiagnostic() {
        var diagnostics = new DiagnosticBag();

        _ = PuckParser.ParseDocumentWithDiagnostics(diagnostics: diagnostics, source: "let a = 1\n}\n");

        Assert.Contains(expectedSubstring: "Learn more: docs/reference/diagnostics.md#puck001", actualString: diagnostics.FormatReport());
    }
    [Fact]
    public void ACapacityMessageNamesTheItemsThatUsedTheCapacity() {
        var message = DiagnosticText.Capacity(capacity: 2, fix: "Remove one", noun: "lanes", used: ["a", "b", "c"], what: "render.lanes");

        Assert.Equal(expected: "'render.lanes' holds at most 2 lanes, and 3 are written: 'a', 'b' and 'c'. Remove one.", actual: message);
    }
    [Fact]
    public void ALongListIsCountedPastItsLimit() {
        Assert.Equal(expected: "'a', 'b', 'c', 'd', 'e' and 2 more", actual: DiagnosticText.List(items: ["a", "b", "c", "d", "e", "f", "g"]));
    }
}
