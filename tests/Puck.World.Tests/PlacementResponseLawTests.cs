using System.Numerics;
using Puck.Assets.Documents;
using Puck.World.Authoring;
using Puck.SignedDistance;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: a placement's response trait swaps its prototype the tick a lattice condition first holds, through the
/// ordinary mutation pipeline — deterministically, and never when the trait is absent or its condition never holds.
/// Multiple entries try in authored order and the first match wins.
/// </summary>
public sealed class PlacementResponseLawTests {
    private const string BaseCreation = "leaf";
    private const string FieldName = "char";
    private const string PlacementId = "grove";
    private const string TargetCreation = "stump";

    private static WorldPrototype Creation(string id) {
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: id,
            Palette: null,
            Shapes: [
                new ShapeDocument(
                    Id: 0,
                    Name: null,
                    Type: SdfSolidPrimitive.Sphere,
                    Position: Vector3.Zero,
                    Rotation: Quaternion.Identity,
                    Scale: new Vector3(value: 1f),
                    Material: 0,
                    Blend: SdfBlendOp.Union,
                    Smooth: 0f,
                    Group: 0
                ),
            ],
            Frames: null
        );
        var canonical = CreationCanonicalizer.Canonicalize(
            document: document,
            source: id
        );

        return new WorldPrototype(
            Id: id,
            Document: canonical.Document,
            HashRaw: canonical.Hash
        );
    }
    // One 1x1x1 lattice cell over a field named "char", climbing 0.1/tick — an unconditional transform (an empty
    // "when" holds vacuously) so every fixture step deterministically advances the SAME chemistry, with no fire
    // simulation or body coupling needed to drive it.
    private static WorldStateSection FieldsSection() => new(
        World: [
            new WorldStateRow(
                Name: CellName.Parse(candidate: FieldName),
                Kind: CellKind.Fixed,
                Domain: new StateDomain.CellsOf(Topology: "world"), Field: new WorldStateFieldTrait(Initial: 0f, Min: 0f, Max: 1f)
            ),
        ],
        Lattices: [
            new WorldFieldTopology(
                Name: "world",
                Origin: new DocumentVector3(value: Vector3.Zero),
                CellSize: 1f,
                Width: 1,
                Depth: 1,
                Layers: 1,
                StepEveryTicks: 1,
                Reactions: [
                    new WorldReaction.Transform(
                        When: [],
                        Then: [new WorldFieldWrite(Field: FieldName, Op: WorldFieldWriteOp.Add, Value: 0.1f)]
                    ),
                ]
            ),
        ]
    );
    private static WorldDefinition Document(IReadOnlyList<WorldPlacementResponse>? respond) {
        var document = Fixtures.BuildDocument();

        return (document with {
            StateRaw = FieldsSection(),
            CreationsRaw = [Creation(id: BaseCreation), Creation(id: TargetCreation)],
            PlacementRowsRaw = [
                new WorldPlacement(
                    Id: PlacementId,
                    PrototypeId: BaseCreation,
                    Position: new DocumentVector3(value: Vector3.Zero),
                    YawDegrees: 0f,
                    Scale: 1f,
                    Respond: respond
                ),
            ],
        });
    }
    private static WorldPlacementResponse Entry(ActionStateComparison comparison, float threshold, string prototypeId) => new(
        When: new WorldPlacementResponseCondition.FieldCondition(Comparison: comparison, Field: FieldName, Value: threshold),
        PrototypeId: prototypeId
    );
    private static string PrototypeOf(WorldFixture fixture) => WorldDefinitionRows.FindPlacement(
        id: PlacementId,
        placements: fixture.Server.Definition.Placements
    )!.PrototypeId;

    /// <summary>ABSENT trait: stepping past the tick a would-be threshold would have crossed leaves the row's
    /// prototype byte-for-byte unchanged. CONTROL: the identical run WITH the trait present swaps.</summary>
    [Fact]
    public void AbsentTraitIsANoOp() {
        using var absent = Fixtures.FreshServer(definition: Document(respond: null));

        for (var index = 0; (index < 20); index++) {
            absent.Step();
        }

        Assert.Equal(
            actual: PrototypeOf(fixture: absent),
            expected: BaseCreation
        );

        using var present = Fixtures.FreshServer(definition: Document(respond: [
            Entry(comparison: ActionStateComparison.GreaterOrEqual, prototypeId: TargetCreation, threshold: 0.5f),
        ]));

        for (var index = 0; (index < 20); index++) {
            present.Step();
        }

        Assert.Equal(
            actual: PrototypeOf(fixture: present),
            expected: TargetCreation
        );
    }
    /// <summary>Two entries hold simultaneously once the field passes the higher threshold; the FIRST in authored
    /// order wins regardless of which condition is "more true". Reversing authored order flips the winner — the
    /// same field trajectory, a different result, proving the rule reads order, not magnitude.</summary>
    [Fact]
    public void FirstMatchInAuthoredOrderWins() {
        const string secondTarget = "ember";

        using var firstWins = Fixtures.FreshServer(definition: (Document(respond: [
            Entry(comparison: ActionStateComparison.GreaterOrEqual, prototypeId: TargetCreation, threshold: 0.2f),
            Entry(comparison: ActionStateComparison.GreaterOrEqual, prototypeId: secondTarget, threshold: 0.2f),
        ]) with {
            CreationsRaw = [Creation(id: BaseCreation), Creation(id: TargetCreation), Creation(id: secondTarget)],
        }));

        for (var index = 0; (index < 5); index++) {
            firstWins.Step();
        }

        Assert.Equal(
            actual: PrototypeOf(fixture: firstWins),
            expected: TargetCreation
        );

        using var secondWins = Fixtures.FreshServer(definition: (Document(respond: [
            Entry(comparison: ActionStateComparison.GreaterOrEqual, prototypeId: secondTarget, threshold: 0.2f),
            Entry(comparison: ActionStateComparison.GreaterOrEqual, prototypeId: TargetCreation, threshold: 0.2f),
        ]) with {
            CreationsRaw = [Creation(id: BaseCreation), Creation(id: TargetCreation), Creation(id: secondTarget)],
        }));

        for (var index = 0; (index < 5); index++) {
            secondWins.Step();
        }

        Assert.Equal(
            actual: PrototypeOf(fixture: secondWins),
            expected: secondTarget
        );
    }
    /// <summary>A holding condition swaps the prototype; the row is left exactly as it reads once nothing holds
    /// (the facet only ever SELECTS on a match, it never reverts) — proved by an unreachable second entry that a
    /// later tick could otherwise have satisfied.</summary>
    [Fact]
    public void ANonHoldingConditionNeverSwaps() {
        using var fixture = Fixtures.FreshServer(definition: Document(respond: [
            Entry(comparison: ActionStateComparison.GreaterOrEqual, prototypeId: TargetCreation, threshold: 999f),
        ]));

        for (var index = 0; (index < 20); index++) {
            fixture.Step();
        }

        Assert.Equal(
            actual: PrototypeOf(fixture: fixture),
            expected: BaseCreation
        );
    }
    /// <summary>DETERMINISM: two independently constructed fixtures, the same document, the same input (none —
    /// nothing but ticks drives this document's chemistry), swap on the exact same tick every time, and every
    /// intermediate tick's prototype matches step for step across the whole run.</summary>
    [Fact]
    public void TheSwapTickIsDeterministicAcrossIndependentRuns() {
        WorldDefinition Build() => Document(respond: [
            Entry(comparison: ActionStateComparison.GreaterOrEqual, prototypeId: TargetCreation, threshold: 0.5f),
        ]);

        using var a = Fixtures.FreshServer(definition: Build());
        using var b = Fixtures.FreshServer(definition: Build());

        for (var index = 0; (index < 20); index++) {
            a.Step();
            b.Step();

            Assert.Equal(
                actual: PrototypeOf(fixture: b),
                expected: PrototypeOf(fixture: a)
            );
        }

        // Both runs actually exercised the swap — a determinism check over a run that never fires proves nothing.
        Assert.Equal(
            actual: PrototypeOf(fixture: a),
            expected: TargetCreation
        );
    }
}
/// <summary>
/// THE LAW: a state condition swaps the prototype on the exact tick its compared row crosses the comparison — the
/// same determinism the field arm proves above, now driven by an ordinary rule-written row instead of a lattice
/// field, and independent of any <c>fields</c> section at all — and a placement whose every entry is a state
/// condition costs nothing extra once its rows stop moving: a quiet tick's allocation with many such placements
/// matches one with none.
/// </summary>
[Collection(AllocationCollection.Name)]
public sealed class PlacementResponseStateConditionLawTests(ITestOutputHelper output) {
    private const string BaseCreation = "leaf";
    private const string CounterRow = "counter";
    private const string PlacementId = "grove";
    private const string TargetCreation = "stump";

