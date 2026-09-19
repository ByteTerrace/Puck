using System.Text;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>CONTRACT UNDER TEST: the <c>verdict</c> state-row trait. It round-trips through the document
/// serializer, refuses by name every shape a reader of the export could not answer from (a missing status cell, a
/// status carrying a code with no firing stamp beside it, a non-Int row, a slot row, a field or host-owned row, an
/// envelope refusing the unevaluated code or declaring a ceiling a tick cannot fit, a value-over-time trait, a cell
/// ceiling with no room for the stamp, a blank or over-long gate, and the row-count ceiling). The declaration-hash
/// half of the contract — the trait folds only when authored — is in
/// <c>tests/Puck.World.Tests</c>'s <c>WorldVerdictHashLawTests</c>, where the hash walk is reachable.</summary>
public sealed class WorldVerdictLawTests {
    private static WorldDefinition BuildDefinition(params WorldStateRow[] rows) => new(
        Simulation: new WorldSimulationDefaults(RateHz: 240),
        StateRaw: new WorldStateSection(World: rows)
    );
    private static WorldStateRow Plain() => new(
        Name: CellName.Parse(candidate: "phaseAdvances"),
        Kind: CellKind.Int,
        Cells: [
            new StateCell(
                Key: CellName.Parse(candidate: "ok"),
                Value: CellValue.Int(value: 0L)
            ),
            new StateCell(
                Key: CellName.Parse(candidate: "generation"),
                Value: CellValue.Int(value: 0L)
            ),
        ]
    );
    private static WorldStateRow Verdict(string gate = "turnPhase's generation advanced past 0", string status = "ok") => Plain() with {
        Verdict = new WorldVerdictTrait(
            Gate: gate,
            Status: CellName.Parse(candidate: status)
        ),
    };
    private static string Validate(WorldDefinition definition) =>
        (WorldDefinitionValidator.TryValidateLocally(
            definition: definition,
            reason: out var reason
        )
            ? string.Empty
            : reason
        );

    [Fact]
    public void AVerdictRowRoundTripsThroughTheDocumentSerializer() {
        var written = Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.Serialize(definition: BuildDefinition(Verdict())));

        Assert.Contains(
            actualString: written,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "\"verdict\""
        );

        var parsed = WorldDefinitionSerialization.Deserialize(utf8Json: Encoding.UTF8.GetBytes(s: written));

