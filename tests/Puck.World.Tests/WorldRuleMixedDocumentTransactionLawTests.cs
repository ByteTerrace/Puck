using System.Numerics;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Locks the rule host's mixed transaction boundary: state writes and a placement document write belong to
/// one preflighted firing, and a later document leaf refusal rolls the whole firing back — the writes the rule made
/// before the transaction included, since a firing is one scope.</summary>
public sealed class WorldRuleMixedDocumentTransactionLawTests {
    private const string PlacementId = "camera-seat-0";

    private static WorldPlacement Placement(WorldDefinition definition, string id) =>
        definition.Placements.Single(predicate: placement => (placement.Id == id));
    private static WorldDefinition Scenario(bool includeBadSecondPlacement, bool transaction = true) {
        var document = Fixtures.BuildCameraBodyDocument();
        var prefix = new WorldStateRow(
            Name: CellName.Parse(candidate: "prefix"),
            Kind: CellKind.Int,
            Cells: [new StateCell(
                    Key: WorldStateRow.SlotKey,
                    Value: CellValue.Int(value: 0L)
                )]
        );
        var prefixLabel = new WorldStateRow(
            Name: CellName.Parse(candidate: "prefixLabel"),
            Kind: CellKind.Text,
            Capacity: 1,
            Cells: [new StateCell(
                    Key: CellName.Parse(candidate: "entry"),
                    Value: CellValue.Text(value: "before")
                )]
        );
        var stage = new WorldStateRow(
            Name: CellName.Parse(candidate: "stage"),
            Kind: CellKind.Int,
            Cells: [new StateCell(
                    Key: WorldStateRow.SlotKey,
                    Value: CellValue.Int(value: 0L)
                )]
        );
        var after = new WorldStateRow(
            Name: CellName.Parse(candidate: "after"),
            Kind: CellKind.Int,
            Cells: [new StateCell(
                    Key: WorldStateRow.SlotKey,
                    Value: CellValue.Int(value: 0L)
                )]
        );
        var label = new WorldStateRow(
            Name: CellName.Parse(candidate: "label"),
            Kind: CellKind.Text,
            Capacity: 1,
            Cells: [new StateCell(
                    Key: CellName.Parse(candidate: "entry"),
                    Value: CellValue.Text(value: "before")
                )]
        );
        var updatedBall = Placement(
            definition: document,
            id: PlacementId
        ) with { Scale = 1.25f };
        var effects = new List<ActionEffect> {
            new ActionEffect.SetState(
            State: "stage",
            Value: 1m
        ),
            new WorldEffect.UpsertPlacement(Placement: updatedBall),
        };

        if (includeBadSecondPlacement) {
            effects.Add(item: new WorldEffect.UpsertPlacement(Placement: new WorldPlacement(
                Id: "missing",
                PrototypeId: "missing",
                Position: Vector3.Zero,
                YawDegrees: 0f,
                Scale: 1f,
                Solid: new WorldSolid(Margin: 0f)
            )));
        }

        var ruleEffects = new List<ActionEffect> {
            new ActionEffect.AddState(
            State: "prefix",
            Value: 1m
        ),
            new ActionEffect.SetState(
            State: "prefixLabel",
            Key: "entry",
            Text: "prefix"
        ),
        };

        if (transaction) {
            ruleEffects.Add(item: new ActionEffect.Transaction(Effects: effects));
        } else {
            ruleEffects.AddRange(collection: effects);
        }
        if (!includeBadSecondPlacement) {
            ruleEffects.Add(item: new ActionEffect.SetState(
                State: "after",
                FromState: "stage"
            ));
            ruleEffects.Add(item: new ActionEffect.SetState(
                State: "label",
                Key: "entry",
                Text: "after"
            ));
        }

        return document with {
            StateRaw = new WorldStateSection(World: [prefix, prefixLabel, stage, after, label]),
            Rules = [new WorldRule(
                Name: CellName.Parse(candidate: "mixed-document-transaction"),
                Gate: new ActionPredicate.CompareState(
                    State: "stage",
                    Comparison: ActionStateComparison.Equal,
                    Value: 0m
                ),
                Effects: ruleEffects
            )],
        };
    }
    private static long State(WorldDefinition definition, string row) =>
        definition.State.Single(predicate: state => (state.Name.Value == row)).Cells!.Single().Value.Raw;
    private static string? Text(WorldDefinition definition, string row) =>
        definition.State.Single(predicate: state => (state.Name.Value == row)).Cells!.Single().Value.AsText;

