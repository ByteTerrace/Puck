using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Validation;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>CONTRACT UNDER TEST: the language server publishes what <see cref="WorldSourceDiagnostics"/> reports and
/// nothing else, so a world the engine refuses is refused in the editor by the same code, text and line, whether
/// the buffer is a saved file or has never been saved.</summary>
public class EditorDiagnosticsLawTests {
    // Parses, lowers and lints clean; only the engine's own validation refuses it.
    private const string Refused = """
        schema: "puck.world.definition.v1"
        documentId: "refused"

        state {
          world {
            slot armour = 0
          }
        }

        rule "reads-a-cell-the-row-lacks" {
          when armour[plate] == 1
          mode: Edge
          armour = 2
        }
        """;

    private static List<PublishedDiagnostic> Reported(string source, string? sourcePath) =>
        [.. WorldSourceDiagnostics.Diagnose(
            source: source,
            sourcePath: sourcePath
        ).Select(selector: static diagnostic => new PublishedDiagnostic(
            Code: diagnostic.Code,
            Line: Math.Max(
                val1: 0,
                val2: (diagnostic.Span.Line - 1)
            ),
            Message: diagnostic.Message
        ))];

    [Fact]
    public async Task AWorldTheEngineRefusesIsRefusedInTheEditorByTheSameCodeTextAndLine() {
        var directory = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"puck-editor-law-{Guid.NewGuid():N}"
        );

        _ = Directory.CreateDirectory(path: directory);
        try {
            var path = Path.Combine(
                path1: directory,
                path2: "refused.puck"
            ).Replace(
                newChar: '/',
                oldChar: '\\'
            );

            await File.WriteAllTextAsync(
                cancellationToken: TestContext.Current.CancellationToken,
                contents: Refused,
                path: path
            );

            var saved = Reported(
                source: Refused,
                sourcePath: path
            );
            var refusal = Assert.Single(collection: saved);

            Assert.Equal(
                actual: refusal.Code,
                expected: PuckDiagnosticCodes.SemanticValidation
            );
            Assert.Equal(
                actual: refusal.Line,
                expected: 10
            );
            Assert.Equal(
                saved,
                await LanguageServerClient.PublishedAsync(
                    text: Refused,
                    uri: new Uri(uriString: path).AbsoluteUri
                )
            );
            Assert.Equal(
                saved,
                await LanguageServerClient.PublishedAsync(
                    text: Refused,
                    uri: "untitled:refused"
                )
            );
        } finally {
            Directory.Delete(
                path: directory,
                recursive: true
            );
        }
    }
}
