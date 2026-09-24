using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Parsing;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The <c>.puck</c> door for placement ids: an id a channel could not name — one carrying <c>:</c>, or one
/// exactly <c>$each</c> — is refused with PUCK116, once, whether it names a <c>placements</c> row (reported on the
/// <c>placement</c> line) or the placement an <c>upsertPlacement</c> effect writes (a rule written as an object in a
/// raw <c>rules</c> array maps to that object, so the report stands on the rule's own line). The control spells each with
/// <c>-</c> and compiles clean.</summary>
public sealed class PlacementIdLawTests {
    private static string Placements(string id) => $"placements {{\n    placement {PuckStrings.Write(value: id)} {{\n        prototypeId: \"p\"\n    }}\n}}\n";
    private static string Upsert(string id) => $"state {{\n    world {{\n        slot flag = 0\n    }}\n}}\n\nrules [\n    {{\n        name: \"grow\"\n        effects [\n            {{\n                \"$type\": \"upsertPlacement\"\n                placement {{\n                    id: {PuckStrings.Write(value: id)}\n                    prototypeId: \"p\"\n                }}\n            }}\n        ]\n    }}\n]\n";

    private static readonly Dictionary<string, Refusal> Refusals = new(comparer: StringComparer.Ordinal) {
        ["a placements row id carrying a colon"] = new(Body: Placements(id: "a:b"), Code: PuckDiagnosticCodes.PlacementIdReserved, Needle: "placement \"a:b\"") { Mentions = "carries ':'" },
        ["a placements row id spelling the each token"] = new(Body: Placements(id: "$each"), Code: PuckDiagnosticCodes.PlacementIdReserved, Needle: "placement \"$each\"") { Mentions = "placement:$each" },
        ["an upserted placement id carrying a colon"] = new(Body: Upsert(id: "a:b"), Code: PuckDiagnosticCodes.PlacementIdReserved, Needle: "    {\n        name: \"grow\"") { Mentions = "carries ':'" },
        ["an upserted placement id spelling the each token"] = new(Body: Upsert(id: "$each"), Code: PuckDiagnosticCodes.PlacementIdReserved, Needle: "    {\n        name: \"grow\"") { Mentions = "placement:$each" },
    };

    public static TheoryData<string> RefusalNames() => new(values: Refusals.Keys);
    [MemberData(nameof(RefusalNames))]
    [Theory]
    public void APlacementIdAChannelCannotNameIsRefusedOnceAtItsOwnLine(string name) => WorldSources.AssertRefused(
        label: name,
        refusal: Refusals[name]
    );
    [InlineData("a-b")]
    [InlineData("each")]
    [Theory]
    public void TheSameIdsSpelledWithoutTheReservedFormsCompile(string id) {
        foreach (var body in new[] { Placements(id: id), Upsert(id: id) }) {
            var diagnostics = WorldSources.Compile(source: (WorldSources.Header + body)).Diagnostics;

            Assert.DoesNotContain(
                collection: diagnostics,
                filter: static diagnostic => (diagnostic.Code == PuckDiagnosticCodes.PlacementIdReserved)
            );
        }
    }
}
