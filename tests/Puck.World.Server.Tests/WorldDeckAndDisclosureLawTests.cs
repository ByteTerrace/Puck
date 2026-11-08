using Puck.Commands;
using System.Text;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>Pins the in-place shuffle's determinism and cursor accounting, and what a hidden cell leaves behind
/// under each disclosure policy.</summary>
public sealed class WorldDeckAndDisclosureLawTests {
    private static WorldDefinition Deck(int count) {
        var keys = Enumerable.Range(
            count: count,
            start: 0
        ).Select(selector: i => $"c{i}").ToArray();

        return Fixtures.BuildDocument() with {
            StateRaw = new(World: [
                new(
                Name(value: "cards"),
                CellKind.Int,
                Cells: keys.Select(selector: k => StateFixtures.Cell(k)).ToArray(),
                Capacity: count
            ),
                new(
                Name(value: "deck"),
                CellKind.Bool,
                Cells: keys.Select(selector: k => StateFixtures.Cell(key: k, kind: CellKind.Bool)).ToArray(),
                Domain: new StateDomain.KeysOf(
                    CellName.Parse(candidate: "cards"),
                    Ordered: true
                ),
                Capacity: count
            ),
                new(
                Name(value: "dice"),
                CellKind.Int,
                Draw: new Draw(
                    Generator: new StateGenerator(Source: GeneratorSource.StreamDraw),
                    Timing: DrawTiming.Event
                )
            ),
            ]),
            Rules = [],
        };
    }
    private static WorldStateRow Find(WorldDefinition document, string row) => WorldDefinitionRows.FindStateRow(
        document.State,
        row
    )!;
    private static WorldDefinition Hand(HiddenCells hidden) => Fixtures.BuildDocument() with {
        StateRaw = new(World: [
            new(
            Name(value: "cards"),
            CellKind.Int,
            Cells: [StateFixtures.Cell(
                    key: "ace",
                    value: 101
                ), StateFixtures.Cell(
                    key: "king",
                    value: 202
                )],
            Visibility: new()
        ),
            new(
            Name(value: "hand"),
            CellKind.Bool,
            Cells: [StateFixtures.Cell(key: "ace", kind: CellKind.Bool) with { Visibility = new(["seat1"]) }, StateFixtures.Cell(key: "king", kind: CellKind.Bool) with { Visibility = new(["seat1"]) }],
            Domain: new StateDomain.KeysOf(
                CellName.Parse(candidate: "cards"),
                Ordered: true
            ),
            Visibility: new(Hidden: hidden)
        ),
        ]),
        Rules = [],
    };
    private static CellName Name(string value) => CellName.Parse(candidate: value);

