using System.Diagnostics;
using Puck.Hosting;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

[Collection(ConsoleRedirectionCollection.Name)]
public sealed class WorldDecisionNeighborLawTests {
    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static ExpressionProgram Constant(decimal value) => new(Instructions: [Instruction.Constant(value: value)]);
    private static WorldStateRow Row(string name, params long[] values) => new(
        Name(value: name),
        CellKind.Int,
        Capacity: Math.Max(
            val1: 1,
            val2: values.Length
        ),
        Cells: values.Select(selector: (value, index) => new StateCell(
            Name(value: index.ToString()),
            CellValue.Int(value: value)
        )).ToArray()
    );
    private static WorldDecision Policy(WorldDecisionNeighbors? neighbors = null) => new(
        [
        new(
                Name(value: "companion"),
                new(Instructions: [Instruction.Operand(key: "$right",
                        name: "appeal"
                    )]),
                [new ActionEffect.AddState(
                        "entries",
                        Value: 1
                    )],
                Neighbors: (neighbors ?? new(
                    20,
                    4,
                    3
                ))
            ),
        new(
                Name(value: "alone"),
                Constant(value: 0),
                []
            ),
    ],
        0.01m,
        ScoreKind: CellKind.Int
    );
    private static WorldDefinition Document(WorldDecision? policy = null) => Fixtures.BuildDocument() with {
        StateRaw = new(World: [Row(
            "observers",
            1
        ), Row(
            "appeal",
            0,
            1,
            2,
            3
        ),
            new(
            Name(value: "entries"),
            CellKind.Int,
            Cells: [new(
                    WorldStateRow.SlotKey,
                    CellValue.Int(value: 0)
                )]
        )]),
        Rules = [new(
            Name(value: "choose"),
            [],
            ForEach: "observers",
            Decision: (policy ?? Policy())
        )],
    };
    private static FixedVector3 Position(int x, int z = 0) => new(
        X: FixedQ4816.FromInteger(value: x),
        Y: FixedQ4816.Zero,
        Z: FixedQ4816.FromInteger(value: z)
    );
    private static WorldBody Join(WorldFixture fixture, int index, int x, int z = 0) {
        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            WorldPrincipal.Seat(slot: index),
            index,
            null,
            WorldProtocol.WireProtocolKey
        )).Accepted);
        var body = fixture.Server.Body(index: index)!;

        body.Pose(
            Position(
                x: x,
                z: z
            ),
            FixedQ4816.Zero,
            FixedQ4816.Zero,
            FixedQ4816.Zero
        );
        body.SetIntentSource(source: IntentSource.Idle);
        return body;
    }
    private static WorldAuthorityCheckpoint Capture(WorldFixture fixture) {
        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                new(
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
                ),
                out var checkpoint,
                out var reason
            ),
            userMessage: reason
        );
        return checkpoint!;
    }
    private static WorldDecisionCheckpoint State(WorldFixture fixture) => Assert.Single(collection: Capture(fixture: fixture).Server.Decisions!);
    private static long Entries(WorldFixture fixture) => WorldDefinitionRows.FindStateRow(
        fixture.Server.Definition.State,
        "entries"
    )!.Cells![0].Value.AsInt;
    private static void Appeal(WorldFixture fixture, int index, long value) => fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(
        WorldPrincipal.Console,
        "appeal",
        index.ToString(),
        value,
        WorldDocumentWriteKind.Set
    ));

    [Fact]
    public void ChoicesBindTheCandidateAndSwitchingIndividualsReentersTheOption() {
        using var fixture = Fixtures.FreshServer(Document());

        _ = Join(
            fixture,
            0,
            0
        ); _ = Join(
            fixture,
            1,
            1
        ); _ = Join(
            fixture,
            2,
            2
        );
        fixture.Step(stepTicks: 504);
        Assert.Equal(
            2,
            State(fixture: fixture).Candidate
        ); Assert.Equal(
            1,
            Entries(fixture: fixture)
        );
        fixture.Step(stepTicks: 504); Assert.Equal(
            1,
            Entries(fixture: fixture)
        );
        Appeal(
            fixture: fixture,
            index: 1,
            value: 10
        ); fixture.Step(stepTicks: 504);
        Assert.Equal(
            1,
            State(fixture: fixture).Candidate
        ); Assert.Equal(
            2,
            Entries(fixture: fixture)
        );
        Assert.Contains(
            "candidate=1@",
            fixture.Server.DescribeDecisions()
        );
        Assert.Contains(
            "budget=4,max=3",
            fixture.Server.DescribeDecisions()
        );
    }
    [Fact]
    public void IncumbentBonusBelongsToTheIndividualNotEveryCandidateInItsOption() {
        using var fixture = Fixtures.FreshServer(Document(policy: Policy() with { IncumbentBonus = 10 }));

        _ = Join(
            fixture,
            0,
            0
        ); _ = Join(
            fixture,
            1,
            1
        ); _ = Join(
            fixture,
            2,
            2
        );
        fixture.Step(stepTicks: 504); Assert.Equal(
            2,
            State(fixture: fixture).Candidate
        );
        Appeal(
            fixture: fixture,
            index: 1,
            value: 5
        ); fixture.Step(stepTicks: 504);
        Assert.Equal(
            2,
            State(fixture: fixture).Candidate
        ); Assert.Equal(
            12,
            State(fixture: fixture).LastScore
        );
        Appeal(
            fixture: fixture,
            index: 1,
            value: 13
        ); fixture.Step(stepTicks: 504); Assert.Equal(
            1,
            State(fixture: fixture).Candidate
        );
    }
    [Fact]
    public void CurrentCompanionGetsOneBudgetedRecheckInsteadOfDependingOnRotatingSampling() {
        var policy = Policy(neighbors: new(
            20,
            1,
            1
        ));
        using var fixture = Fixtures.FreshServer(Document(policy: policy));

        _ = Join(
            fixture,
            0,
            0
        ); _ = Join(
            fixture,
            1,
            1
        ); _ = Join(
            fixture,
            2,
            2
        );
        fixture.Step(stepTicks: 504);
        for (var i = 0; ((i < 4) && (State(fixture: fixture).Candidate < 0)); i++) { fixture.Step(stepTicks: 504); }
        var chosen = State(fixture: fixture).Candidate; Assert.InRange(
            actual: chosen,
            high: 2,
            low: 1
        );
        for (var i = 0; (i < 20); i++) {
            fixture.Step(stepTicks: 504); Assert.Equal(
                chosen,
                State(fixture: fixture).Candidate
            );
            Assert.Equal(
                1,
                fixture.Server.DecisionWork.Inspected
            );
        }
    }
    [Fact]
    public void RetentionDoesNotBypassRangeOrConeAndFixedAloneOptionCanWin() {
        using var fixture = Fixtures.FreshServer(Document(policy: Policy(neighbors: new(
            5,
            4,
            3,
            HalfAngleDegrees: 45
        ))));

        _ = Join(
            fixture,
            0,
            0
        ); var companion = Join(
            fixture: fixture,
            index: 1,
            x: 0,
            z: -2
        ); _ = Join(
            fixture: fixture,
            index: 2,
            x: 0,
            z: 2
        );
        fixture.Step(stepTicks: 504); Assert.Equal(
            1,
            State(fixture: fixture).Candidate
        );
        companion.Pose(
            Position(
                x: 0,
                z: -6
            ),
            FixedQ4816.Zero,
            FixedQ4816.Zero,
            FixedQ4816.Zero
        );
        fixture.Step(stepTicks: 504); Assert.Equal(
            1,
            State(fixture: fixture).Selected
        ); Assert.Equal(
            -1,
            State(fixture: fixture).Candidate
        );
        companion.Pose(
            Position(
                x: 0,
                z: -4
            ),
            FixedQ4816.Zero,
            FixedQ4816.Zero,
            FixedQ4816.Zero
        );
        fixture.Step(stepTicks: 504); Assert.Equal(
            1,
            State(fixture: fixture).Candidate
        );
    }
    [Fact]
    public void FourQuarterCrowdSamplesFindTheOnlyEligibleCompanionForEveryObserver() {
        var policy = Policy(neighbors: new(
            20,
            32,
            1
        )) with {
            Options = [Policy().Options[0] with {
            Effects = [], Neighbors = new(
                20,
                32,
                1
            ),
            Gate = new ActionPredicate.CompareState(
                "appeal",
                ActionStateComparison.Greater,
                0,
                Key: "$right"
            ),
        }],
        };
        var appeal = new long[128]; appeal[64] = 1;
        var doc = Document(policy: policy);

        doc = doc with {
            PopulationRaw = doc.Population with { CapacityRaw = 128, NetworkPlayers = 124 },
            StateRaw = new(World: [Row(
                "observers",
                Enumerable.Repeat(
                    count: 128,
                    element: 1L
                ).ToArray()
            ), Row(
                "appeal",
                appeal
            )]),
        };
        using var fixture = Fixtures.FreshServer(doc);

        for (var i = 0; (i < 4); i++) {
            _ = Join(
            fixture,
            i,
            0
        );
        }
        Assert.Equal(
            124,
            fixture.Server.Population.SetSimulatedCount(124)
        );
        for (var i = 4; (i < 128); i++) {
            fixture.Server.Body(index: i)!.Pose(
                Position(0),
                FixedQ4816.Zero,
                FixedQ4816.Zero,
                FixedQ4816.Zero
            );
            fixture.Server.Body(index: i)!.SetIntentSource(source: IntentSource.Idle);
        }
        for (var sample = 0; (sample < 4); sample++) {
            fixture.Step(stepTicks: 504);
            Assert.InRange(
                fixture.Server.DecisionWork.Inspected,
                0,
                (128 * 32)
            );
        }
        var states = Capture(fixture: fixture).Server.Decisions!;

        Assert.Equal(
            128,
            states.Count
        );
        foreach (var state in states) {
            Assert.Equal(
                ((state.Key == 64)
                ? -1
                : 64),
                state.Candidate
            );
            Assert.Equal(
                0UL,
                state.DrawCount
            );
        }
    }
    [Fact]
    public void PerceptionBudgetSharesScalesAndDoesNotDiscountAlignedReconsiderations() {
        var option = Policy().Options[0] with { Effects = [], Score = Constant(value: 1) };

        WorldDefinition WithRanges(params decimal[] ranges) => Document(policy: Policy() with {
            PeriodSeconds = 100,
            CommitmentSeconds = 100,
            Options = ranges.Select(selector: (range, index) => option with {
                Name = Name(value: $"option{index}"),
                Neighbors = new(
            range,
            4,
            3
        ),
            }).ToArray(),
        });
        var one = WorldRuleWorkBudget.Measure(definition: WithRanges(17));
        var shared = WorldRuleWorkBudget.Measure(definition: WithRanges(
            17,
            20
        ));
        var separate = WorldRuleWorkBudget.Measure(definition: WithRanges(
            17,
            33
        ));
        var capacity = Document().Population.Capacity;

        Assert.Equal(
            capacity,
            one.DecisionImagePointsPerTick
        );
        Assert.Equal(
            1,
            one.DecisionGridBuildsPerTick
        );
        Assert.Equal(
            capacity,
            one.DecisionGridPointsPerTick
        );
        Assert.Equal(
            one.DecisionGridBuildsPerTick,
            shared.DecisionGridBuildsPerTick
        );
        Assert.Equal(
            one.DecisionGridPointsPerTick,
            shared.DecisionGridPointsPerTick
        );
        Assert.Equal(
            2,
            separate.DecisionGridBuildsPerTick
        );
        Assert.Equal(
            (2 * capacity),
            separate.DecisionGridPointsPerTick
        );
        // A second grid keys its points, sorts them, and groups the sorted run.
        Assert.Equal(
            ((2L * capacity) + Puck.State.Rules.RuleWorkBudget.IntrosortWork(count: capacity).Units),
            (separate.WorkUnitsPerTick.Units - shared.WorkUnitsPerTick.Units)
        );
        var noNeighbors = WorldRuleWorkBudget.Measure(definition: Document(policy: Policy() with { Options = [option with { Neighbors = null }] }));

        Assert.Equal(
            0,
            noNeighbors.DecisionImagePointsPerTick
        );
        Assert.Equal(
            0,
            noNeighbors.DecisionGridBuildsPerTick
        );
        Assert.Equal(
            0,
            noNeighbors.DecisionGridPointsPerTick
        );
    }
    [Fact]
    public void LosingCandidateGateInterruptsCommitmentWithTheCorrectRightBinding() {
        var policy = Policy() with { CommitmentSeconds = 10 };

        policy = policy with {
            Options = [policy.Options[0] with {
            Gate = new ActionPredicate.CompareState(
                "appeal",
                ActionStateComparison.Greater,
                0,
                Key: "$right"
            ),
        }, policy.Options[1]],
        };
        using var fixture = Fixtures.FreshServer(Document(policy: policy));

        _ = Join(
            fixture,
            0,
            0
        ); _ = Join(
            fixture,
            1,
            1
        ); _ = Join(
            fixture,
            2,
            2
        );
        fixture.Step(); Assert.Equal(
            2,
            State(fixture: fixture).Candidate
        );
        Appeal(
            fixture: fixture,
            index: 2,
            value: 0
        ); fixture.Step();
        Assert.Equal(
            1,
            State(fixture: fixture).Candidate
        ); Assert.Equal(
            2UL,
            State(fixture: fixture).Reconsiderations
        );
    }
    [Fact]
    public void ReusingTheSelectedSlotCannotInheritCommitmentOrSkipEntryEffects() {
        var doc = Document(policy: Policy() with { CommitmentSeconds = 10 });

        doc = doc with {
            StateRaw = doc.StateRaw! with {
                World = [Row(
                "observers",
                1
            ), Row(
                "appeal",
                0,
                0,
                0,
                0,
                10
            ),
            new(
                Name(value: "entries"),
                CellKind.Int,
                Cells: [new(
                        WorldStateRow.SlotKey,
                        CellValue.Int(value: 0)
                    )]
            )],
            },
            PopulationRaw = doc.Population with { CapacityRaw = 5, NetworkPlayers = 1 },
        };
        using var fixture = Fixtures.FreshServer(doc);

        _ = Join(
            fixture,
            0,
            0
        ); Assert.Equal(
            1,
            fixture.Server.Population.SetSimulatedCount(1)
        );
        fixture.Server.Body(index: 4)!.Pose(
            Position(2),
            FixedQ4816.Zero,
            FixedQ4816.Zero,
            FixedQ4816.Zero
        );
        fixture.Step(); var before = State(fixture: fixture); Assert.Equal(
            4,
            before.Candidate
        );
        fixture.Server.Population.SetSimulatedCount(0); fixture.Server.Population.SetSimulatedCount(1);
        fixture.Server.Body(index: 4)!.Pose(
            Position(2),
            FixedQ4816.Zero,
            FixedQ4816.Zero,
            FixedQ4816.Zero
        );
        fixture.Step(); Assert.NotEqual(
            before.CandidateGeneration,
            State(fixture: fixture).CandidateGeneration
        );
        Assert.Equal(
            2UL,
            State(fixture: fixture).Reconsiderations
        ); Assert.Equal(
            2,
            Entries(fixture: fixture)
        );
    }
    [Fact]
    public void AParameterizedOptionCannotLeakBindingsIntoOtherOptionsOrCommonEffects() {
        var doc = Document();

        Assert.NotNull(@object: ((CompiledWorldFactsRule)WorldFactsCompiler.CompileAll(definition: doc)[0]).Decision!.Options[0].Neighbors);
        var rule = doc.Rules![0];
        var fixedOption = rule.Decision!.Options[1] with {
            Score = new(Instructions: [Instruction.Operand(key: "$right",
                name: "appeal"
            )]),
        };

        Assert.Throws<RuleException>(testCode: () => WorldFactsCompiler.Compile(
            rule with {
                Decision = rule.Decision with { Options = [rule.Decision.Options[0], fixedOption] },
            },
            WorldFactsCompiler.Context(definition: doc)
        ));
        Assert.Throws<RuleException>(testCode: () => WorldFactsCompiler.Compile(
            rule with {
                Effects = [new ActionEffect.SetState(
                    "appeal",
                    Key: "$right",
                    Value: 1
                )],
            },
            WorldFactsCompiler.Context(definition: doc)
        ));
        Assert.Throws<RuleException>(testCode: () => WorldFactsCompiler.Compile(
            rule with { ForEach = null },
            WorldFactsCompiler.Context(definition: doc)
        ));
        Assert.NotNull(@object: ((CompiledWorldFactsRule)WorldFactsCompiler.Compile(
            context: WorldFactsCompiler.Context(definition: doc),
            rule: rule
        )).Decision);
    }
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [Theory]
    public void MalformedNeighborLimitsAreRefused(int variant) {
        var source = new WorldDecisionNeighbors(
            20,
            4,
            3
        );

        source = variant switch {
            0 => source with { Range = 0 },
            1 => source with { Range = 1000001 },
            2 => source with { CandidateBudget = 0 },
            3 => source with { MaxCandidates = 5 },
            4 => source with { HalfAngleDegrees = 0 },
            _ => source with { MaxCandidates = 33, CandidateBudget = 128 },
        };
        Assert.Throws<RuleException>(testCode: () => WorldFactsCompiler.CompileAll(definition: Document(policy: Policy(neighbors: source))));
    }
    [Fact]
    public void StrictRoundTripAndWireCheckpointPreserveFutureNeighborChoicesAndHashes() {
        var doc = Document(policy: Policy() with { Mode = WorldDecisionMode.Weighted });
        var roundTrip = WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: doc));

        Assert.Equal(
            doc.Rules![0].Decision!.Options[0].Neighbors,
            roundTrip.Rules![0].Decision!.Options[0].Neighbors
        );
        using var original = Fixtures.FreshServer(doc);

        for (var i = 0; (i < 4); i++) {
            _ = Join(
            original,
            i,
            i
        );
        }
        original.Step(); var captured = Capture(fixture: original);

        Assert.True(
            condition: WorldAuthorityCheckpointCodec.TryDecode(
                bytes: WorldAuthorityCheckpointCodec.Encode(checkpoint: captured),
                checkpoint: out var decoded,
                reason: out var reason
            ),
            userMessage: reason
        );
        using var restored = Fixtures.FreshServer(doc); restored.Server.RestoreCheckpoint(checkpoint: decoded!);
        for (ulong tick = 1; (tick <= 100); tick++) {
            var context = new FixedStepContext(
                Tick: tick,
                ElapsedTicks: ((tick + 1) * Fixtures.StepTicks),
                StepTicks: Fixtures.StepTicks
            );

            original.Server.Step(context: context); restored.Server.Step(context: context);
            Assert.Equal(
                State(fixture: original),
                State(fixture: restored)
            );
            Assert.Equal(
                WorldStateHashComposition.HashAuthoritative(
                    server: original.Server,
                    tick: tick
                ),
                WorldStateHashComposition.HashAuthoritative(
                    server: restored.Server,
                    tick: tick
                )
            );
        }
    }
    [Fact]
    public void SightOnlyNeighborPolicyBuildsTheFieldAndAcceptsAnUnobstructedCandidate() {
        var doc = Document(policy: Policy(neighbors: new(
            20,
            4,
            3,
            RequiresLineOfSight: true
        )));

        Assert.True(condition: WorldTargetSelection.RequiresLineOfSight(definition: doc));
        using var fixture = Fixtures.FreshServer(doc);

        _ = Join(
            fixture,
            0,
            0
        ); _ = Join(
            fixture,
            1,
            2
        );
        fixture.Step(); Assert.Equal(
            1,
            State(fixture: fixture).Candidate
        );
        Assert.True(condition: (fixture.Server.DecisionWork.SightTests > 0));
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void CoincidentCrowdHasBoundedInspectionAndNoAddedSteadyAllocation(bool rejectAll) {
        var policy = Policy(neighbors: new(
            20,
            32,
            16
        )) with { Mode = WorldDecisionMode.Weighted };

        policy = policy with {
            Options = [policy.Options[0] with {
            Score = Constant(value: 1), Effects = [], Gate = (rejectAll
            ? new ActionPredicate.CompareState(
                    "appeal",
                    ActionStateComparison.Less,
                    0,
                    Key: "$right"
                )
            : null),
        }],
        };
        var doc = Document(policy: policy);

        doc = doc with {
            PopulationRaw = doc.Population with { CapacityRaw = 128, NetworkPlayers = 124 },
            StateRaw = new(World: [Row(
                "observers",
                Enumerable.Repeat(
                    count: 128,
                    element: 1L
                ).ToArray()
            ), Row(
                "appeal",
                new long[128]
            )]),
        };
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: doc,
                reason: out var reason
            ),
            userMessage: reason
        );
        using var subject = Fixtures.FreshServer(doc);
        using var control = Fixtures.FreshServer(doc with { Rules = [] });

        static void Populate(WorldFixture fixture) {
            for (var i = 0; (i < 4); i++) {
                _ = Join(
                fixture,
                i,
                0
            );
            }
            Assert.Equal(
                124,
                fixture.Server.Population.SetSimulatedCount(124)
            );
            for (var i = 4; (i < 128); i++) {
                fixture.Server.Body(index: i)!.Pose(
                    Position(0),
                    FixedQ4816.Zero,
                    FixedQ4816.Zero,
                    FixedQ4816.Zero
                );
                fixture.Server.Body(index: i)!.SetIntentSource(source: IntentSource.Idle);
            }
        }
        Populate(fixture: subject); Populate(fixture: control);
        // Exercise every body before measuring. Tiered compilation is disabled for allocation laws, so
        // no background promotion needs thousands of warmup calls. Repeated windows isolate one-off bookkeeping.
        static (long Bytes, TimeSpan Time) Run(WorldFixture f) {
            for (var i = 0; (i < 16); i++) {
                f.Step(stepTicks: 504); _ = WorldStateHashComposition.HashAuthoritative(
                server: f.Server,
                tick: 0
            );
            }
            var bestBytes = long.MaxValue;
            var bestTime = TimeSpan.MaxValue;

            for (var sample = 0; (sample < 3); sample++) {
                var bytes = GC.GetAllocatedBytesForCurrentThread();
                var start = Stopwatch.GetTimestamp();

                for (var i = 0; (i < 32); i++) {
                    f.Step(stepTicks: 504); _ = WorldStateHashComposition.HashAuthoritative(
                    server: f.Server,
                    tick: 0
                );
                }

                var allocated = (GC.GetAllocatedBytesForCurrentThread() - bytes);

                if (allocated < bestBytes) {
                    bestBytes = allocated;
                    bestTime = Stopwatch.GetElapsedTime(startingTimestamp: start);
                }
            }

            return (bestBytes, bestTime);
        }
        var measured = Run(f: subject); var baseline = Run(f: control);

        TestContext.Current.TestOutputHelper!.WriteLine(message: $"128 coincident bodies, budget32/max16, rejectAll={rejectAll}: x32 steps+hash {measured.Time.TotalMilliseconds:F3}ms/{measured.Bytes}B; no-decisions {baseline.Time.TotalMilliseconds:F3}ms/{baseline.Bytes}B");
        Assert.Equal(
            actual: measured.Bytes,
            expected: baseline.Bytes
        );
        Assert.Equal(
            (128 * 32),
            subject.Server.DecisionWork.Inspected
        );
        Assert.Equal(
            (rejectAll
            ? 0
            : (128 * 16)),
            subject.Server.DecisionWork.Scored
        );
        Assert.Equal(
            128,
            subject.Server.DecisionWork.LimitedQueries
        );
    }
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [Theory]
    public void MalformedCandidateCheckpointRefusesWithoutChangingAuthority(int variant) {
        using var fixture = Fixtures.FreshServer(Document());

        _ = Join(
            fixture,
            0,
            0
        ); _ = Join(
            fixture,
            1,
            2
        ); fixture.Step();
        var captured = Capture(fixture: fixture); var original = Assert.Single(collection: captured.Server.Decisions!);
        var invalid = variant switch {
            0 => original with { Candidate = -1 },
            1 => original with { Candidate = 128 },
            2 => original with { CandidateGeneration = -1 },
            _ => original with { Candidate = original.Key },
        };
        var before = WorldStateHashComposition.HashAuthoritative(
            server: fixture.Server,
            tick: 0
        );

        Assert.Throws<InvalidOperationException>(testCode: () => fixture.Server.RestoreCheckpoint(checkpoint: captured with { Server = captured.Server with { Decisions = [invalid] } }));
        Assert.Equal(
            before,
            WorldStateHashComposition.HashAuthoritative(
                server: fixture.Server,
                tick: 0
            )
        );
    }
    [Fact]
    public void CommittedWorldEffectsRoundTripButRemainForbiddenAtBothLiveCodecDoors() {
        var effect = new WorldMutation.UpsertStateCell(
            WorldPrincipal.World,
            "entries",
            "$value",
            1,
            WorldDocumentWriteKind.Set
        );

        Assert.True(
            condition: WorldSubmissionCodec.TryEncodeCommittedMutation(
                bytes: out var bytes,
                failure: out var failure,
                mutation: effect
            ),
            userMessage: failure.ToString()
        );
        Assert.True(
            condition: WorldSubmissionCodec.TryDecodeCommittedMutation(
                bytes: bytes,
                failure: out failure,
                mutation: out var restored
            ),
            userMessage: failure.ToString()
        );
        Assert.Equal(
            actual: restored,
            expected: effect
        );
        Assert.False(condition: WorldSubmissionCodec.TryEncodeMutation(
            bytes: out _,
            failure: out _,
            mutation: effect
        ));
        Assert.False(condition: WorldSubmissionCodec.TryDecodeMutation(
            bytes: bytes,
            failure: out _,
            mutation: out _
        ));
        var external = effect with { Principal = WorldPrincipal.Console };

        Assert.True(
            condition: WorldSubmissionCodec.TryEncodeMutation(
                bytes: out bytes,
                failure: out failure,
                mutation: external
            ),
            userMessage: failure.ToString()
        );
        Assert.True(
            condition: WorldSubmissionCodec.TryDecodeMutation(
                bytes: bytes,
                failure: out failure,
                mutation: out restored
            ),
            userMessage: failure.ToString()
        );
        Assert.Equal(
            actual: restored,
            expected: external
        );
        Assert.False(condition: WorldSubmissionCodec.TryEncodeCommittedMutation(
            effect with { Principal = WorldPrincipal.World with { Index = 1 } },
            out _,
            out _
        ));
    }
}
