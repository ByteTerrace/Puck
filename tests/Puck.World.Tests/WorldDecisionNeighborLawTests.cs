using System.Diagnostics;
using Puck.Hosting;
using Puck.Maths;
using Puck.Physics.Motion;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

[Collection(ConsoleRedirectionCollection.Name)]
public sealed class WorldDecisionNeighborLawTests {
    private static WorldCellName Name(string value) => WorldCellName.Parse(value);
    private static WorldValueExpression Constant(decimal value) => new([new WorldValueToken.Constant(value)]);
    private static WorldStateRow Row(string name, params long[] values) => new(Name(name), CellKind.Int, Capacity: Math.Max(1, values.Length),
        Cells: values.Select((value, index) => new WorldStateCell(Name(index.ToString()), value)).ToArray());
    private static WorldDecision Policy(WorldDecisionNeighbors? neighbors = null) => new([
        new(Name("companion"), new([new WorldValueToken.State("appeal", "$right")]),
            [new ActionEffect.AddState("entries", Value: 1)], Neighbors: neighbors ?? new(20, 4, 3)),
        new(Name("alone"), Constant(0), []),
    ], 0.01m, ScoreKind: CellKind.Int);
    private static WorldDefinition Document(WorldDecision? policy = null) => Fixtures.BuildDocument() with {
        StateRaw = new(World: [Row("observers", 1), Row("appeal", 0, 1, 2, 3),
            new(Name("entries"), CellKind.Int, Cells: [new(WorldStateRow.SlotKey, 0)])]),
        Rules = [new(Name("choose"), [], ForEach: "observers", Decision: policy ?? Policy())],
    };
    private static FixedVector3 Position(int x, int z = 0) => new(FixedQ4816.FromInteger(x), FixedQ4816.Zero, FixedQ4816.FromInteger(z));
    private static WorldBody Join(WorldFixture fixture, int index, int x, int z = 0) {
        Assert.True(fixture.Server.ApplySession(new SessionRequest.Join(WorldPrincipal.Seat(index), index, null, WorldProtocol.WireProtocolKey)).Accepted);
        var body = fixture.Server.Body(index)!;
        body.Pose(Position(x, z), FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.Zero);
        body.SetIntentSource(IntentSource.Idle);
        return body;
    }
    private static WorldAuthorityCheckpoint Capture(WorldFixture fixture) {
        Assert.True(fixture.Server.TryCaptureCheckpoint(new(0, 0, false, [], 1, [], [], [], null, 0, false, [], []), out var checkpoint, out var reason), reason);
        return checkpoint!;
    }
    private static WorldDecisionCheckpoint State(WorldFixture fixture) => Assert.Single(Capture(fixture).Server.Decisions!);
    private static long Entries(WorldFixture fixture) => WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, "entries")!.Cells![0].Value;
    private static void Appeal(WorldFixture fixture, int index, long value) => fixture.Server.EnqueueMutation(
        new WorldMutation.UpsertStateCell(WorldPrincipal.Console, "appeal", index.ToString(), value, WorldDocumentWriteKind.Set));

    [Fact]
    public void ChoicesBindTheCandidateAndSwitchingIndividualsReentersTheOption() {
        using var fixture = Fixtures.FreshServer(Document());
        _ = Join(fixture, 0, 0); _ = Join(fixture, 1, 1); _ = Join(fixture, 2, 2);
        fixture.Step(504);
        Assert.Equal(2, State(fixture).Candidate); Assert.Equal(1, Entries(fixture));
        fixture.Step(504); Assert.Equal(1, Entries(fixture));
        Appeal(fixture, 1, 10); fixture.Step(504);
        Assert.Equal(1, State(fixture).Candidate); Assert.Equal(2, Entries(fixture));
        Assert.Contains("candidate=1@", fixture.Server.DescribeDecisions());
        Assert.Contains("budget=4,max=3", fixture.Server.DescribeDecisions());
    }

    [Fact]
    public void IncumbentBonusBelongsToTheIndividualNotEveryCandidateInItsOption() {
        using var fixture = Fixtures.FreshServer(Document(Policy() with { IncumbentBonus = 10 }));
        _ = Join(fixture, 0, 0); _ = Join(fixture, 1, 1); _ = Join(fixture, 2, 2);
        fixture.Step(504); Assert.Equal(2, State(fixture).Candidate);
        Appeal(fixture, 1, 5); fixture.Step(504);
        Assert.Equal(2, State(fixture).Candidate); Assert.Equal(12, State(fixture).LastScore);
        Appeal(fixture, 1, 13); fixture.Step(504); Assert.Equal(1, State(fixture).Candidate);
    }

    [Fact]
    public void CurrentCompanionGetsOneBudgetedRecheckInsteadOfDependingOnRotatingSampling() {
        var policy = Policy(new(20, 1, 1));
        using var fixture = Fixtures.FreshServer(Document(policy));
        _ = Join(fixture, 0, 0); _ = Join(fixture, 1, 1); _ = Join(fixture, 2, 2);
        fixture.Step(504);
        for (var i = 0; i < 4 && State(fixture).Candidate < 0; i++) { fixture.Step(504); }
        var chosen = State(fixture).Candidate; Assert.InRange(chosen, 1, 2);
        for (var i = 0; i < 20; i++) {
            fixture.Step(504); Assert.Equal(chosen, State(fixture).Candidate);
            Assert.Equal(1, fixture.Server.DecisionWork.Inspected);
        }
    }

    [Fact]
    public void RetentionDoesNotBypassRangeOrConeAndFixedAloneOptionCanWin() {
        using var fixture = Fixtures.FreshServer(Document(Policy(new(5, 4, 3, HalfAngleDegrees: 45))));
        _ = Join(fixture, 0, 0); var companion = Join(fixture, 1, 0, -2); _ = Join(fixture, 2, 0, 2);
        fixture.Step(504); Assert.Equal(1, State(fixture).Candidate);
        companion.Pose(Position(0, -6), FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.Zero);
        fixture.Step(504); Assert.Equal(1, State(fixture).Selected); Assert.Equal(-1, State(fixture).Candidate);
        companion.Pose(Position(0, -4), FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.Zero);
        fixture.Step(504); Assert.Equal(1, State(fixture).Candidate);
    }

    [Fact]
    public void FourQuarterCrowdSamplesFindTheOnlyEligibleCompanionForEveryObserver() {
        var policy = Policy(new(20, 32, 1)) with { Options = [Policy().Options[0] with {
            Effects = [], Neighbors = new(20, 32, 1),
            Gate = new ActionPredicate.CompareState("appeal", ActionStateComparison.Greater, 0, Key: "$right"),
        }] };
        var appeal = new long[128]; appeal[64] = 1;
        var doc = Document(policy);
        doc = doc with { PopulationRaw = doc.Population with { CapacityRaw = 128, NetworkPlayers = 124 },
            StateRaw = new(World: [Row("observers", Enumerable.Repeat(1L, 128).ToArray()), Row("appeal", appeal)]) };
        using var fixture = Fixtures.FreshServer(doc);
        for (var i = 0; i < 4; i++) { _ = Join(fixture, i, 0); }
        Assert.Equal(124, fixture.Server.Population.SetSimulatedCount(124));
        for (var i = 4; i < 128; i++) {
            fixture.Server.Body(i)!.Pose(Position(0), FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.Zero);
            fixture.Server.Body(i)!.SetIntentSource(IntentSource.Idle);
        }
        for (var sample = 0; sample < 4; sample++) {
            fixture.Step(504);
            Assert.InRange(fixture.Server.DecisionWork.Inspected, 0, 128 * 32);
        }
        var states = Capture(fixture).Server.Decisions!;
        Assert.Equal(128, states.Count);
        foreach (var state in states) {
            Assert.Equal(state.Key == 64 ? -1 : 64, state.Candidate);
            Assert.Equal(0UL, state.DrawCount);
        }
    }

    [Fact]
    public void PerceptionBudgetSharesScalesAndDoesNotDiscountAlignedReconsiderations() {
        var option = Policy().Options[0] with { Effects = [], Score = Constant(1) };
        WorldDefinition WithRanges(params decimal[] ranges) => Document(Policy() with {
            PeriodSeconds = 100, CommitmentSeconds = 100,
            Options = ranges.Select((range, index) => option with { Name = Name($"option{index}"), Neighbors = new(range, 4, 3) }).ToArray(),
        });
        var one = WorldRuleWorkBudget.Measure(WithRanges(17));
        var shared = WorldRuleWorkBudget.Measure(WithRanges(17, 20));
        var separate = WorldRuleWorkBudget.Measure(WithRanges(17, 33));
        var capacity = Document().Population.Capacity;
        Assert.Equal(capacity, one.DecisionImagePointsPerTick);
        Assert.Equal(1, one.DecisionGridBuildsPerTick);
        Assert.Equal(capacity, one.DecisionGridPointsPerTick);
        Assert.Equal(one.DecisionGridBuildsPerTick, shared.DecisionGridBuildsPerTick);
        Assert.Equal(one.DecisionGridPointsPerTick, shared.DecisionGridPointsPerTick);
        Assert.Equal(2, separate.DecisionGridBuildsPerTick);
        Assert.Equal(2 * capacity, separate.DecisionGridPointsPerTick);
        Assert.Equal(2 * capacity, separate.WorkUnitsPerTick - shared.WorkUnitsPerTick);
        var noNeighbors = WorldRuleWorkBudget.Measure(Document(Policy() with { Options = [option with { Neighbors = null }] }));
        Assert.Equal(0, noNeighbors.DecisionImagePointsPerTick);
        Assert.Equal(0, noNeighbors.DecisionGridBuildsPerTick);
        Assert.Equal(0, noNeighbors.DecisionGridPointsPerTick);
    }

    [Fact]
    public void LosingCandidateGateInterruptsCommitmentWithTheCorrectRightBinding() {
        var policy = Policy() with { CommitmentSeconds = 10 };
        policy = policy with { Options = [policy.Options[0] with {
            Gate = new ActionPredicate.CompareState("appeal", ActionStateComparison.Greater, 0, Key: "$right"),
        }, policy.Options[1]] };
        using var fixture = Fixtures.FreshServer(Document(policy));
        _ = Join(fixture, 0, 0); _ = Join(fixture, 1, 1); _ = Join(fixture, 2, 2);
        fixture.Step(); Assert.Equal(2, State(fixture).Candidate);
        Appeal(fixture, 2, 0); fixture.Step();
        Assert.Equal(1, State(fixture).Candidate); Assert.Equal(2UL, State(fixture).Reconsiderations);
    }

    [Fact]
    public void ReusingTheSelectedSlotCannotInheritCommitmentOrSkipEntryEffects() {
        var doc = Document(Policy() with { CommitmentSeconds = 10 });
        doc = doc with { StateRaw = doc.StateRaw! with { World = [Row("observers", 1), Row("appeal", 0, 0, 0, 0, 10),
            new(Name("entries"), CellKind.Int, Cells: [new(WorldStateRow.SlotKey, 0)])] },
            PopulationRaw = doc.Population with { CapacityRaw = 5, NetworkPlayers = 1 } };
        using var fixture = Fixtures.FreshServer(doc);
        _ = Join(fixture, 0, 0); Assert.Equal(1, fixture.Server.Population.SetSimulatedCount(1));
        fixture.Server.Body(4)!.Pose(Position(2), FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.Zero);
        fixture.Step(); var before = State(fixture); Assert.Equal(4, before.Candidate);
        fixture.Server.Population.SetSimulatedCount(0); fixture.Server.Population.SetSimulatedCount(1);
        fixture.Server.Body(4)!.Pose(Position(2), FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.Zero);
        fixture.Step(); Assert.NotEqual(before.CandidateGeneration, State(fixture).CandidateGeneration);
        Assert.Equal(2UL, State(fixture).Reconsiderations); Assert.Equal(2, Entries(fixture));
    }

    [Fact]
    public void AParameterizedOptionCannotLeakBindingsIntoOtherOptionsOrCommonEffects() {
        var doc = Document();
        Assert.NotNull(WorldRuleCompiler.CompileAll(doc)[0].Decision!.Options[0].Neighbors);
        var rule = doc.Rules![0];
        var fixedOption = rule.Decision!.Options[1] with { Score = new([new WorldValueToken.State("appeal", "$right")]) };
        Assert.Throws<WorldRuleException>(() => WorldRuleCompiler.Compile(rule with {
            Decision = rule.Decision with { Options = [rule.Decision.Options[0], fixedOption] },
        }, doc));
        Assert.Throws<WorldRuleException>(() => WorldRuleCompiler.Compile(rule with {
            Effects = [new ActionEffect.SetState("appeal", Key: "$right", Value: 1)],
        }, doc));
        Assert.Throws<WorldRuleException>(() => WorldRuleCompiler.Compile(rule with { ForEach = null }, doc));
        Assert.NotNull(WorldRuleCompiler.Compile(rule, doc).Decision);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void MalformedNeighborLimitsAreRefused(int variant) {
        var source = new WorldDecisionNeighbors(20, 4, 3);
        source = variant switch {
            0 => source with { Range = 0 }, 1 => source with { Range = 1000001 }, 2 => source with { CandidateBudget = 0 },
            3 => source with { MaxCandidates = 5 }, 4 => source with { HalfAngleDegrees = 0 }, _ => source with { MaxCandidates = 33, CandidateBudget = 128 },
        };
        Assert.Throws<WorldRuleException>(() => WorldRuleCompiler.CompileAll(Document(Policy(source))));
    }

    [Fact]
    public void StrictRoundTripAndWireCheckpointPreserveFutureNeighborChoicesAndHashes() {
        var doc = Document(Policy() with { Mode = WorldDecisionMode.Weighted });
        var roundTrip = WorldDefinitionSerialization.Deserialize(WorldDefinitionSerialization.Serialize(doc));
        Assert.Equal(doc.Rules![0].Decision!.Options[0].Neighbors, roundTrip.Rules![0].Decision!.Options[0].Neighbors);
        using var original = Fixtures.FreshServer(doc);
        for (var i = 0; i < 4; i++) { _ = Join(original, i, i); }
        original.Step(); var captured = Capture(original);
        Assert.True(WorldAuthorityCheckpointCodec.TryDecode(WorldAuthorityCheckpointCodec.Encode(captured), out var decoded, out var reason), reason);
        using var restored = Fixtures.FreshServer(doc); restored.Server.RestoreCheckpoint(decoded!);
        for (ulong tick = 1; tick <= 100; tick++) {
            var context = new FixedStepContext(Tick: tick, ElapsedTicks: (tick + 1) * Fixtures.StepTicks, StepTicks: Fixtures.StepTicks);
            original.Server.Step(context); restored.Server.Step(context);
            Assert.Equal(State(original), State(restored));
            Assert.Equal(WorldRuntimeStateHash.HashAuthoritative(original.Server, tick), WorldRuntimeStateHash.HashAuthoritative(restored.Server, tick));
        }
    }

    [Fact]
    public void SightOnlyNeighborPolicyBuildsTheFieldAndAcceptsAnUnobstructedCandidate() {
        var doc = Document(Policy(new(20, 4, 3, RequiresLineOfSight: true)));
        Assert.True(WorldTargetSelection.RequiresLineOfSight(doc));
        using var fixture = Fixtures.FreshServer(doc);
        _ = Join(fixture, 0, 0); _ = Join(fixture, 1, 2);
        fixture.Step(); Assert.Equal(1, State(fixture).Candidate);
        Assert.True(fixture.Server.DecisionWork.SightTests > 0);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CoincidentCrowdHasBoundedInspectionAndNoAddedSteadyAllocation(bool rejectAll) {
        var policy = Policy(new(20, 32, 16)) with { Mode = WorldDecisionMode.Weighted };
        policy = policy with { Options = [policy.Options[0] with {
            Score = Constant(1), Effects = [], Gate = rejectAll ? new ActionPredicate.CompareState("appeal", ActionStateComparison.Less, 0, Key: "$right") : null,
        }] };
        var doc = Document(policy);
        doc = doc with { PopulationRaw = doc.Population with { CapacityRaw = 128, NetworkPlayers = 124 },
            StateRaw = new(World: [Row("observers", Enumerable.Repeat(1L, 128).ToArray()), Row("appeal", new long[128])]) };
        Assert.True(WorldDefinitionValidator.TryValidateLocally(doc, out var reason), reason);
        using var subject = Fixtures.FreshServer(doc);
        using var control = Fixtures.FreshServer(doc with { Rules = [] });
        static void Populate(WorldFixture fixture) {
            for (var i = 0; i < 4; i++) { _ = Join(fixture, i, 0); }
            Assert.Equal(124, fixture.Server.Population.SetSimulatedCount(124));
            for (var i = 4; i < 128; i++) {
                fixture.Server.Body(i)!.Pose(Position(0), FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.Zero);
                fixture.Server.Body(i)!.SetIntentSource(IntentSource.Idle);
            }
        }
        Populate(subject); Populate(control);
        // Wide enough that every hot callee the measured window below allocates against has already promoted to
        // Tier1 before that window opens — a background tier-up recompilation landing inside the measured window
        // reads as extra allocation on whichever of subject/control's own Run it lands in, even though steady-state
        // allocation is identical (the same JIT-tiering flake WorldFlockScaleLawTests' own dense-social-flock case
        // widened past).
        static (long Bytes, TimeSpan Time) Run(WorldFixture f) {
            for (var i = 0; i < 2048; i++) { f.Step(504); _ = WorldRuntimeStateHash.HashAuthoritative(f.Server, 0); }
            var bytes = GC.GetAllocatedBytesForCurrentThread(); var start = Stopwatch.GetTimestamp();
            for (var i = 0; i < 1000; i++) { f.Step(504); _ = WorldRuntimeStateHash.HashAuthoritative(f.Server, 0); }
            return (GC.GetAllocatedBytesForCurrentThread() - bytes, Stopwatch.GetElapsedTime(start));
        }
        var measured = Run(subject); var baseline = Run(control);
        TestContext.Current.TestOutputHelper!.WriteLine($"128 coincident bodies, budget32/max16, rejectAll={rejectAll}: x1000 steps+hash {measured.Time.TotalMilliseconds:F3}ms/{measured.Bytes}B; no-decisions {baseline.Time.TotalMilliseconds:F3}ms/{baseline.Bytes}B");
        Assert.Equal(baseline.Bytes, measured.Bytes);
        Assert.Equal(128 * 32, subject.Server.DecisionWork.Inspected);
        Assert.Equal(rejectAll ? 0 : 128 * 16, subject.Server.DecisionWork.Scored);
        Assert.Equal(128, subject.Server.DecisionWork.LimitedQueries);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void MalformedCandidateCheckpointRefusesWithoutChangingAuthority(int variant) {
        using var fixture = Fixtures.FreshServer(Document());
        _ = Join(fixture, 0, 0); _ = Join(fixture, 1, 2); fixture.Step();
        var captured = Capture(fixture); var original = Assert.Single(captured.Server.Decisions!);
        var invalid = variant switch {
            0 => original with { Candidate = -1 }, 1 => original with { Candidate = 128 },
            2 => original with { CandidateGeneration = -1 }, _ => original with { Candidate = original.Key },
        };
        var before = WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 0);
        Assert.Throws<InvalidOperationException>(() => fixture.Server.RestoreCheckpoint(captured with { Server = captured.Server with { Decisions = [invalid] } }));
        Assert.Equal(before, WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 0));
    }

    [Fact]
    public void CommittedWorldEffectsRoundTripButRemainForbiddenAtBothLiveCodecDoors() {
        var effect = new WorldMutation.UpsertStateCell(WorldPrincipal.World, "entries", "$value", 1, WorldDocumentWriteKind.Set);
        Assert.True(WorldSubmissionCodec.TryEncodeCommittedMutation(effect, out var bytes, out var failure), failure.ToString());
        Assert.True(WorldSubmissionCodec.TryDecodeCommittedMutation(bytes, out var restored, out failure), failure.ToString());
        Assert.Equal(effect, restored);
        Assert.False(WorldSubmissionCodec.TryEncodeMutation(effect, out _, out _));
        Assert.False(WorldSubmissionCodec.TryDecodeMutation(bytes, out _, out _));
        var external = effect with { Principal = WorldPrincipal.Console };
        Assert.True(WorldSubmissionCodec.TryEncodeMutation(external, out bytes, out failure), failure.ToString());
        Assert.True(WorldSubmissionCodec.TryDecodeMutation(bytes, out restored, out failure), failure.ToString());
        Assert.Equal(external, restored);
        Assert.False(WorldSubmissionCodec.TryEncodeCommittedMutation(effect with { Principal = WorldPrincipal.World with { Index = 1 } }, out _, out _));
    }
}