    [Fact]
    public void HiddenCellsLeaveExactlyWhatThePolicyAllows() {
        foreach (var (policy, cells, count) in new[] { (HiddenCells.Omit, 0, 0), (HiddenCells.Count, 0, 2), (HiddenCells.Placeholder, 2, 2) }) {
            var definition = Hand(hidden: policy);
            var opponent = Fixtures.Disclose(
                definition: definition,
                recipient: Principal.Seat(slot: 1)
            )!.Single(predicate: r => (r.Name == "hand"));
            var owner = Fixtures.Disclose(
                definition: definition,
                recipient: Principal.Seat(slot: 0)
            )!.Single(predicate: r => (r.Name == "hand"));

            Assert.Equal(
                count,
                opponent.HiddenCount
            );
            Assert.Equal(
                cells,
                opponent.Cells.Count
            );
            Assert.All(
                opponent.Cells,
                c => {
                    Assert.True(condition: c.Hidden); Assert.Equal(
                string.Empty,
                c.Key
            ); Assert.Null(@object: c.Text);
                }
            );
            Assert.Equal(
                0,
                owner.HiddenCount
            );
            Assert.Equal(
                new[] { "ace", "king" },
                owner.Cells.Select(selector: c => c.Key)
            );

            var json = Encoding.UTF8.GetString(bytes: WorldProjection.Serialize(projection: Fixtures.Project(
                definition,
                WorldDisclosureTier.Presentation,
                "test",
                1,
                Principal.Seat(slot: 1)
            )!));

            Assert.DoesNotContain(
                actualString: json,
                expectedSubstring: "\"ace\""
            );
            Assert.DoesNotContain(
                actualString: json,
                expectedSubstring: "\"king\""
            );
            Assert.Equal(
                (policy != HiddenCells.Omit),
                json.Contains(value: "hiddenCount")
            );
        }
    }
    // A hand of three dealt into a four-slot rack: the hidden `ace` holds slot 0 and the visible `king` and `queen`
    // slots 1 and 3 (a card that left slot 2 freed it), the king displaced half a metre up from its slot.
    private static WorldDefinition Rack(HiddenCells hidden, string? solidCard = null) {
        static StateCell Card(string key, bool hidden) => StateFixtures.Cell(key: key, kind: CellKind.Bool) with { Visibility = (hidden ? new(["seat1"]) : null) };
        var template = new WorldPlacement(
            Id: "rack",
            PrototypeId: "back",
            Position: new System.Numerics.Vector3(x: 0f, y: 1f, z: 0f),
            YawDegrees: 0f,
            Scale: 1f,
            Distribution: new WorldDistribution(
                Region: new WorldDistributionRegion.Lattice(
                    StepA: new System.Numerics.Vector3(x: 0.25f, y: 0f, z: 0f),
                    CountA: 4,
                    StepB: new System.Numerics.Vector3(x: 0f, y: 0f, z: 1f),
                    CountB: 1
                ),
                Fill: new WorldSequence(Name: WorldSequence.None, Offset: 0, Step: 0f)
            ),
            Deal: new WorldPlacementDeal(Row: "hand", Preserve: new WorldPlacementDealPreserve(Transform: true))
        );
        WorldPlacement Child(string key, int slot, float lift) => WorldPlacementDeal.Child(
            id: WorldPlacementDeal.ChildId(template: "rack", key: key),
            offset: new System.Numerics.Vector3(x: 0.25f * slot, y: lift, z: 0f),
            prototype: key,
            slot: slot,
            template: template
        ) with { Solid = ((key == solidCard) ? new WorldSolid(Margin: 0f) : null) };

        return Fixtures.BuildDocument() with {
            StateRaw = new(World: [
                new(Name(value: "hand"), CellKind.Bool, Cells: [Card("ace", hidden: true), Card("king", hidden: false), Card("queen", hidden: false)],
                    Capacity: 4, Visibility: new(Hidden: hidden)),
            ]),
            PlacementRowsRaw = [template, Child("ace", slot: 0, lift: 0f), Child("king", slot: 1, lift: 0.5f), Child("queen", slot: 3, lift: 0f)],
            Rules = [],
        };
    }