    // One rule whose document arms depend on each other, gated to fire once.
    private static WorldDefinition Sequence(params ActionEffect[] arms) {
        var document = Fixtures.BuildCameraBodyDocument();

        return document with {
            StateRaw = new WorldStateSection(World: [new WorldStateRow(
                    Name: CellName.Parse(candidate: "stage"),
                    Kind: CellKind.Int,
                    Cells: [new StateCell(
                            Key: WorldStateRow.SlotKey,
                            Value: CellValue.Int(value: 0L)
                        )]
                )]),
            Rules = [new WorldRule(
                Name: CellName.Parse(candidate: "document-sequence"),
                Gate: new ActionPredicate.CompareState(
                    State: "stage",
                    Comparison: ActionStateComparison.Equal,
                    Value: 0m
                ),
                Effects: [
                    new ActionEffect.SetState(
                        State: "stage",
                        Value: 1m
                    ),
                    .. arms,
                ]
            )],
        };
    }

    // The arms of one firing are preflighted as the sequence they fire in. The second removal names a placement the
    // first has already removed, so the firing is refused before its commit and nothing of it lands: not the state
    // write, and not the first removal.
    [Fact]
    public void ARepeatedRemovalRefusesTheWholeFiringBeforeItCommits() {
        using var fixture = Fixtures.FreshServer(definition: Sequence(
            new WorldEffect.RemovePlacement(Id: PlacementId),
            new WorldEffect.RemovePlacement(Id: PlacementId)
        ));
        var installs = 0;

        fixture.Server.MutationJournalTap = (_, _, _) => installs++;
        fixture.Step();

        Assert.Equal(
            actual: State(
                definition: fixture.Server.Definition,
                row: "stage"
            ),
            expected: 0L
        );
        Assert.Contains(
            collection: fixture.Server.Definition.Placements,
            filter: static placement => (placement.Id == PlacementId)
        );
        Assert.Equal(
            actual: installs,
            expected: 0
        );
    }
    // A gate that only the install door runs, here the render envelope, is decided before the firing commits: the
    // row composes and validates, the envelope has no room for it, and nothing of the firing lands.
    [Fact]
    public void ARowTheRenderEnvelopeRefusesRewindsTheFiringsStateWrite() {
        var created = (Placement(
            definition: Fixtures.BuildCameraBodyDocument(),
            id: PlacementId
        ) with { Id = "past-the-envelope" });

        using var fixture = Fixtures.FreshServer(definition: Sequence(new WorldEffect.UpsertPlacement(Placement: created)));
        using var full = fixture.Server.Envelope.Configure(
            instanceCapacity: 0,
            measure: static _ => (1, 1),
            programWordCapacity: 0
        );
        var installs = 0;

        fixture.Server.MutationJournalTap = (_, _, _) => installs++;
        fixture.Step();

        Assert.Equal(
            actual: State(
                definition: fixture.Server.Definition,
                row: "stage"
            ),
            expected: 0L
        );
        Assert.DoesNotContain(
            collection: fixture.Server.Definition.Placements,
            filter: placement => (placement.Id == created.Id)
        );
        Assert.Equal(
            actual: installs,
            expected: 0
        );
    }
    // The other direction: a removal of a placement an earlier arm of the same firing creates is valid as a sequence
    // and is judged as one, so the firing commits and both arms run.
    [Fact]
    public void ARemovalOfWhatAnEarlierArmCreatesFires() {
        var created = (Placement(
            definition: Fixtures.BuildCameraBodyDocument(),
            id: PlacementId
        ) with { Id = "created-then-removed" });

        using var fixture = Fixtures.FreshServer(definition: Sequence(
            new WorldEffect.UpsertPlacement(Placement: created),
            new WorldEffect.RemovePlacement(Id: created.Id)
        ));
        var installs = 0;

        fixture.Server.MutationJournalTap = (_, _, _) => installs++;
        fixture.Step();

        Assert.Equal(
            actual: State(
                definition: fixture.Server.Definition,
                row: "stage"
            ),
            expected: 1L
        );
        Assert.DoesNotContain(
            collection: fixture.Server.Definition.Placements,
            filter: placement => (placement.Id == created.Id)
        );
        // The firing's rows are one unit, so they are one install.
        Assert.Equal(
            actual: installs,
            expected: 1
        );
    }
    [Fact]
    public void MixedStateAndPlacementTransactionInstallsBothExactlyOnce() {
        using var fixture = Fixtures.FreshServer(definition: Scenario(includeBadSecondPlacement: false));
        var installs = 0;

        fixture.Server.MutationJournalTap = (_, _, _) => installs++;

        fixture.Step();

        Assert.Equal(
            expected: 1L,
            actual: State(
                definition: fixture.Server.Definition,
                row: "stage"
            )
        );
        Assert.Equal(
            expected: 1L,
            actual: State(
                definition: fixture.Server.Definition,
                row: "prefix"
            )
        );
        Assert.Equal(
            expected: "prefix",
            actual: Text(
                definition: fixture.Server.Definition,
                row: "prefixLabel"
            )
        );
        Assert.Equal(
            expected: 1L,
            actual: State(
                definition: fixture.Server.Definition,
                row: "after"
            )
        );
        Assert.Equal(
            expected: "after",
            actual: Text(
                definition: fixture.Server.Definition,
                row: "label"
            )
        );
        Assert.Equal(
            expected: 1.25f,
            actual: Placement(
                definition: fixture.Server.Definition,
                id: PlacementId
            ).Scale
        );
        Assert.Equal(
            actual: installs,
            expected: 1
        );

        // The gate is closed by the state write. A second fixed step must not replay the document write.
        fixture.Step();
        Assert.Equal(
            expected: 1L,
            actual: State(
                definition: fixture.Server.Definition,
                row: "stage"
            )
        );
        Assert.Equal(
            expected: 1L,
            actual: State(
                definition: fixture.Server.Definition,
                row: "prefix"
            )
        );
        Assert.Equal(
            expected: "prefix",
            actual: Text(
                definition: fixture.Server.Definition,
                row: "prefixLabel"
            )
        );
        Assert.Equal(
            expected: 1L,
            actual: State(
                definition: fixture.Server.Definition,
                row: "after"
            )
        );
        Assert.Equal(
            expected: "after",
            actual: Text(
                definition: fixture.Server.Definition,
                row: "label"
            )
        );
        Assert.Equal(
            expected: 1.25f,
            actual: Placement(
                definition: fixture.Server.Definition,
                id: PlacementId
            ).Scale
        );
    }
    [Fact]
    public void RefusalOfALaterPlacementLeafRollsBackTheWholeFiring() {
        using var fixture = Fixtures.FreshServer(definition: Scenario(includeBadSecondPlacement: true));

        fixture.Step();

        Assert.Equal(
            expected: 0L,
            actual: State(
                definition: fixture.Server.Definition,
                row: "prefix"
            )
        );
        Assert.Equal(
            expected: "before",
            actual: Text(
                definition: fixture.Server.Definition,
                row: "prefixLabel"
            )
        );
        Assert.Equal(
            expected: 0L,
            actual: State(
                definition: fixture.Server.Definition,
                row: "stage"
            )
        );
        Assert.Equal(
            expected: 0L,
            actual: State(
                definition: fixture.Server.Definition,
                row: "after"
            )
        );
        Assert.Equal(
            expected: "before",
            actual: Text(
                definition: fixture.Server.Definition,
                row: "label"
            )
        );
        Assert.Equal(
            expected: 1f,
            actual: Placement(
                definition: fixture.Server.Definition,
                id: PlacementId
            ).Scale
        );
        Assert.DoesNotContain(
            expected: "missing",
            collection: fixture.Server.Definition.Placements.Select(selector: static placement => placement.Id)
        );
    }
    [Fact]
    public void StandalonePlacementKeepsOrderedStateAndDocumentEffects() {
        using var fixture = Fixtures.FreshServer(definition: Scenario(
            includeBadSecondPlacement: false,
            transaction: false
        ));

        fixture.Step();

        Assert.Equal(
            expected: 1L,
            actual: State(
                definition: fixture.Server.Definition,
                row: "prefix"
            )
        );
        Assert.Equal(
            expected: "prefix",
            actual: Text(
                definition: fixture.Server.Definition,
                row: "prefixLabel"
            )
        );
        Assert.Equal(
            expected: 1L,
            actual: State(
                definition: fixture.Server.Definition,
                row: "stage"
            )
        );
        Assert.Equal(
            expected: 1.25f,
            actual: Placement(
                definition: fixture.Server.Definition,
                id: PlacementId
            ).Scale
        );
        Assert.Equal(
            expected: 1L,
            actual: State(
                definition: fixture.Server.Definition,
                row: "after"
            )
        );
        Assert.Equal(
            expected: "after",
            actual: Text(
                definition: fixture.Server.Definition,
                row: "label"
            )
        );
    }
}
