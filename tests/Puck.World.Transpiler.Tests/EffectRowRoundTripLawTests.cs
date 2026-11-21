using System.Text.Json.Nodes;
using Puck.Transpiler.Formatting;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Decompiler;
using Puck.World.Transpiler.Lowering;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The round-trip laws of the effects that name a whole row or a pool (<c>push</c>, <c>draw</c>,
/// <c>deal</c>, <c>shuffle</c>, <c>claim</c>, <c>for each</c>) and of a member that spells a construct body's
/// keyword: what the formatter prints reads back as the same tree, and what the decompiler prints compiles back to
/// the same document, whatever name the row carries.</summary>
public class EffectRowRoundTripLawTests {
    private const string State = """
        schema: "puck.world.definition.v1"
        documentId: "law"

        state {
          record Player {
            score: Int
          }
          pool players of Player capacity(2)
          world {
            row {
              name: "cards"
              kind: "Int"
              capacity: 4
              cells: map(range(0, 4), i => { key: $"{i}", value: i })
            }
            pile deck of cards capacity(4) {
              "0"
              "1"
            }
            pile hand of cards capacity(4)
            row {
              name: "odd-name"
              kind: "Int"
              domain: ring(capacity: 4, empty: 0)
            }
            row {
              name: "stream"
              kind: "Int"
              draw {
                generator {
                  source: "StreamDraw"
                }
                timing: "Event"
              }
            }
            slot go = 0
          }
        }

        let count = 1

        """;

    private static JsonObject Compile(string source) {
        var compilation = WorldCompileSession.Shared.Compile(cancellationToken: TestContext.Current.CancellationToken, source: source);

        Assert.False(condition: compilation.Diagnostics.HasErrors, userMessage: compilation.Diagnostics.FormatReport(source));

        return compilation.RequireJson();
    }
    private static string Format(string source) {
        var parsed = PuckParser.ParseDocumentWithDiagnostics(source: source, vocabulary: WorldDocumentVocabulary.Instance);

        Assert.False(condition: parsed.Diagnostics.HasErrors, userMessage: parsed.Diagnostics.FormatReport(source));

        return PuckPrinter.Print(document: PuckSyntaxTrivia.Attach(document: parsed.Value!, source: source));
    }

    /// <summary>Gets the rule bodies the laws run over, each naming its rows a different way.</summary>
    public static TheoryData<string> Bodies => [
        "draw deck to hand\n  deal 2 from deck to hand\n  shuffle hand with stream\n  push hand = 1",
        "deal count * 2 from deck to hand",
        "push `odd-name` = 2",
        "push \"odd-name\" = 3",
        "claim players as seated {\n    seated.score = 1\n  }\n  for each member in players {\n    member.score = 0\n  }",
    ];

    [MemberData(memberName: nameof(Bodies))]
    [Theory]
    public void WhatTheFormatterPrintsReadsBackAndIsAFixedPoint(string body) {
        var source = $"{State}rule \"r\" {{\n  when go == 1\n  {body}\n}}\n";
        var printed = Format(source: source);

        Assert.Equal(actual: Format(source: printed), expected: printed);
        Assert.Equal(actual: Compile(source: printed).ToJsonString(), expected: Compile(source: source).ToJsonString());
    }
    [MemberData(memberName: nameof(Bodies))]
    [Theory]
    public void WhatTheDecompilerPrintsCompilesBackToTheSameDocument(string body) {
        var document = Compile(source: $"{State}rule \"r\" {{\n  when go == 1\n  {body}\n}}\n");
        var decompiled = WorldDecompiler.Decompile(root: document);

        Assert.Equal(actual: Compile(source: decompiled).ToJsonString(), expected: document.ToJsonString());
    }
    // A member spelling a keyword a rule body opens a statement with is read as the member, bare before its colon or
    // backquoted, and prints in the one spelling that reads back.
    [InlineData("release: 2")]
    [InlineData("`release`: 2")]
    [InlineData("`claim`: 1")]
    [InlineData("`score`: 1")]
    [Theory]
    public void AMemberSpellingABodyKeywordPrintsAsItReadsBack(string member) {
        var printed = Format(source: $"{State}rule \"r\" {{\n  when go == 1\n  {member}\n  go = 2\n}}\n");

        Assert.Contains(actualString: printed, comparisonType: StringComparison.Ordinal, expectedSubstring: $"`{member.Split(separator: ':')[0].Trim(trimChar: '`')}`: ");
        Assert.Equal(actual: Format(source: printed), expected: printed);
    }
}
