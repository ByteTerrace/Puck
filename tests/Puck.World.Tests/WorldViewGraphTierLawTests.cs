using System.Text;
using Xunit;

using Puck.Abstractions.Presentation;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// Laws for a <c>views.graphs</c> row's quality tier: a tier selects how a pass computes and never what it reads, so two
/// documents differing only in a row's tier compile identical presentation manifests, fill identical state mirrors and
/// hash equal state; a tier outside the authored vocabulary is refused by name; and a package row takes no tier.
/// </summary>
public sealed class WorldViewGraphTierLawTests {
    private const string LevelRow = "tierLevel";

    // The fixture document with one Int row the graph binds, and one graph row at the given tier.
    private static WorldDefinition Document(QualityTier? tier) {
        var document = Fixtures.BuildDocument();
        var state = (document.StateRaw ?? new WorldStateSection());

        return (document with {
            StateRaw = (state with {
                World = [.. (state.World ?? []), new WorldStateRow(
                    Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value: 3))],
                    Kind: CellKind.Int,
                    Max: 10,
                    Min: 0,
                    Name: CellName.Parse(candidate: LevelRow)
                )],
            }),
            ViewsRaw = (document.Views with {
                Graphs = [new WorldViewGraph(
                    Name: "board",
                    Parameters: new Dictionary<string, IReadOnlyDictionary<string, BindableScalar>>(comparer: StringComparer.Ordinal) {
                        ["draw"] = new Dictionary<string, BindableScalar>(comparer: StringComparer.Ordinal) {
                            ["level"] = new BindableScalar(binding: $"state.{LevelRow}"),
                        },
                    },
                    Source: "graphs/board.graph.json",
                    Tier: tier
                )],
            }),
        });
    }
    // Every slot a mirror over the document fills once it installs, as (binding, conversion, value).
    private static (StateBinding, WorldStateConversion, double)[] Mirror(WorldDefinition definition) {
        var mirror = new WorldStateMirror(view: new WorldDocumentStateView(definition: () => definition));

        mirror.Install(
            engineTick: 0UL,
            tick: 0UL
        );

        return [.. mirror.Manifest.Bindings.ToArray().Select(selector: binding => (
            binding.Binding,
            binding.Conversion,
            (mirror.TryValue(
                slot: mirror.SlotOf(
                    binding: binding.Binding,
                    conversion: binding.Conversion
                ),
                value: out var value
            )
                ? value
                : double.NaN)
        ))];
    }

    [Fact]
    public void TwoDocumentsDifferingOnlyInTierPresentTheSameStateAndHashEqually() {
        var untiered = Document(tier: null);
        var low = Document(tier: QualityTier.Low);
        var high = Document(tier: QualityTier.High);

        foreach (var definition in ((WorldDefinition[])[untiered, low, high])) {
            Assert.True(
                condition: WorldDefinitionValidator.TryValidateLocally(
                    definition: definition,
                    reason: out var reason
                ),
                userMessage: reason
            );
        }

        var manifest = WorldPresentationManifest.Compile(definition: untiered);

        Assert.Contains(
            collection: manifest.Bindings.ToArray(),
            filter: static binding => (binding.Binding == new StateBinding(Key: null, Row: LevelRow, Target: false))
        );

        var mirror = Mirror(definition: untiered);

        Assert.Contains(
            collection: mirror,
            filter: static slot => ((slot.Item1.Row == LevelRow) && (slot.Item3 == 3d))
        );

        using var untieredServer = Fixtures.FreshServer(definition: untiered);
        var hash = WorldStateHashComposition.Hash(
            scope: WorldStateHashScope.World,
            server: untieredServer.Server,
            tick: 0UL
        );

        foreach (var tiered in ((WorldDefinition[])[low, high])) {
            Assert.Equal(
                actual: WorldPresentationManifest.Compile(definition: tiered).Bindings.ToArray(),
                expected: manifest.Bindings.ToArray()
            );
            Assert.Equal(
                actual: WorldPresentationManifest.Compile(definition: tiered).BodyBindings.ToArray(),
                expected: manifest.BodyBindings.ToArray()
            );
            Assert.Equal(
                actual: Mirror(definition: tiered),
                expected: mirror
            );

            using var server = Fixtures.FreshServer(definition: tiered);

            Assert.Equal(
                actual: WorldStateHashComposition.Hash(
                    scope: WorldStateHashScope.World,
                    server: server.Server,
                    tick: 0UL
                ),
                expected: hash
            );
        }

        // The tier is the rows' one difference, and it survives a round trip through the document's JSON.
        Assert.NotEqual(
            actual: Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.Serialize(definition: high)),
            expected: Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.Serialize(definition: untiered))
        );
        Assert.Equal(
            actual: WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: high)).Views.Graphs![0].Tier,
            expected: QualityTier.High
        );
    }
    [Fact]
    public void AnUnknownTierIsRefusedByName() {
        var json = Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.Serialize(definition: Document(tier: QualityTier.High)));

        Assert.Contains(
            actualString: json,
            expectedSubstring: "\"tier\": \"high\""
        );

        // The tier is spelled one way: another word, or the right word in another case, is refused naming it and the
        // vocabulary.
        foreach (var word in ((string[])["ultra", "High"])) {
            var refusal = Assert.Throws<InvalidDataException>(testCode: () => WorldDefinitionSerialization.Deserialize(utf8Json: Encoding.UTF8.GetBytes(s: json.Replace(
                newValue: $"\"tier\": \"{word}\"",
                oldValue: "\"tier\": \"high\""
            ))));

            Assert.Contains(
                actualString: refusal.Message,
                expectedSubstring: $"tier '{word}' must be 'low', 'medium', or 'high'"
            );
        }
    }
    [Fact]
    public void APackageRowTakesNoTier() {
        var document = Fixtures.BuildDocument();

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: (document with {
                ViewsRaw = (document.Views with {
                    Graphs = [new WorldViewGraph(
                        Name: "world2",
                        Package: "sdf.world",
                        Tier: QualityTier.Low
                    )],
                }),
            }),
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "package instance 'world2' takes no timeScale, output, overrides, parameters or tier"
        );
    }
}
