using System.Collections;
using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Validation;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>CONTRACT UNDER TEST: the reference lint resolves a state row's name wherever
/// <see cref="WorldNameRegistry"/> says a document holds one. Each probe is built from a registered site's own
/// path, so a member registered later is held here without an edit.</summary>
public class RegisteredRowReferenceLawTests {
    private const string Missing = "missingRow";

    // One document holding `name` at `path` and nothing else, plus the rows `declared` lists.
    private static JsonObject Probe(string path, string name, bool asList, params string[] declared) {
        var root = new JsonObject {
            ["schema"] = WorldSemanticValidator.RootSchemaId,
        };
        var segments = path.Split(separator: '.');
        var holder = root;

        for (var index = 0; (index < segments.Length); index++) {
            var segment = segments[index];
            var bracket = segment.IndexOf(value: '[');
            var member = ((bracket < 0)
                ? segment
                : segment[..bracket]
            );
            var suffix = ((bracket < 0)
                ? string.Empty
                : segment[bracket..]
            );

            if (index == (segments.Length - 1)) {
                Assert.Equal(
                    string.Empty,
                    suffix
                );
                holder[member] = (asList
                    ? new JsonArray(name)
                    : name
                );

                break;
            }

            var next = ((holder[member] as JsonObject) ?? new JsonObject());
            var isElement = suffix.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "[]"
            );
            var arm = (isElement
                ? suffix[2..]
                : suffix
            );

            if (arm.Length > 0) {
                next["$type"] = arm[1..^1];
            }
            if (isElement) {
                holder[member] ??= new JsonArray(next);
                next = (JsonObject)((JsonArray)holder[member]!)[0]!;
            } else {
                holder[member] ??= next;
            }
            holder = next;
        }

        if (declared.Length > 0) {
            var state = ((root["state"] as JsonObject) ?? new JsonObject());
            var rows = ((state["world"] as JsonArray) ?? []);

            foreach (var row in declared) {
                rows.Add(value: new JsonObject {
                    ["name"] = row,
                });
            }
            state["world"] ??= rows;
            root["state"] ??= state;
        }

        return root;
    }
    private static List<Diagnostic> Unresolved(JsonObject document) {
        var diagnostics = new DiagnosticBag();

        PuckLinter.LintReferences(
            diagnostics: diagnostics,
            document: document,
            sourceMap: null,
            sourcePath: "law.puck"
        );

        return [.. diagnostics.Where(predicate: static diagnostic => string.Equals(
            a: diagnostic.Code,
            b: PuckDiagnosticCodes.LintUnresolvedState,
            comparisonType: StringComparison.Ordinal
        ))];
    }

    // A path through an expression's instruction list or a re-entered shape is a site of the instruction's own
    // member, which the infix probes of the lint's other laws reach through a real compile.
    public static TheoryData<string, string, bool> Sites() {
        var data = new TheoryData<string, string, bool>();
        var seen = new HashSet<(Type, string)>();

        foreach (var site in WorldNameRegistry.Sites) {
            if (
                (site.Field.Role != WorldNameRole.Names) ||
                (site.Field.Kind is not (WorldNameKind.State or WorldNameKind.Zone)) ||
                site.Path.Contains(value: '{') ||
                site.Path.Contains(value: '…') ||
                !seen.Add(item: (site.Field.Owner, site.Field.Member))
            ) {
                continue;
            }

            var type = site.Field.Owner.GetProperty(name: site.Field.Member)!.PropertyType;

            data.Add(
                site.Path,
                $"{site.Field.Owner.Name}.{site.Field.Member}",
                ((type != typeof(string)) && typeof(IEnumerable).IsAssignableFrom(c: type))
            );
        }

        return data;
    }

    [MemberData(nameof(Sites))]
    [Theory]
    public void ARowNameNothingDeclaresIsReportedAtEveryRegisteredSiteAndADeclaredOneIsNot(string path, string member, bool asList) {
        var refusal = Assert.Single(collection: Unresolved(document: Probe(
            asList: asList,
            name: Missing,
            path: path
        )));

        Assert.Contains(
            expectedSubstring: Missing,
            actualString: refusal.Message
        );
        Assert.True(
            condition: (Unresolved(document: Probe(
                path,
                Missing,
                asList,
                Missing
            )).Count == 0),
            userMessage: $"{member} at {path} reports a row the document declares"
        );
    }
    [Fact]
    public void ARegionInteractionsRightSideIsResolvedAsAPlacementNotARow() {
        JsonObject Region(string placementId) => new() {
            ["schema"] = WorldSemanticValidator.RootSchemaId,
            ["interactions"] = new JsonObject {
                ["interactions"] = new JsonArray(new JsonObject {
                    ["coOccurrence"] = nameof(WorldInteractionCoOccurrence.Region),
                    ["left"] = Missing,
                    ["right"] = "pool",
                }),
            },
            ["placements"] = new JsonObject {
                ["rows"] = new JsonArray(new JsonObject {
                    ["id"] = placementId,
                }),
            },
            ["state"] = new JsonObject {
                ["world"] = new JsonArray(new JsonObject {
                    ["name"] = Missing,
                }),
            },
        };
        List<Diagnostic> Findings(JsonObject document) {
            var diagnostics = new DiagnosticBag();

            PuckLinter.LintReferences(
                diagnostics: diagnostics,
                document: document,
                sourceMap: null,
                sourcePath: "law.puck"
            );

            return [.. diagnostics];
        }

        Assert.Empty(collection: Findings(document: Region(placementId: "pool")));

        var refusal = Assert.Single(collection: Findings(document: Region(placementId: "pond")));

        Assert.Equal(
            PuckDiagnosticCodes.LintUnresolvedPlacementParent,
            refusal.Code
        );
    }
    [Fact]
    public void TheRegistryListsSitesForTheLawToHold() =>
        Assert.NotEmpty(collection: Sites());
}
