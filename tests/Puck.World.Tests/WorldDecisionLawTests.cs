using System.Diagnostics;
using Puck.Hosting;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

// Run allocation/CPU comparisons after parallel collections; concurrent test activity contaminates the full-step probe.
[Collection(ConsoleRedirectionCollection.Name)]
public sealed class WorldDecisionLawTests {
    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static WorldStateRow Slot(string name, long value = 0) => new(
        Name(value: name),
        CellKind.Int,
        Cells: [new(
                WorldStateRow.SlotKey,
                CellValue.Int(value: value)
            )]
    );
    private static ExpressionProgram Score(decimal value) => new(Instructions: [Instruction.Constant(value: value)]);
    private static WorldDecisionOption Option(string name, decimal score, ActionPredicate? gate = null, params ActionEffect[] effects) =>
        new(
            Name(value: name),
            Score(value: score),
            effects,
            gate
        );
    private static ActionPredicate Gate(string row, decimal value = 1) => new ActionPredicate.CompareState(
        row,
        ActionStateComparison.Equal,
        value
    );
    private static WorldRule Rule(WorldDecision policy, string name = "choose", string? forEach = null, ActionPredicate? gate = null) =>
        new(
            Name(value: name),
            [],
            gate,
            ForEach: StateChannelRef.OfNullable(spelling: forEach),
            Decision: policy
        );
    private static WorldDecision Policy(params WorldDecisionOption[] options) => new(
        options,
        0.01m,
        ScoreKind: CellKind.Int
    );
    private static WorldDefinition Document(WorldRule rule, params WorldStateRow[] rows) => Fixtures.BuildDocument() with {
        StateRaw = new(World: rows),
        Rules = [rule],
    };
    private static WorldAuthorityHostRowCheckpoint Host() => new(
        AnnouncedCrossingHolds: [],
        AppliedTransferHighWater: null,
        AppliedTransferIds: [],
        ElapsedEngineTicks: 0,
        ForwardedBodies: [],
        FreshCounter: 0,
        InDoubtTransfers: [],
        IsPaused: false,
        NextTransferId: 1,
        PortalOccupancy: [],
        Retained: false,
        ScheduleAccumulatorTicks: 0,
        SeededArrivals: []
    );
    private static WorldAuthorityCheckpoint Capture(WorldFixture fixture) {
        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                Host(),
                out var captured,
                out var reason
            ),
            userMessage: reason
        );
        return captured!;
    }
    private static WorldDecisionCheckpoint State(WorldFixture fixture, string rule = "choose", int key = -1) =>
        Assert.Single(
            collection: Capture(fixture: fixture).Server.Decisions!,
            predicate: s => ((s.Rule == rule) && (s.Key == key))
        );
    private static long Value(WorldFixture fixture, string row) => WorldDefinitionRows.FindStateRow(
        fixture.Server.Definition.State,
        row
    )!.Cells![0].Value.AsInt;
    private static void Set(WorldFixture fixture, string row, long value) => fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(
        WorldPrincipal.Console,
        row,
        WorldStateRow.SlotKey.Value,
        value,
        WorldDocumentWriteKind.Set
    ));

    [Fact]
    public void HighestScoreFiltersBeforeScoringAndUsesDocumentOrderForNegativeTies() {
        using var fixture = Fixtures.FreshServer(Document(
            Rule(Policy(
                Option(
                    "forbidden",
                    long.MaxValue,
                    Gate("allowed")
                ),
                Option(
                    "first",
                    -7
                ),
                Option(
                    "second",
                    -7
                )
            )),
            Slot("allowed")
        ));

        fixture.Step();
        Assert.Equal(
            1,
            State(fixture).Selected
        );
        Assert.Equal(
            -7,
            State(fixture).LastScore
        );
        Assert.Equal(
            0UL,
            State(fixture).DrawCount
        );
        Assert.Contains(
            "-1:first",
            fixture.Server.DescribeDecisions()
        );
    }
    [Fact]
    public void InvalidScoreCannotWinOrCrashTheDecision() {
        var broken = new WorldDecisionOption(
            Name(value: "broken"),
            new(Instructions: [Instruction.Constant(value: 1),
            Instruction.Constant(value: 0), Instruction.Of(operation: ExpressionOp.Divide)]),
            []
        );
        using var fixture = Fixtures.FreshServer(Document(Rule(Policy(
            broken,
            Option(
                "valid",
                -1
            )
        ))));

        fixture.Step();
        Assert.Equal(
            1,
            State(fixture).Selected
        );
    }
    [Fact]
    public void StayingDoesNotRepeatEffectsOrRenewCommitment() {
        var policy = Policy(Option(
            "stay",
            1,
            effects: [new ActionEffect.AddState(
                    "entered",
                    Value: 1
                )]
        )) with {
            PeriodSeconds = 0.01m,
            CommitmentSeconds = 0.03m,
        };
        using var fixture = Fixtures.FreshServer(Document(
            Rule(policy),
            Slot("entered")
        ));

        fixture.Step(stepTicks: 504);
        Assert.Equal(
            1512UL,
            State(fixture).CommitmentRemaining
        );
        for (var index = 0; (index < 10); index++) { fixture.Step(stepTicks: 504); }
        Assert.Equal(
            1,
            Value(
                fixture: fixture,
                row: "entered"
            )
        );
        Assert.Equal(
            0UL,
            State(fixture).CommitmentRemaining
        );
        Assert.True(condition: (State(fixture).Reconsiderations > 1));
    }
    [Fact]
    public void CommitmentBlocksOrdinaryChangesButNotLostEligibility() {
        var policy = Policy(
            Option(
                "safe",
                1,
                Gate("safe")
            ),
            new(
                Name(value: "tempting"),
                new(Instructions: [Instruction.Operand(name: "score")]),
                []
            )
        ) with { CommitmentSeconds = 10 };
        using var fixture = Fixtures.FreshServer(Document(
            Rule(policy),
            Slot(
                name: "safe",
                value: 1
            ),
            Slot("score")
        ));

        fixture.Step(stepTicks: 504);
        Set(
            fixture: fixture,
            row: "score",
            value: 10
        ); fixture.Step(stepTicks: 504);
        Assert.Equal(
            0,
            State(fixture).Selected
        );
        Set(
            fixture: fixture,
            row: "safe",
            value: 0
        ); fixture.Step(stepTicks: 1);
        Assert.Equal(
            1,
            State(fixture).Selected
        );
        Assert.Equal(
            2UL,
            State(fixture).Reconsiderations
        );
    }
    [Fact]
    public void InterruptIsAnEdgeAndBypassesBothTimers() {
        var policy = Policy(
            Option(
                "a",
                1
            ),
            Option(
                "b",
                1
            )
        ) with {
            Mode = WorldDecisionMode.Weighted,
            PeriodSeconds = 10,
            CommitmentSeconds = 10,
            Interrupt = Gate("alarm"),
        };
        using var fixture = Fixtures.FreshServer(Document(
            Rule(policy),
            Slot("alarm")
        ));

        fixture.Step();
        Set(
            fixture: fixture,
            row: "alarm",
            value: 1
        ); fixture.Step();
        Assert.Equal(
            2UL,
            State(fixture).Reconsiderations
        );
        for (var index = 0; (index < 10); index++) { fixture.Step(); }
        Assert.Equal(
            2UL,
            State(fixture).Reconsiderations
        );
        Assert.Equal(
            4UL,
            State(fixture).DrawCount
        );
        Set(
            fixture: fixture,
            row: "alarm",
            value: 0
        ); fixture.Step();
        Set(
            fixture: fixture,
            row: "alarm",
            value: 1
        ); fixture.Step();
        Assert.Equal(
            3UL,
            State(fixture).Reconsiderations
        );
    }
    [Fact]
    public void NoChoiceAndGateClosureAreTransitionsNotLevelEffects() {
        var policy = Policy(Option(
            "choice",
            1,
            Gate("eligible")
        )) with {
            OnNoChoice = [new ActionEffect.AddState(
                "empty",
                Value: 1
            )],
        };
        using var fixture = Fixtures.FreshServer(Document(
            Rule(
                policy,
                gate: Gate("enabled")
            ),
            Slot("eligible"),
            Slot(
                name: "enabled",
                value: 1
            ),
            Slot("empty")
        ));

        for (var index = 0; (index < 5); index++) { fixture.Step(stepTicks: 504); }
        Assert.Equal(
            1,
            Value(
                fixture: fixture,
                row: "empty"
            )
        );
        Set(
            fixture: fixture,
            row: "eligible",
            value: 1
        ); fixture.Step(stepTicks: 504);
        Assert.Equal(
            0,
            State(fixture).Selected
        );
        Set(
            fixture: fixture,
            row: "enabled",
            value: 0
        ); fixture.Step(stepTicks: 504);
        Assert.Equal(
            2,
            Value(
                fixture: fixture,
                row: "empty"
            )
        );
        Assert.Equal(
            -1,
            State(fixture).Selected
        );
        fixture.Step(stepTicks: 504);
        Assert.Equal(
            2,
            Value(
                fixture: fixture,
                row: "empty"
            )
        );
    }
    [Fact]
    public void WeightedZeroNegativeAndSingleEligibleOptionsConsumeNoRandomness() {
        using var fixture = Fixtures.FreshServer(Document(Rule(Policy(
            Option(
                "zero",
                0
            ),
            Option(
                "negative",
                -1
            )
        ) with { Mode = WorldDecisionMode.Weighted })));

        fixture.Step();
        Assert.Equal(
            -1,
            State(fixture).Selected
        );
        Assert.Equal(
            0UL,
            State(fixture).DrawCount
        );
        using var single = Fixtures.FreshServer(Document(Rule(Policy(
            Option(
                "zero",
                0
            ),
            Option(
                "positive",
                2
            )
        ) with { Mode = WorldDecisionMode.Weighted })));

        single.Step();
        Assert.Equal(
            1,
            State(single).Selected
        );
        Assert.Equal(
            0UL,
            State(single).DrawCount
        );
    }
    [Fact]
    public void WeightedWideTotalsStayInBoundsAndTrackRelativeWeights() {
        // The sum exceeds UInt64: this discriminates against a wrapping cumulative-weight implementation.
        var policy = Policy(
            Option(
                "small",
                (long.MaxValue / 3)
            ),
            Option(
                "large",
                long.MaxValue
            ),
            Option(
                "large-two",
                long.MaxValue
            ),
            Option(
                "never",
                0
            )
        ) with { Mode = WorldDecisionMode.Weighted };
        using var fixture = Fixtures.FreshServer(Document(Rule(policy)));
        var counts = new int[4];

        for (var index = 0; (index < 1400); index++) {
            fixture.Step(stepTicks: 504);
            counts[State(fixture).Selected]++;
        }
        Assert.InRange(
            counts[0],
            120,
            280
        );
        Assert.InRange(
            counts[1],
            470,
            740
        );
        Assert.InRange(
            counts[2],
            470,
            740
        );
        Assert.Equal(
            0,
            counts[3]
        );
        Assert.Equal(
            2800UL,
            State(fixture).DrawCount
        );
    }
    [Fact]
    public void UnrelatedDecisionsAndRuleOrderDoNotPerturbTheLocalStream() {
        var policy = Policy(
            Option(
                "a",
                1
            ),
            Option(
                "b",
                3
            )
        ) with { Mode = WorldDecisionMode.Weighted };
        var chosen = Rule(policy);
        using var alone = Fixtures.FreshServer(Document(chosen));
        using var withOther = Fixtures.FreshServer(Document(chosen) with {
            Rules = [Rule(
                policy,
                "other"
            ), chosen],
        });

        for (var index = 0; (index < 8); index++) {
            alone.Step(stepTicks: 504); withOther.Step(stepTicks: 504);
            Assert.Equal(
                State(alone),
                State(withOther)
            );
        }
        Assert.NotEqual(
            State(withOther).RandomState,
            State(
                withOther,
                "other"
            ).RandomState
        );
    }
    [Fact]
    public void GateReopeningDoesNotRestartTheRandomStream() {
        var policy = Policy(
            Option(
                "a",
                1
            ),
            Option(
                "b",
                1
            )
        ) with { Mode = WorldDecisionMode.Weighted };
        using var fixture = Fixtures.FreshServer(Document(
            Rule(
                policy,
                gate: Gate("enabled")
            ),
            Slot(
                name: "enabled",
                value: 1
            )
        ));

        fixture.Step();
        var before = State(fixture);

        Set(
            fixture: fixture,
            row: "enabled",
            value: 0
        ); fixture.Step();
        Assert.Equal(
            before.RandomState,
            State(fixture).RandomState
        );
        Set(
            fixture: fixture,
            row: "enabled",
            value: 1
        ); fixture.Step();
        Assert.Equal(
            4UL,
            State(fixture).DrawCount
        );
        Assert.NotEqual(
            before.RandomState,
            State(fixture).RandomState
        );
    }
    [Fact]
    public void IncumbentBonusPreventsSmallOscillationsAndSaturatesSafely() {
        var policy = Policy(
            Option(
                "first",
                (long.MaxValue - 1)
            ),
            Option(
                "later",
                long.MaxValue,
                Gate("allowed")
            )
        ) with { IncumbentBonus = long.MaxValue };
        using var fixture = Fixtures.FreshServer(Document(
            Rule(policy),
            Slot("allowed")
        ));

        fixture.Step(stepTicks: 504);
        Set(
            fixture: fixture,
            row: "allowed",
            value: 1
        ); fixture.Step(stepTicks: 504);
        Assert.Equal(
            0,
            State(fixture).Selected
        );
        Assert.Equal(
            long.MaxValue,
            State(fixture).LastScore
        );
    }
    [Fact]
    public void BindingKeysAreNotAssumedToBePopulationSlotsAndMissingKeysArePruned() {
        var carriers = new WorldStateRow(
            Name(value: "carriers"),
            CellKind.Int,
            Capacity: 3,
            Cells: [new(
                    Name(value: "1000000"),
                    CellValue.Int(value: 1)
                ), new(
                    Name(value: "0"),
                    CellValue.Int(value: 1)
                )]
        );
        using var fixture = Fixtures.FreshServer(Document(
            Rule(
                Policy(Option(
                    "a",
                    1
                )),
                forEach: "carriers"
            ),
            carriers
        ));

        fixture.Step();
        Assert.Equal(
            new[] { 0, 1000000 },
            Capture(fixture: fixture).Server.Decisions!.Select(selector: s => s.Key)
        );
        fixture.Server.EnqueueMutation(new WorldMutation.RemoveStateCell(
            WorldPrincipal.Console,
            "carriers",
            "1000000"
        ));
        fixture.Step();
        Assert.Single(collection: Capture(fixture: fixture).Server.Decisions!);
    }
    [Fact]
    public void CheckpointWireRoundTripPreservesEveryFutureChoiceAndHash() {
        var policy = Policy(
            Option(
                "a",
                1
            ),
            Option(
                "b",
                2
            )
        ) with { Mode = WorldDecisionMode.Weighted, CommitmentSeconds = 0.03m };
        using var original = Fixtures.FreshServer(Document(Rule(policy)));

        for (var index = 0; (index < 5); index++) { original.Step(); }
        var captured = Capture(fixture: original);

        Assert.True(
            condition: WorldAuthorityCheckpointCodec.TryDecode(
                bytes: WorldAuthorityCheckpointCodec.Encode(checkpoint: captured),
                checkpoint: out var decoded,
                reason: out var reason
            ),
            userMessage: reason
        );
        using var restored = Fixtures.FreshServer(Document(Rule(policy)));

        restored.Server.RestoreCheckpoint(checkpoint: decoded!);
        Assert.Equal(
            WorldStateHashComposition.HashAuthoritative(
                server: original.Server,
                tick: 0
            ),
            WorldStateHashComposition.HashAuthoritative(
                server: restored.Server,
                tick: 0
            )
        );
        for (ulong index = 5; (index < 200); index++) {
            var context = new FixedStepContext(
                Tick: index,
                ElapsedTicks: ((index + 1) * Fixtures.StepTicks),
                StepTicks: Fixtures.StepTicks
            );

            original.Server.Step(context: in context); restored.Server.Step(context: in context);
            Assert.Equal(
                WorldStateHashComposition.HashAuthoritative(
                    server: original.Server,
                    tick: index
                ),
                WorldStateHashComposition.HashAuthoritative(
                    server: restored.Server,
                    tick: index
                )
            );
        }
    }
    [Fact]
    public void RecompilingUnchangedPolicyPreservesStateButEditingPolicyRestartsIt() {
        var rule = Rule(Policy(
            Option(
                "a",
                1
            ),
            Option(
                "b",
                1
            )
        ) with { Mode = WorldDecisionMode.Weighted, PeriodSeconds = 10 });
        using var fixture = Fixtures.FreshServer(Document(rule));

        fixture.Step();
        var before = State(fixture);

        fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateRow(
            Principal: WorldPrincipal.Console,
            Row: Slot("unrelated")
        ));
        fixture.Step();
        Assert.Equal(
            before.RandomState,
            State(fixture).RandomState
        );
        Assert.Equal(
            before.DrawCount,
            State(fixture).DrawCount
        );
        Assert.Equal(
            before.Reconsiderations,
            State(fixture).Reconsiderations
        );
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertWorldRule(
            Principal: WorldPrincipal.Console,
            Rule: rule with {
                Decision = rule.Decision! with { Seed = 99 },
            }
        ));
        fixture.Step();
        Assert.Equal(
            1UL,
            State(fixture).Reconsiderations
        );
        Assert.NotEqual(
            before.RandomState,
            State(fixture).RandomState
        );
    }
    [Fact]
    public void ANewBodyGenerationCannotInheritAPreviousOccupantsChoiceStream() {
        var carriers = new WorldStateRow(
            Name(value: "carriers"),
            CellKind.Int,
            Capacity: 1,
            Cells: [new(
                    Name(value: "0"),
                    CellValue.Int(value: 1)
                )]
        );
        using var fixture = Fixtures.FreshServer(Document(
            Rule(
                Policy(
                    Option(
                        "a",
                        1
                    ),
                    Option(
                        "b",
                        1
                    )
                ) with {
                    Mode = WorldDecisionMode.Weighted,
                    PeriodSeconds = 10,
                },
                forEach: "carriers"
            ),
            carriers
        ));

        fixture.Step();
        var before = State(
            fixture,
            key: 0
        );

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            WorldPrincipal.Seat(slot: 0),
            0,
            null,
            WorldProtocol.WireProtocolKey
        )).Accepted);
        fixture.Step();
        var after = State(
            fixture,
            key: 0
        );

        Assert.NotEqual(
            before.Generation,
            after.Generation
        );
        Assert.NotEqual(
            before.RandomState,
            after.RandomState
        );
        Assert.Equal(
            1UL,
            after.Reconsiderations
        );
    }
    [Fact]
    public void NumericAliasesOfABindingAreEvaluatedOnce() {
        var carriers = new WorldStateRow(
            Name(value: "carriers"),
            CellKind.Int,
            Capacity: 2,
            Cells: [new(
                    Name(value: "0"),
                    CellValue.Int(value: 1)
                ), new(
                    Name(value: "00"),
                    CellValue.Int(value: 1)
                )]
        );
        using var fixture = Fixtures.FreshServer(Document(
            Rule(
                Policy(Option(
                    "a",
                    1
                )),
                forEach: "carriers"
            ),
            carriers
        ));

        fixture.Step();
        var state = Assert.Single(collection: Capture(fixture: fixture).Server.Decisions);

        Assert.Equal(
            0,
            state.Key
        );
        Assert.Equal(
            1UL,
            state.Reconsiderations
        );
    }
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [Theory]
    public void InvalidCheckpointRefusesBeforeChangingAuthority(int variant) {
        using var fixture = Fixtures.FreshServer(Document(Rule(Policy(Option(
            "a",
            1
        )))));

        fixture.Step();
        var captured = Capture(fixture: fixture);
        var state = Assert.Single(collection: captured.Server.Decisions!);
        var invalid = variant switch {
            0 => state with { Selected = 1 },
            1 => state with { PeriodRemaining = ulong.MaxValue },
            2 => state with { DrawCount = 2 },
            3 => state with { Key = 0 },
            _ => state with { Evaluated = false },
        };
        var before = WorldStateHashComposition.HashAuthoritative(
            server: fixture.Server,
            tick: 0
        );

        Assert.Throws<InvalidOperationException>(testCode: () => fixture.Server.RestoreCheckpoint(checkpoint: captured with {
            Server = captured.Server with { Decisions = [invalid] },
        }));
        Assert.Equal(
            before,
            WorldStateHashComposition.HashAuthoritative(
                server: fixture.Server,
                tick: 0
            )
        );
    }
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [Theory]
    public void MalformedPolicyIsRefusedAtCompilation(int variant) {
        var policy = Policy(Option(
            "a",
            1
        ));

        policy = variant switch {
            0 => policy with { PeriodSeconds = 0 },
            1 => policy with { CommitmentSeconds = -1 },
            2 => policy with { PeriodSeconds = 0.000001m },
            3 => policy with { IncumbentBonus = -1 },
            4 => policy with {
                Options = [Option(
                "a",
                1
            ), Option(
                "a",
                2
            )],
            },
            _ => policy with { ScoreKind = CellKind.Bool },
        };
        Assert.Throws<RuleException>(testCode: () => WorldFactsCompiler.CompileAll(definition: Document(Rule(policy))));
    }
    [Fact]
    public void DecisionCannotCombineAnEdgeLatchOrMismatchedScoreKinds() {
        var rule = Rule(Policy(Option(
            "a",
            1
        )));

        Assert.Throws<RuleException>(testCode: () => WorldFactsCompiler.CompileAll(definition: Document(rule with { Mode = ActionTriggerMode.Edge })));
        var typed = Policy(new WorldDecisionOption(
            Name(value: "a"),
            new(Instructions: [Instruction.Operand(name: "score")]),
            []
        )) with { ScoreKind = CellKind.Fixed };

        Assert.Throws<RuleException>(testCode: () => WorldFactsCompiler.CompileAll(definition: Document(
            Rule(typed),
            Slot(
                name: "score",
                value: 1
            )
        )));
    }
    [Fact]
    public void StrictDocumentRoundTripIncludesDecisionsAndTheirFullWorkCost() {
        var rule = Rule(Policy(
            Option(
                "a",
                1
            ),
            Option(
                "b",
                2
            )
        ));
        var document = Document(rule);
        var roundTrip = WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: document));

        Assert.Equal(
            ((CompiledWorldFactsRule)WorldFactsCompiler.CompileAll(definition: document)[0]).Decision!.PolicyIdentity,
            ((CompiledWorldFactsRule)WorldFactsCompiler.CompileAll(definition: roundTrip)[0]).Decision!.PolicyIdentity
        );
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: roundTrip,
                reason: out var reason
            ),
            userMessage: reason
        );
        var one = WorldRuleWorkBudget.Measure(definition: Document(Rule(Policy(Option(
            "a",
            1
        ))))).WorkUnitsPerTick.Units;

        Assert.True(condition: (WorldRuleWorkBudget.Measure(definition: document).WorkUnitsPerTick.Units > one));
    }
    [InlineData(1)]
    [InlineData(32)]
    [Theory]
    public void DenseDecisionsAndHashesAddNoSteadyStateAllocation(int policyCount) {
        var carriers = new WorldStateRow(
            Name(value: "carriers"),
            CellKind.Int,
            Capacity: 128,
            Cells: Enumerable.Range(
                count: 128,
                start: 0
            ).Select(selector: i => new StateCell(
                Name(value: i.ToString()),
                CellValue.Int(value: 1)
            )).ToArray()
        );
        var policy = Policy(Enumerable.Range(
            count: 32,
            start: 0
        ).Select(selector: i => Option(
            $"option-{i}",
            (i + 1)
        )).ToArray()) with { Mode = WorldDecisionMode.Weighted };
        var document = Document(
            Rule(
                policy,
                forEach: "carriers"
            ),
            carriers
        ) with {
            Rules = Enumerable.Range(
            count: policyCount,
            start: 0
        ).Select(selector: i => Rule(
            policy,
            $"policy-{i}",
            "carriers"
        )).ToArray(),
        };

        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: document,
                reason: out var reason
            ),
            userMessage: reason
        );
        using var fixture = Fixtures.FreshServer(document);

        for (var index = 0; (index < 8); index++) {
            fixture.Step(stepTicks: 504);
            _ = WorldStateHashComposition.HashAuthoritative(
                server: fixture.Server,
                tick: 0
            );
        }
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();

        for (var index = 0; (index < 16); index++) {
            fixture.Step(stepTicks: 504);
            _ = WorldStateHashComposition.HashAuthoritative(
                server: fixture.Server,
                tick: 0
            );
        }
        var elapsed = Stopwatch.GetElapsedTime(startingTimestamp: start);
        var allocated = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);

        TestContext.Current.TestOutputHelper!.WriteLine(message: $"{(policyCount * 128)} bindings x 32 options, 16 full decision steps + authoritative hashes: {elapsed.TotalMilliseconds:F3} ms, {allocated} allocated bytes");
        using var baseline = Fixtures.FreshServer(fixture.Server.Definition with { Rules = [] });

        for (var index = 0; (index < 8); index++) {
            baseline.Step(stepTicks: 504);
            _ = WorldStateHashComposition.HashAuthoritative(
                server: baseline.Server,
                tick: 0
            );
        }
        var baselineBefore = GC.GetAllocatedBytesForCurrentThread();

        for (var index = 0; (index < 16); index++) {
            baseline.Step(stepTicks: 504);
            _ = WorldStateHashComposition.HashAuthoritative(
                server: baseline.Server,
                tick: 0
            );
        }
        var baselineAllocated = (GC.GetAllocatedBytesForCurrentThread() - baselineBefore);

        TestContext.Current.TestOutputHelper!.WriteLine(message: $"Same world without decisions: {baselineAllocated} allocated bytes");
        Assert.Equal(
            actual: allocated,
            expected: baselineAllocated
        );
        Assert.Equal(
            (policyCount * 128),
            Capture(fixture: fixture).Server.Decisions!.Count
        );
    }
}
