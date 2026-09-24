using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The language server reads a row's <c>: Enum</c> and a document value's bitwise operators as the compiler
/// does: hovering the enum a <c>slot</c>, <c>table</c> or <c>grid</c> names describes that enum, an enum no declaration
/// supplies is published as PUCK119 at the name itself by the full tier's semantic validation (the source tier, which
/// stops before it, publishes nothing for it), and a bitwise operator the document refuses is published with its code,
/// under the source tier and the full tier alike.</summary>
public sealed class EnumRowEditorLawTests {
    private const string Element = "  enum Element {\n    Nothing\n    Air\n  }\n";

    // Each row declaration spelling its enum after the name, as a document writes it.
    private static readonly string[] Spellings = [
        "slot left: Element = Air",
        "table recipe: Element {\n      a = Air\n    }",
        "grid field: Element dimensions(width: 2, depth: 2)",
    ];

    public static TheoryData<string> Declarations() => new(values: Spellings);

    private static string Document(string declaration) =>
        $"schema: \"puck.world.definition.v1\"\ndocumentId: \"enum-rows\"\n\nstate {{\n{Element}  world {{\n    {declaration}\n  }}\n}}\n";
    // The diagnostics the client shows once the server is done, from a session whose `initialize` asked for `tier`
    // (the full tier when null).
    private static async Task<JsonArray> PublishedAsync(string text, string? tier) {
        var transcript = await LanguageServerClient.ExchangeAsync(messages: [
            LanguageServerClient.Request(
                id: 1,
                method: "initialize",
                @params: ((tier is null)
                    ? []
                    : new JsonObject { ["initializationOptions"] = new JsonObject { ["diagnostics"] = tier } })
            ),
            LanguageServerClient.Open(text: text),
            LanguageServerClient.Request(id: 9999, method: "shutdown"),
            LanguageServerClient.Notification(method: "exit"),
        ]);

        return Assert.IsType<JsonArray>(@object: transcript.Notifications(method: "textDocument/publishDiagnostics").Last()["params"]?["diagnostics"]);
    }

    [MemberData(nameof(Declarations))]
    [Theory]
    public async Task HoveringTheEnumARowNamesDescribesThatEnum(string declaration) {
        var source = Document(declaration: declaration);
        var card = await LanguageServerClient.HoverAsync(markedSource: source.Insert(
            startIndex: (source.IndexOf(
                comparisonType: StringComparison.Ordinal,
                value: ": Element"
            ) + 2),
            value: "|"
        ));

        Assert.NotNull(@object: card);
        Assert.StartsWith(
            actualString: card,
            expectedStartString: "**`Element`** — enum"
        );
        Assert.Contains(
            actualString: card,
            expectedSubstring: "`Nothing`, `Air`"
        );
    }
    [MemberData(nameof(Declarations))]
    [Theory]
    public async Task AnUndeclaredEnumIsPublishedAtItsNameByTheFullTierAlone(string declaration) {
        // Only the name is wrong: the cells hold plain values an undeclared enum cannot refuse again.
        var source = Document(declaration: declaration.Replace(
            newValue: ": Elements",
            oldValue: ": Element"
        ).Replace(
            newValue: "1",
            oldValue: "Air"
        ));

        Assert.DoesNotContain(
            collection: await PublishedAsync(
                text: source,
                tier: "source"
            ),
            filter: static entry => (entry?["code"]?.ToString() == PuckDiagnosticCodes.StateEnumUndeclared)
        );

        var published = await PublishedAsync(
            text: source,
            tier: null
        );
        var diagnostic = Assert.Single(collection: published);
        var name = source.IndexOf(
            comparisonType: StringComparison.Ordinal,
            value: "Elements"
        );
        var line = source[..name].Count(predicate: static character => (character == '\n'));
        var character = (name - (source.LastIndexOf(
            startIndex: name,
            value: '\n'
        ) + 1));

        Assert.Equal(
            expected: PuckDiagnosticCodes.StateEnumUndeclared,
            actual: diagnostic?["code"]?.ToString()
        );
        Assert.Equal(
            expected: 1,
            actual: diagnostic?["severity"]?.GetValue<int>()
        );
        Assert.Equal(
            expected: new JsonObject {
                ["start"] = new JsonObject { ["line"] = line, ["character"] = character },
                ["end"] = new JsonObject { ["line"] = line, ["character"] = (character + "Elements".Length) },
            }.ToJsonString(),
            actual: diagnostic?["range"]?.ToJsonString()
        );
    }
    [InlineData("~1.5", null)]
    [InlineData("~1.5", "source")]
    [InlineData("1.5 << 2", null)]
    [InlineData("1.5 << 2", "source")]
    [InlineData("1 >>> 0.5", null)]
    [InlineData("1 >>> 0.5", "source")]
    [Theory]
    public async Task ABitwiseOperatorTheDocumentRefusesIsPublishedOnItsLineUnderEitherTier(string value, string? tier) {
        var published = await PublishedAsync(
            text: $"schema: \"puck.world.definition.v1\"\ndocumentId: \"bitwise\"\n\nlet folded = {value}\nstate {{\n  world {{\n    slot held = 0 bounds(0..folded)\n  }}\n}}\n",
            tier: tier
        );
        var diagnostic = Assert.Single(
            collection: published,
            predicate: static entry => (entry?["range"]?["start"]?["line"]?.GetValue<int>() == 3)
        );

        Assert.Equal(
            expected: PuckDiagnosticCodes.InvalidValue,
            actual: diagnostic?["code"]?.ToString()
        );
        Assert.Equal(
            expected: 1,
            actual: diagnostic?["severity"]?.GetValue<int>()
        );
    }
}
