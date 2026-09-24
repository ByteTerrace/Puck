using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>A compile-time <c>for</c> inside a rule body, a step body or an effect list unrolls into exactly the
/// statements an author would have written out by hand: the document carries the unrolled locals and effects and
/// never the loop, so each one is compiled and priced like any other.</summary>
public class RuleBodyForLawTests {
    private const string State = """

        state {
          world {
            grid board dimensions(width: 4, depth: 1) bounds(0..9)

            slot total = 0

            slot seen = 0
          }
        }

        """;

    // Each case is a loop and the same body written out by hand; both must lower to one document.
    private static readonly Dictionary<string, (string Looped, string Written)> Unrollings = new(comparer: StringComparer.Ordinal) {
        ["effects in a rule body"] = (
            Looped: """
                rule "fill" {
                  when total == 0
                  for i in range(0, 3) {
                    board[(i)] = i + 1
                  }
                  total = 1
                }
                """,
            Written: """
                rule "fill" {
                  when total == 0
                  board[(0)] = 0 + 1
                  board[(1)] = 1 + 1
                  board[(2)] = 2 + 1
                  total = 1
                }
                """
        ),
        ["locals named per iteration"] = (
            Looped: """
                rule "read" {
                  when total == 0
                  for (cell, i) in [3, 1] {
                    local $"at{i}" = board[(cell)] ?? 7
                  }
                  seen = at0 * 10 + at1
                }
                """,
            Written: """
                rule "read" {
                  when total == 0
                  local at0 = board[(3)] ?? 7
                  local at1 = board[(1)] ?? 7
                  seen = at0 * 10 + at1
                }
                """
        ),
        ["effects inside a branch"] = (
            Looped: """
                rule "branch" {
                  when total == 0
                  if seen == 0 {
                    for i in range(0, 2) {
                      if board[(i)] == 0 {
                        board[(i)] = 5
                      }
                    }
                  } else {
                    for i in [3] {
                      board[(i)] = 1
                    }
                  }
                }
                """,
            Written: """
                rule "branch" {
                  when total == 0
                  if seen == 0 {
                    if board[(0)] == 0 {
                      board[(0)] = 5
                    }
                    if board[(1)] == 0 {
                      board[(1)] = 5
                    }
                  } else {
                    board[(3)] = 1
                  }
                }
                """
        ),
        ["effects inside a transaction"] = (
            Looped: """
                rule "atomic" {
                  when total == 0
                  transaction {
                    for i in range(0, 2) {
                      board[(i)] = 9
                    }
                  } onFailure {
                    for i in [1] {
                      total = i
                    }
                  }
                }
                """,
            Written: """
                rule "atomic" {
                  when total == 0
                  transaction {
                    board[(0)] = 9
                    board[(1)] = 9
                  } onFailure {
                    total = 1
                  }
                }
                """
        ),
        ["nested loops"] = (
            Looped: """
                rule "pairs" {
                  when total == 0
                  for i in range(0, 2) {
                    for j in range(0, 2) {
                      board[(i * 2 + j)] = i + j
                    }
                  }
                }
                """,
            Written: """
                rule "pairs" {
                  when total == 0
                  board[(0 * 2 + 0)] = 0 + 0
                  board[(0 * 2 + 1)] = 0 + 1
                  board[(1 * 2 + 0)] = 1 + 0
                  board[(1 * 2 + 1)] = 1 + 1
                }
                """
        ),
        ["a workflow step's body"] = (
            Looped: """
                workflow turn {
                  step fill {
                    for i in range(0, 2) {
                      local $"was{i}" = board[(i)] ?? 0
                      board[(i)] = 4
                    }
                    seen = was0 + was1
                  }
                }
                """,
            Written: """
                workflow turn {
                  step fill {
                    local was0 = board[(0)] ?? 0
                    board[(0)] = 4
                    local was1 = board[(1)] ?? 0
                    board[(1)] = 4
                    seen = was0 + was1
                  }
                }
                """
        ),
    };

    public static TheoryData<string> UnrollingNames() => new(values: Unrollings.Keys);
    [MemberData(nameof(UnrollingNames))]
    [Theory]
    public void ALoopLowersToTheStatementsWrittenOut(string name) {
        var (looped, written) = Unrollings[name];
        var expected = WorldSources.LowerClean(body: (State + written));
        var actual = WorldSources.LowerClean(body: (State + looped));

        Assert.Null(@object: JsonMismatch.Find(
            actual: actual,
            expected: expected,
            path: "$"
        ));
    }
    [MemberData(nameof(UnrollingNames))]
    [Theory]
    public void ALoopInARuleFormatsStably(string name) => PuckFormat.AssertStable(source: ((WorldSources.Header + State) + Unrollings[name].Looped));
    [Fact]
    public void ALoopOverAnEmptySequenceProducesNothingAndStillCountsAsTheRuleBody() {
        var json = WorldSources.LowerClean(body: (State + """
            rule "none" {
              when total == 0
              for i in [] {
                total = i
              }
            }
            """));
        var rule = Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: json["rules"])[0]);

        Assert.Empty(collection: Assert.IsType<JsonArray>(@object: rule["effects"]));
    }

    // One mistake, one error, on the line that carries it.
    private static readonly Dictionary<string, Refusal> Refusals = new(comparer: StringComparer.Ordinal) {
        ["a gate repeated by a loop"] = new(
            Body: (State + "rule \"r\" {\n  for i in range(0, 2) {\n    when board[(i)] == 0\n    total = i\n  }\n}\n"),
            Code: PuckDiagnosticCodes.ForRepeatsARuleMember,
            Needle: "when board[(i)] == 0"
        ),
        ["a rule stamped inside a step's loop"] = new(
            Body: (State + "workflow turn {\n  step fill {\n    for i in range(0, 2) {\n      rule \"inner\" {\n        total = i\n      }\n    }\n  }\n}\n"),
            Code: PuckDiagnosticCodes.ForRepeatsARuleMember,
            Needle: "rule \"inner\""
        ),
        ["a sequence read from state"] = new(
            Body: (State + "rule \"r\" {\n  when total == 0\n  for i in total {\n    seen = i\n  }\n}\n"),
            Code: PuckDiagnosticCodes.ForSequenceRefused,
            Needle: "total {"
        ),
        ["a rule property repeated by a loop"] = new(
            Body: (State + "rule \"r\" {\n  when total == 0\n  for i in range(0, 2) {\n    mode: Edge\n    seen = i\n  }\n}\n"),
            Code: PuckDiagnosticCodes.ForAssignsAField,
            Needle: "mode: Edge"
        ),
    };

    public static TheoryData<string> RefusalNames() => new(values: Refusals.Keys);
    [MemberData(nameof(RefusalNames))]
    [Theory]
    public void AnInadmissibleLoopIsRefusedOnceAtItsOwnLine(string name) => WorldSources.AssertRefused(
        label: name,
        refusal: Refusals[name]
    );
}