    [Fact]
    public void ADealtHandIsReDealtInTheReadersOwnOrder() {
        foreach (var policy in new[] { HiddenCells.Omit, HiddenCells.Count, HiddenCells.Placeholder }) {
            var definition = Rack(hidden: policy);
            var arena = Fixtures.Store(definition: definition);
            var opponent = WorldStateDisclosure.PlacementProjection(definition: definition, arena: arena, recipient: Principal.Seat(slot: 1));
            var owner = WorldStateDisclosure.PlacementProjection(definition: definition, arena: arena, recipient: Principal.Seat(slot: 0));
            var replica = WorldStateDisclosure.Disclose(definition: definition, arena: arena, recipient: Principal.Seat(slot: 1)).Definition.Placements;
            string Layout(IReadOnlyList<WorldPlacement> placements) => string.Join(separator: "; ", values: placements.Select(selector: p =>
                FormattableString.Invariant($"{p.Id}@{p.DealSlot}=({p.Position.X:0.##},{p.Position.Y:0.##}) {p.PrototypeId}")));

            // Nothing of the authority's slots survives for the opponent: the king's half-metre displacement rides its
            // new slot, the queen closes the gap, and the hidden ace is absent or, under Placeholder, an anonymous back.
            Assert.Equal(
                expected: (policy == HiddenCells.Placeholder)
                    ? "rack@=(0,1) back; rack/$back$0@0=(0,0) back; rack/king@1=(0.25,0.5) king; rack/queen@2=(0.5,0) queen"
                    : "rack@=(0,1) back; rack/king@0=(0,0.5) king; rack/queen@1=(0.25,0) queen",
                actual: Layout(opponent)
            );
            Assert.Equal(expected: Layout(opponent), actual: Layout(replica));
            Assert.Equal(
                expected: "rack@=(0,1) back; rack/ace@0=(0,0) ace; rack/king@1=(0.25,0.5) king; rack/queen@2=(0.5,0) queen",
                actual: Layout(owner)
            );
        }
        Assert.Equal(expected: "rack", actual: WorldPlacementDeal.PickedId(id: WorldPlacementDeal.PlaceholderId(template: "rack", slot: 0)));
        Assert.Equal(expected: "rack/king", actual: WorldPlacementDeal.PickedId(id: "rack/king"));
    }
    [Fact]
    public void ATemplateOverARowThatCanWithholdDealsNoPhysicalFacet() {
        var definition = Rack(hidden: HiddenCells.Placeholder, solidCard: "king");
        var admitted = WorldDefinitionValidator.TryValidateDealtChildren(
            children: [definition.Placements[2]],
            definition: definition,
            reason: out var reason,
            template: definition.Placements[0]
        );

        Assert.False(condition: admitted);
        Assert.Contains(expectedSubstring: "placement 'rack/king' carries solid", actualString: reason, comparisonType: StringComparison.Ordinal);
    }
    // A dealt child of a template that can withhold carries only what the re-deal already discloses, so a facet outside
    // that set (here inhabit, which a preserved child could once be edited to keep) is refused by name, and the same
    // child without it is admitted.
    [Fact]
    public void ADealtChildUnderARowThatCanWithholdCarriesOnlyWhatTheReDealDiscloses() {
        var definition = Rack(hidden: HiddenCells.Omit) with { CreationsRaw = [CreationFixtures.UnitSphere(id: "king")] };
        var plain = definition.Placements[2];
        var inhabited = plain with { Inhabit = new WorldPlacementInhabit(Kit: "display", Look: null, Source: IntentSource.Idle) };
        bool Admits(WorldPlacement child) => WorldDefinitionValidator.TryValidateDealtChildren(children: [child], definition: definition, reason: out _, template: definition.Placements[0]);

        Assert.True(condition: Admits(child: plain));
        Assert.False(condition: Admits(child: inhabited));
        _ = WorldDefinitionValidator.TryValidateDealtChildren(children: [inhabited], definition: definition, reason: out var reason, template: definition.Placements[0]);
        Assert.Contains(expectedSubstring: "placement 'rack/king' carries inhabit", actualString: reason, comparisonType: StringComparison.Ordinal);
    }
    // A public dealt row whose faces come from a row only seat 1 reads: the opponent's placements are the same whichever
    // face the hidden variant cell selects, each child shows the template's own prototype, and the variant row is
    // recorded as a row the placements read.
    [Fact]
    public void AHiddenVariantAloneMasksTheFaceAndIsRecorded() {
        WorldDefinition Faces(long king) {
            var template = new WorldPlacement(
                Id: "rack",
                PrototypeId: "back",
                Position: new System.Numerics.Vector3(x: 0f, y: 1f, z: 0f),
                YawDegrees: 0f,
                Scale: 1f,
                Distribution: new WorldDistribution(
                    Region: new WorldDistributionRegion.Lattice(
                        StepA: new System.Numerics.Vector3(x: 0.25f, y: 0f, z: 0f),
                        CountA: 2,
                        StepB: new System.Numerics.Vector3(x: 0f, y: 0f, z: 1f),
                        CountB: 1
                    ),
                    Fill: new WorldSequence(Name: WorldSequence.None, Offset: 0, Step: 0f)
                ),
                Deal: new WorldPlacementDeal(Row: "hand", Variants: new WorldPlacementDealVariants(
                    Row: "faces",
                    Map: new Dictionary<string, string>(comparer: StringComparer.Ordinal) { ["1"] = "ace", ["2"] = "king" }
                ))
            );
            WorldPlacement Child(string key, int slot, string prototype) => WorldPlacementDeal.Child(
                id: WorldPlacementDeal.ChildId(template: "rack", key: key),
                offset: new System.Numerics.Vector3(x: 0.25f * slot, y: 0f, z: 0f),
                prototype: prototype,
                slot: slot,
                template: template
            );

            return Fixtures.BuildDocument() with {
                StateRaw = new(World: [
                    new(Name(value: "hand"), CellKind.Bool, Cells: [StateFixtures.Cell(key: "queen", kind: CellKind.Bool), StateFixtures.Cell(key: "king", kind: CellKind.Bool)], Capacity: 2),
                    new(Name(value: "faces"), CellKind.Int, Cells: [StateFixtures.Cell(key: "queen", value: 1) with { Visibility = new(["seat1"]) }, StateFixtures.Cell(key: "king", value: king) with { Visibility = new(["seat1"]) }]),
                ]),
                PlacementRowsRaw = [template, Child("queen", slot: 0, prototype: "ace"), Child("king", slot: 1, prototype: (king == 1) ? "ace" : "king")],
                Rules = [],
            };
        }
        string Layout(WorldDefinition definition, Principal recipient) => string.Join(separator: "; ", values: WorldStateDisclosure.PlacementProjection(
            definition: definition,
            arena: Fixtures.Store(definition: definition),
            recipient: recipient
        ).Select(selector: p => FormattableString.Invariant($"{p.Id}@{p.DealSlot}=({p.Position.X:R},{p.Position.Y:R}) {p.PrototypeId}")));

        Assert.Equal(expected: Layout(definition: Faces(king: 1), recipient: Principal.Seat(slot: 1)), actual: Layout(definition: Faces(king: 2), recipient: Principal.Seat(slot: 1)));
        Assert.Equal(expected: "rack@=(0,1) back; rack/queen@0=(0,0) back; rack/king@1=(0.25,0) back", actual: Layout(definition: Faces(king: 2), recipient: Principal.Seat(slot: 1)));
        Assert.Equal(expected: "rack@=(0,1) back; rack/queen@0=(0,0) ace; rack/king@1=(0.25,0) king", actual: Layout(definition: Faces(king: 2), recipient: Principal.Seat(slot: 0)));
        var hidden = Faces(king: 2);
        var disclosed = WorldStateDisclosure.Disclose(definition: hidden, arena: Fixtures.Store(definition: hidden), recipient: Principal.Seat(slot: 1));

        Assert.Contains(expected: "faces", collection: disclosed.PlacementReads!);
        Assert.Contains(expected: "hand", collection: disclosed.PlacementReads!);
    }
    // A lattice step only fixed point holds exactly beyond the first slot (128 + 2^-16 metres): the one visible card,
    // dealt alone at slot 0 or after three hidden cards at slot 3 (where the stored float position rounds), is shown at
    // bit-identical coordinates either way, since the re-dealt position comes from the lattice and never from the
    // authority's rounded position.
    [Fact]
    public void AReDealtPositionCarriesNoRoundingOfTheAuthoritysSlot() {
        WorldDefinition Arrived(int hiddenAhead) {
            var template = new WorldPlacement(
                Id: "rack",
                PrototypeId: "back",
                Position: new System.Numerics.Vector3(x: 0f, y: 1f, z: 0f),
                YawDegrees: 0f,
                Scale: 1f,
                Distribution: new WorldDistribution(
                    Region: new WorldDistributionRegion.Lattice(
                        StepA: new System.Numerics.Vector3(x: 128.0000152587890625f, y: 0f, z: 0f),
                        CountA: 4,
                        StepB: new System.Numerics.Vector3(x: 0f, y: 0f, z: 1f),
                        CountB: 1
                    ),
                    Fill: new WorldSequence(Name: WorldSequence.None, Offset: 0, Step: 0f)
                ),
                Deal: new WorldPlacementDeal(Row: "hand")
            );
            var offsets = WorldPlacementDeal.Offsets(template: template, worldSeed: 0UL);
            var keys = Enumerable.Range(start: 0, count: hiddenAhead).Select(selector: index => $"hidden{index}").Append(element: "king").ToArray();
            // The sweep stores each child at its slot's offset narrowed to float, which is what rounds at slot 3.
            WorldPlacement Child(string key, int slot) => WorldPlacementDeal.Child(
                id: WorldPlacementDeal.ChildId(template: "rack", key: key),
                offset: offsets[slot].ToVector3(),
                prototype: "back",
                slot: slot,
                template: template
            );

            return Fixtures.BuildDocument() with {
                StateRaw = new(World: [
                    new(Name(value: "hand"), CellKind.Bool, Cells: [.. keys.Select(selector: key => StateFixtures.Cell(key: key, kind: CellKind.Bool) with { Visibility = ((key == "king") ? null : new(["seat1"])) })],
                        Capacity: 4, Visibility: new(Hidden: HiddenCells.Omit)),
                ]),
                PlacementRowsRaw = [template, .. keys.Select(selector: (key, slot) => Child(key: key, slot: slot))],
                Rules = [],
            };
        }
        string Layout(WorldDefinition definition) => string.Join(separator: "; ", values: WorldStateDisclosure.PlacementProjection(
            definition: definition,
            arena: Fixtures.Store(definition: definition),
            recipient: Principal.Seat(slot: 1)
        ).Select(selector: p => FormattableString.Invariant($"{p.Id}@{p.DealSlot}=({p.Position.X:R},{p.Position.Y:R},{p.Position.Z:R})")));

        Assert.Equal(expected: "rack@=(0,1,0); rack/king@0=(0,0,0)", actual: Layout(definition: Arrived(hiddenAhead: 0)));
        Assert.Equal(expected: Layout(definition: Arrived(hiddenAhead: 0)), actual: Layout(definition: Arrived(hiddenAhead: 3)));
    }
    [Fact]
    public void HiddenPolicyRoundTripsThroughTheStrictWireShape() {
        var definition = Hand(hidden: HiddenCells.Placeholder);
        var parsed = WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: definition));

        Assert.Equal(
            HiddenCells.Placeholder,
            Find(
                document: parsed,
                row: "hand"
            ).Visibility!.Hidden
        );
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: parsed,
                reason: out var reason
            ),
            userMessage: reason
        );
    }
    [Fact]
    public void ShuffleAdmitsAnUnorderedKeyedRowButRefusesASlotAndBootOnlySites() {
        // keysOf collapsed the old ordered-zone/unordered-attribute-row split into one shape: cell order is either
        // gameplay-meaningful (a pile) or incidental, but a keyed row's cells can always be permuted either way.
        var unordered = Deck(count: 4) with { };

        unordered = unordered with {
            StateRaw = unordered.StateRaw! with {
                World = unordered.StateRaw.World!.Select(selector: r => ((r.Name.Value == "deck")
            ? r with {
                Domain = new StateDomain.KeysOf(
                CellName.Parse(candidate: "cards"),
                Ordered: false
            ),
            }
            : r)).ToArray(),
            },
        };
        Assert.True(
            condition: WorldArenaTransforms.TryApply(
                unordered,
                new StateTransform.Shuffle(
                    Draw: "dice",
                    Row: "deck"
                ),
                Principal.World,
                0,
                "test",
                out _,
                out var unorderedReason
            ),
            userMessage: unorderedReason
        );

        var slotOnly = Deck(count: 4) with { };

        slotOnly = slotOnly with {
            StateRaw = slotOnly.StateRaw! with {
                World = slotOnly.StateRaw.World!.Select(selector: r => ((r.Name.Value == "deck")
            ? r with {
                Domain = null,
                Capacity = null,
                Cells = [new(
                    WorldStateRow.SlotKey,
                    CellValue.Bool(value: false)
                )],
            }
            : r)).ToArray(),
            },
        };
        Assert.False(condition: WorldArenaTransforms.TryApply(
            slotOnly,
            new StateTransform.Shuffle(
                Draw: "dice",
                Row: "deck"
            ),
            Principal.World,
            0,
            "test",
            out _,
            out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "keyed or ordered row"
        );

        var bootSite = Deck(count: 4);

        bootSite = bootSite with {
            StateRaw = bootSite.StateRaw! with {
                World = bootSite.StateRaw.World!.Select(selector: r => ((r.Name.Value == "dice")
            ? r with { Draw = r.Draw! with { Timing = DrawTiming.Boot } }
            : r)).ToArray(),
            },
        };
        Assert.False(condition: WorldArenaTransforms.TryApply(
            bootSite,
            new StateTransform.Shuffle(
                Draw: "dice",
                Row: "deck"
            ),
            Principal.World,
            0,
            "test",
            out _,
            out var siteReason
        ));
        Assert.Contains(
            actualString: siteReason,
            expectedSubstring: "streamDraw site"
        );

        var ruled = Deck(count: 4) with {
            Rules = [new WorldRule(
                CellName.Parse(candidate: "shuffle"),
                [new ActionEffect.TransformState(Transform: new StateTransform.Shuffle(
                        Draw: "dice",
                        Row: "deck"
                    ))]
            )],
        };

        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: ruled,
                reason: out var ok
            ),
            userMessage: ok
        );
        var badRule = Deck(count: 4) with {
            Rules = [new WorldRule(
                CellName.Parse(candidate: "shuffle"),
                [new ActionEffect.TransformState(Transform: new StateTransform.Shuffle(
                        Draw: "deck",
                        Row: "deck"
                    ))]
            )],
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: badRule,
            reason: out _
        ));
    }
    [Fact]
    public void ShuffleIsAPermutationThatSpendsOneSamplePerPositionAndReplaysExactly() {
        var definition = Deck(count: 52);

        var shuffled = StateFixtures.Apply(
            definition: definition,
            transform: new StateTransform.Shuffle(
                Draw: "dice",
                Row: "deck"
            )
        );
        var order = Find(
            document: shuffled,
            row: "deck"
        ).Cells!.Select(selector: c => c.Key.Value).ToArray();

        Assert.Equal(
            52,
            order.Length
        );
        Assert.Equal(
            52,
            order.Distinct().Count()
        );
        Assert.NotEqual(
            Find(
                document: definition,
                row: "deck"
            ).Cells!.Select(selector: c => c.Key.Value),
            order
        );
        Assert.Equal(
            51L,
            Find(
                document: shuffled,
                row: "dice"
            ).DrawCursor
        );

        var again = StateFixtures.Apply(
            definition: definition,
            transform: new StateTransform.Shuffle(
                Draw: "dice",
                Row: "deck"
            )
        );

        Assert.Equal(
            order,
            Find(
                document: again,
                row: "deck"
            ).Cells!.Select(selector: c => c.Key.Value)
        );

        var later = StateFixtures.Apply(
            definition: shuffled,
            transform: new StateTransform.Shuffle(
                Draw: "dice",
                Row: "deck"
            )
        );

        Assert.NotEqual(
            order,
            Find(
                document: later,
                row: "deck"
            ).Cells!.Select(selector: c => c.Key.Value)
        );
        Assert.Equal(
            102L,
            Find(
                document: later,
                row: "dice"
            ).DrawCursor
        );
    }
}