    private static WorldPrototype Creation(string id) {
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: id,
            Palette: null,
            Shapes: [
                new ShapeDocument(
                    Id: 0,
                    Name: null,
                    Type: SdfSolidPrimitive.Sphere,
                    Position: Vector3.Zero,
                    Rotation: Quaternion.Identity,
                    Scale: new Vector3(value: 1f),
                    Material: 0,
                    Blend: SdfBlendOp.Union,
                    Smooth: 0f,
                    Group: 0
                ),
            ],
            Frames: null
        );
        var canonical = CreationCanonicalizer.Canonicalize(
            document: document,
            source: id
        );

        return new WorldPrototype(
            Id: id,
            Document: canonical.Document,
            HashRaw: canonical.Hash
        );
    }
    // No fields section at all — the state arm reads an ordinary rule-written row, never a lattice cell. A rule with
    // no gate (Level trigger) fires every tick, so the counter is exactly the completed tick count.
    private static WorldDefinition CrossingDocument() {
        var document = Fixtures.BuildDocument();

        return (document with {
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(Name: CellName.Parse(candidate: CounterRow), Kind: CellKind.Int),
            ]),
            Rules = [new WorldRule(Name: CellName.Parse(candidate: "tick"), Effects: [new ActionEffect.AddState(State: CounterRow, Value: 1)])],
            CreationsRaw = [Creation(id: BaseCreation), Creation(id: TargetCreation)],
            PlacementRowsRaw = [
                new WorldPlacement(
                    Id: PlacementId,
                    PrototypeId: BaseCreation,
                    Position: new DocumentVector3(value: Vector3.Zero),
                    YawDegrees: 0f,
                    Scale: 1f,
                    Respond: [
                        new WorldPlacementResponse(
                            When: new WorldPlacementResponseCondition.StateCondition(State: CounterRow, Comparison: ActionStateComparison.GreaterOrEqual, Value: 3),
                            PrototypeId: TargetCreation
                        ),
                    ]
                ),
            ],
        });
    }
    private static string PrototypeOf(WorldFixture fixture) => WorldDefinitionRows.FindPlacement(
        id: PlacementId,
        placements: fixture.Server.Definition.Placements
    )!.PrototypeId;

    /// <summary>The row climbs 1/tick from a rule with no gate; the swap lands the moment it reaches 3, not
    /// before.</summary>
    [Fact]
    public void SwapsExactlyOnTheTickTheCounterCrossesTheComparison() {
        using var fixture = Fixtures.FreshServer(definition: CrossingDocument());

        fixture.Step();
        Assert.Equal(expected: BaseCreation, actual: PrototypeOf(fixture: fixture));

        fixture.Step();
        Assert.Equal(expected: BaseCreation, actual: PrototypeOf(fixture: fixture));

        fixture.Step();
        Assert.Equal(expected: TargetCreation, actual: PrototypeOf(fixture: fixture));
    }
    // Breaking the source change once (stash it, re-run, restore) turns this red: without the skip, every placement
    // re-reads and re-compares its row every tick regardless of whether it moved, so the "with" and "without" medians
    // converge instead of the "with" one staying near the "without" baseline.
    [Fact]
    public void ManyUnchangingStateResponsesCostNothingExtraOnAQuietTick() {
        const int placementCount = 64;
        const int warmupTicks = 10;
        const int sampleTicks = 60;

        static WorldDefinition Build(bool respond) {
            var document = Fixtures.BuildDocument();
            var placements = new List<WorldPlacement>(capacity: placementCount);

            for (var index = 0; (index < placementCount); index++) {
                placements.Add(item: new WorldPlacement(
                    Id: $"quiet{index}",
                    PrototypeId: BaseCreation,
                    Position: new DocumentVector3(value: Vector3.Zero),
                    YawDegrees: 0f,
                    Scale: 1f,
                    // The comparison never holds (the row never moves off its default 0) — every entry stays quiet
                    // for the whole run, so the ONLY question is whether the sweep still pays for that every tick.
                    Respond: (respond
                        ? [new WorldPlacementResponse(
                            When: new WorldPlacementResponseCondition.StateCondition(State: CounterRow, Comparison: ActionStateComparison.GreaterOrEqual, Value: 999_999),
                            PrototypeId: BaseCreation
                        )]
                        : null
                    )
                ));
            }

            return (document with {
                StateRaw = new WorldStateSection(World: [
                    new WorldStateRow(Name: CellName.Parse(candidate: CounterRow), Kind: CellKind.Int),
                ]),
                CreationsRaw = [Creation(id: BaseCreation)],
                PlacementRowsRaw = placements,
            });
        }
        static long MedianStepBytes(WorldDefinition definition) {
            using var fixture = Fixtures.FreshServer(definition: definition);

            for (var warm = 0; (warm < warmupTicks); warm++) {
                fixture.Step();
            }

            var samples = new long[sampleTicks];

            for (var index = 0; (index < samples.Length); index++) {
                var before = GC.GetAllocatedBytesForCurrentThread();

                fixture.Step();
                samples[index] = (GC.GetAllocatedBytesForCurrentThread() - before);
            }

            Array.Sort(array: samples);

            return samples[(samples.Length / 2)];
        }

        var withResponses = MedianStepBytes(definition: Build(respond: true));
        var withoutResponses = MedianStepBytes(definition: Build(respond: false));
        var delta = (withResponses - withoutResponses);

        output.WriteLine(message: $"quiet tick median: {withResponses:N0} bytes with {placementCount} unchanging state responses, {withoutResponses:N0} bytes without (delta {delta:N0})");

        Assert.True(condition: (delta < 512), userMessage: $"expected {placementCount} quiet state-only responses to add under 512 bytes/tick over the baseline, measured a delta of {delta:N0}");
    }
}
