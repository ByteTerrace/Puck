using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>A module used under an alias is an instance: every name it declares — a row, a rule, a placement, a
/// prototype — is the generated name <c>alias$name</c>, which no author-written name can spell, and the source that
/// uses it reads each one as <c>alias.name</c>. So a host name can never collide with an instance's, and two
/// instances of one module each read their own placements.</summary>
public sealed class ModuleNamespaceLawTests {
    private const string Gate = """
        module gate() {
          prototypes {
            prototype "post" {
              document {
                name: "post"
                schema: "puck.creation.v1"
              }
            }
          }
          placements {
            placement "door" {
              prototype: "post"
              position [0, 0, 0]
              region {
                radius: 2
              }
            }
          }
          state { world { slot inside = 0 } }
          rule "sense" {
            when region(door) >= 1
            inside = 1
          }
        }

        use gate as east()
        use gate as west()

        """;
    private const string Room = """
        module room() {
          state { world { slot floor = 2 } }
          export read floor
        }

        """;

    private static JsonObject Lower(string body) => WorldSources.LowerClean(body: body);
    private static string[] Names(JsonNode? rows, string member = "name") =>
        [.. ((rows as JsonArray) ?? []).Select(selector: row => row![member]!.GetValue<string>())];

    // The spelling an alias prefix once had, `a_floor`, is an ordinary name a host may declare: it stands beside the
    // instance's `a$floor` as a second row. The mutation proof: qualify with '_' and the two rows are one name.
    [Fact]
    public void AHostNameSpelledLikeTheAliasNeverCollidesWithTheInstances() {
        var json = Lower(body: (Room + "state { world { slot a_floor = 1 } }\n\nuse room as a()\n"));
        var rows = json["state"]!["world"]!.AsArray();

        Assert.Equal(expected: ["a_floor", "a$floor"], actual: Names(rows: rows));
        Assert.Equal(expected: [1L, 2L], actual: rows.Select(selector: static row => row!["value"]!.GetValue<long>()));
    }
    // The instance's own spelling is generated, so a host that writes it is refused by name where it declares it: the
    // module's rule 'grow' is the instance's 'a$grow', and the host's rule of that name is PUCK113.
    [Fact]
    public void AHostCannotDeclareAnInstancesName() => WorldSources.AssertRefused(
        label: "a host rule spelled as an instance's",
        refusal: new Refusal(
            Body: "module room() {\n  state { world { slot floor = 2 } }\n  rule \"grow\" {\n    when floor < 3\n    floor = 3\n  }\n}\n\nuse room as a()\n\nrule \"a$grow\" {\n  when a.floor < 3\n  a.floor = 3\n}\n",
            Code: PuckDiagnosticCodes.GeneratedNameReserved,
            Needle: "rule \"a$grow\" {"
        )
    );
    // `a.floor` reads the instance, so an alias spelled like a row or constant of the same scope would make it name two
    // things; the use is refused by name, and the control, the alias renamed, compiles.
    [InlineData("state {\n  world {\n    slot a = 1\n  }\n}\n\n")]
    [InlineData("let a = 1\n\n")]
    [Theory]
    public void AnAliasSpelledLikeTheScopesOwnNameIsRefusedAtItsUse(string declaration) {
        WorldSources.AssertRefused(
            label: "an alias spelled like the scope's own name",
            refusal: new Refusal(
                Body: ((Room + declaration) + "use room as a()\n"),
                Code: PuckDiagnosticCodes.ModuleAliasShadowsName,
                Needle: "use room as a()"
            ) { Alone = true }
        );
        _ = Lower(body: ((Room + declaration) + "use room as b()\n"));
    }
    // `left.score` names the instance's row wherever a name stands — a gate, an assignment's target, an expression,
    // a module argument, a re-export — written before its `use` as well as after it; a nested instance reads through
    // both aliases.
    [Fact]
    public void AQualifiedReferenceNamesTheInstancesRowInEveryPosition() {
        var json = Lower(body: """
            rule "early" {
              when left.score < 3
              left.score = left.score + right.score
            }

            module counter(start: Angle) {
              state { world { slot score = start } }
              export read score
              export action score
            }
            module guarded(condition: Gate) {
              state { world { slot fired = false } }
              rule "fire" {
                when condition == true
                fired = true
              }
            }
            module flag() {
              state { world { slot lit = true } }
              export read lit
            }
            module wrapper() {
              use counter as child(start: 7)
              export child.score
            }

            use counter as left(start: 0)
            use counter as right(start: 3)
            use flag as first()
            use guarded as second(condition: first.lit)
            use wrapper as box()

            rule "late" {
              when box.child.score > 0
              box.child.score = 0
            }
            """);
        var rules = json["rules"]!.AsArray();
        var early = rules.Single(predicate: static rule => (rule!["name"]!.GetValue<string>() == "early"))!;
        var late = rules.Single(predicate: static rule => (rule!["name"]!.GetValue<string>() == "late"))!;
        var fire = rules.Single(predicate: static rule => (rule!["name"]!.GetValue<string>() == "second$fire"))!;

        Assert.Equal(expected: ["left$score", "right$score", "first$lit", "second$fired", "box$child$score"], actual: Names(rows: json["state"]!["world"]));
        Assert.Equal(expected: "left$score", actual: early["gate"]!["state"]!.GetValue<string>());
        Assert.Equal(expected: "left$score", actual: early["effects"]![0]!["state"]!.GetValue<string>());
        Assert.Contains(expectedSubstring: "right$score", actualString: early["effects"]![0]!.ToJsonString(), comparisonType: StringComparison.Ordinal);
        Assert.Equal(expected: "first$lit", actual: fire["gate"]!["state"]!.GetValue<string>());
        Assert.Equal(expected: "box$child$score", actual: late["gate"]!["state"]!.GetValue<string>());
        Assert.Equal(expected: "box$child$score", actual: late["effects"]![0]!["state"]!.GetValue<string>());
        Assert.DoesNotContain(expectedSubstring: "left.score", actualString: json.ToJsonString(), comparisonType: StringComparison.Ordinal);
    }
    // Two instances of one module each stand the module's placement and prototype under their own alias, and each
    // instance's rule reads its own placement's region. The mutation proof: with placements outside the module
    // namespace, both instances declare 'door' and both rules read the one placement.
    [Fact]
    public void TwoInstancesEachReadTheirOwnPlacement() {
        var json = Lower(body: Gate);
        var placements = json["placements"]!["rows"]!;

        Assert.Equal(expected: ["east$door", "west$door"], actual: Names(member: "id", rows: placements));
        Assert.Equal(expected: ["east$post", "west$post"], actual: Names(member: "prototypeId", rows: placements));
        Assert.Equal(expected: ["east$post", "west$post"], actual: Names(member: "id", rows: json["prototypes"]));

        foreach (var alias in new[] { "east", "west" }) {
            var sense = json["rules"]!.AsArray().Single(predicate: rule => (rule!["name"]!.GetValue<string>() == $"{alias}$sense"))!;

            Assert.Contains(expectedSubstring: $"{alias}$door", actualString: sense["gate"]!.ToJsonString(), comparisonType: StringComparison.Ordinal);
            Assert.Equal(expected: $"{alias}$inside", actual: sense["effects"]![0]!["state"]!.GetValue<string>());
        }
    }
    // A placement and a state row live in separate namespaces: a module that declares the placement `score` still
    // reads its host's row `score`. The mutation proof: one namespace for every kind renames the read to 'a$score'.
    [Fact]
    public void AnInstancesPlacementNeverRenamesAHostRowOfTheSameName() {
        var json = Lower(body: """
            state { world { slot score = 5 } }

            module marker() {
              prototypes {
                prototype "post" {
                  document {
                    name: "post"
                    schema: "puck.creation.v1"
                  }
                }
              }
              placements {
                placement "score" {
                  prototype: "post"
                  position [0, 0, 0]
                }
              }
              state { world { slot seen = 0 } }
              rule "copy" {
                when score > 0
                seen = score
              }
            }

            use marker as a()
            """);
        var copy = json["rules"]!.AsArray().Single(predicate: static rule => (rule!["name"]!.GetValue<string>() == "a$copy"))!;

        Assert.Equal(expected: "a$score", actual: json["placements"]!["rows"]![0]!["id"]!.GetValue<string>());
        Assert.Equal(expected: "score", actual: copy["gate"]!["state"]!.GetValue<string>());
    }
    // A look's creation source names a prototype, so it follows the instance's prototypes into the namespace. The
    // mutation proof: without the look source's registry row the look still names the bare 'fin', which no row declares.
    [Fact]
    public void AnInstancesLookWearsItsOwnPrototype() {
        var json = Lower(body: """
            module swimmer() {
              prototypes {
                prototype "fin" {
                  document {
                    name: "fin"
                    schema: "puck.creation.v1"
                  }
                }
              }
              looks {
                rows [{
                  name: "fin", scale: 1, source: creation(prototypeId: fin)
                  motion { cues [], gaitAmplitude: 0, replayFrames: false, secondsPerFrame: 0 }
                }]
              }
            }

            use swimmer as a()
            """);

        Assert.Equal(expected: "a$fin", actual: json["prototypes"]![0]!["id"]!.GetValue<string>());
        Assert.Equal(expected: "a$fin", actual: json["looks"]!["rows"]![0]!["source"]!["prototypeId"]!.GetValue<string>());
    }
}