        Assert.Equal(
            actual: parsed.State[0].Verdict?.Gate,
            expected: "turnPhase's generation advanced past 0"
        );
        Assert.Equal(
            actual: parsed.State[0].Verdict?.Status.Value,
            expected: "ok"
        );
        Assert.Equal(
            actual: Validate(definition: parsed),
            expected: string.Empty
        );
    }
    [Fact]
    public void AVerdictFreeRowWritesNoVerdictMember() {
        var plain = BuildDefinition(Plain());

        Assert.DoesNotContain(
            actualString: Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.Serialize(definition: plain)),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "\"verdict\""
        );
        Assert.Equal(
            actual: Validate(definition: plain),
            expected: string.Empty
        );
    }
    [Fact]
    public void AGateThatIsBlankOrTooLongIsRefusedByName() {
        Assert.Contains(
            actualString: Validate(definition: BuildDefinition(Verdict(gate: "  "))),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: ".gate is required"
        );
        Assert.Contains(
            actualString: Validate(definition: BuildDefinition(Verdict(gate: new string(
                c: 'g',
                count: (WorldVerdict.MaxGateLength + 1)
            )))),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: $"exceeds {WorldVerdict.MaxGateLength}"
        );
    }
    [Fact]
    public void AStatusNamingNoCellOfTheRowIsRefusedByName() {
        Assert.Contains(
            actualString: Validate(definition: BuildDefinition(Verdict(status: "absent"))),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "names no cell of row 'phaseAdvances'"
        );
    }
    [Fact]
    public void AStatusCellAuthoredAnythingButNotEvaluatedIsRefusedByName() {
        var row = Verdict();

        Assert.Contains(
            actualString: Validate(definition: BuildDefinition(row with {
                Cells = [
                    new StateCell(
                        Key: CellName.Parse(candidate: "ok"),
                        Value: CellValue.Int(value: WorldVerdict.Pass)
                    ),
                    new StateCell(
                        Key: CellName.Parse(candidate: "generation"),
                        Value: CellValue.Int(value: 0L)
                    ),
                ],
            })),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "reads as never evaluated"
        );
    }
    [Fact]
    public void ANonIntRowAndASlotRowAreRefusedByName() {
        Assert.Contains(
            actualString: Validate(definition: BuildDefinition(Verdict() with {
                Kind = CellKind.Text,
                Cells = [
                    new StateCell(
                        Key: CellName.Parse(candidate: "ok"),
                        Value: CellValue.Text(value: string.Empty)
                    ),
                ],
            })),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "so the row is kind Int"
        );
        Assert.Contains(
            actualString: Validate(definition: BuildDefinition(Verdict(status: WorldStateRow.SlotKey.Value) with {
                Cells = [
                    new StateCell(
                        Key: WorldStateRow.SlotKey,
                        Value: CellValue.Int(value: 0L)
                    ),
                ],
                Domain = StateDomain.Slot.Instance,
            })),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "is declared on a slot row"
        );
    }
    [Fact]
    public void AnEnvelopeRefusingAStatusCodeIsRefusedByName() {
        Assert.Contains(
            actualString: Validate(definition: BuildDefinition(Verdict() with { Min = 1L })),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: $"refuses the unevaluated status code {WorldVerdict.NotEvaluated}"
        );
    }
    [Fact]
    public void ADeclaredCeilingIsRefusedByNameBecauseTheRowHoldsTheFiringTick() {
        Assert.Contains(
            actualString: Validate(definition: BuildDefinition(Verdict() with { Max = 2L })),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: $"no authored ceiling bounds a tick"
        );
    }
    [Fact]
    public void EveryValueOverTimeTraitOnAVerdictRowOrItsCellsIsRefusedByName() {
        foreach (var moved in new WorldStateRow[] {
            (Verdict() with {
                Advance = new StateAdvance(
                    PerSecondDenominator: 1,
                    PerSecondNumerator: 1
                ),
            }),
            (Verdict() with { Dynamics = new StateDynamics(Row: CellName.Parse(candidate: "ease")) }),
            (Verdict() with { ValuesFrom = "elsewhere" }),
            (Verdict() with {
                Cells = [
                    new StateCell(
                        Advance: new StateAdvance(
                        PerSecondDenominator: 1,
                        PerSecondNumerator: 1
                    ),
                        Key: CellName.Parse(candidate: "ok"),
                        Value: CellValue.Int(value: 0L)
                    ),
                    new StateCell(
                        Key: CellName.Parse(candidate: "generation"),
                        Value: CellValue.Int(value: 0L)
                    ),
                ],
            }),
            (Verdict() with {
                Cells = [
                    new StateCell(
                        Clock: new StateCellClock(EpochTick: 3L),
                        Key: CellName.Parse(candidate: "ok"),
                        Value: CellValue.Int(value: 0L)
                    ),
                    new StateCell(
                        Key: CellName.Parse(candidate: "generation"),
                        Value: CellValue.Int(value: 0L)
                    ),
                ],
            }),
        }) {
            Assert.Contains(
                actualString: Validate(definition: BuildDefinition(moved)),
                comparisonType: StringComparison.Ordinal,
                expectedSubstring: "a value-over-time trait moves a verdict's cells with no rule behind it"
            );
        }
    }
    [Fact]
    public void AStatusBesideAFiringStampRoundTripsAndOneWithoutItRefuses() {
        var fired = Verdict() with {
            Cells = [
                new StateCell(
                    Key: CellName.Parse(candidate: "ok"),
                    Value: CellValue.Int(value: WorldVerdict.Pass)
                ),
                new StateCell(
                    Key: CellName.Parse(candidate: "generation"),
                    Value: CellValue.Int(value: 1L)
                ),
                new StateCell(
                    Key: WorldVerdict.FiredTickKey,
                    Value: CellValue.Int(value: 7L)
                ),
            ],
        };

        Assert.Equal(
            actual: Validate(definition: BuildDefinition(fired)),
            expected: string.Empty
        );
        Assert.Contains(
            actualString: Validate(definition: BuildDefinition(fired with {
                Cells = [
                    fired.Cells![0],
                    fired.Cells[1],
                    (fired.Cells[2] with { Value = CellValue.Int(value: 0L) }),
                ],
            })),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: $"with no '{WorldVerdict.FiredTickKey}' stamp"
        );
    }
    [Fact]
    public void TheFiringStampIsAReservedCellOnlyAVerdictRowMints() {
        var stamped = new StateCell(
            Key: WorldVerdict.FiredTickKey,
            Value: CellValue.Int(value: 4L)
        );

        Assert.Contains(
            actualString: Validate(definition: BuildDefinition(Plain() with {
                Cells = [
                    Plain().Cells![0],
                    Plain().Cells![1],
                    stamped,
                ],
            })),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "reserved cell keys are engine-minted, and this row mints none by that name"
        );
        Assert.Equal(
            actual: Validate(definition: BuildDefinition(Verdict() with {
                Cells = [
                    Verdict().Cells![0],
                    Verdict().Cells![1],
                    stamped,
                ],
            })),
            expected: string.Empty
        );
    }
    [Fact]
    public void ACellCeilingWithNoRoomForTheStampIsRefusedByName() {
        Assert.Contains(
            actualString: Validate(definition: BuildDefinition(Verdict() with { Capacity = 2 })),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: $"leaves no room beside its 2 declared cell(s) for the engine's '{WorldVerdict.FiredTickKey}' stamp"
        );
        Assert.Equal(
            actual: Validate(definition: BuildDefinition(Verdict() with { Capacity = 3 })),
            expected: string.Empty
        );
    }
    [Fact]
    public void EveryDoorButARuleEffectRefusesAVerdictWriteWithOneText() {
        var refusal = WorldVerdict.RefuseWrite(row: CellName.Parse(candidate: "phaseAdvances"));

        Assert.Contains(
            actualString: refusal,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "'phaseAdvances'"
        );
        Assert.Contains(
            actualString: refusal,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: WorldVerdict.FiredTickKey.Value
        );
    }
    [Fact]
    public void MoreVerdictRowsThanTheCeilingAreRefusedByName() {
        var rows = new WorldStateRow[(WorldVerdict.MaxRows + 1)];

        for (var index = 0; (index < rows.Length); index++) {
            rows[index] = Verdict() with { Name = CellName.Parse(candidate: $"verdict{index}") };
        }

        Assert.Contains(
            actualString: Validate(definition: BuildDefinition(rows)),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: $"verdict rows, which exceeds {WorldVerdict.MaxRows}"
        );
    }
    [Fact]
    public void TheStatusCodesAreDistinctAndOnlyPassPasses() {
        Assert.True(condition: WorldVerdict.IsPass(status: WorldVerdict.Pass));
        Assert.False(condition: WorldVerdict.IsPass(status: WorldVerdict.Fail));
        Assert.False(condition: WorldVerdict.IsPass(status: WorldVerdict.NotEvaluated));
        Assert.False(condition: WorldVerdict.IsPass(status: 7L));
        Assert.Equal(
            actual: WorldVerdict.Describe(status: WorldVerdict.NotEvaluated),
            expected: "never evaluated"
        );
        Assert.Equal(
            actual: WorldVerdict.Describe(status: 7L),
            expected: "status 7"
        );
    }
    [Fact]
    public void TheLiveTouchedRowWalkAndTheBootLoaderRefuseAnAuthoredPassWithOneText() {
        var definition = BuildDefinition(Verdict() with {
            Cells = [
                new StateCell(
                    Key: CellName.Parse(candidate: "ok"),
                    Value: CellValue.Int(value: WorldVerdict.Pass)
                ),
                new StateCell(
                    Key: CellName.Parse(candidate: "generation"),
                    Value: CellValue.Int(value: 0L)
                ),
            ],
        });

        Assert.False(condition: WorldDefinitionValidator.TryValidateTouchedStateRows(
            definition: definition,
            reason: out var live,
            rowNames: ["phaseAdvances"]
        ));
        Assert.NotEmpty(collection: live);
        // The live mutation pipeline and the boot loader refuse the same document by the same words, which is what
        // keeps a value legal in memory and illegal on disk from being possible.
        Assert.Contains(
            actualString: Validate(definition: definition),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: live
        );
    }
}
