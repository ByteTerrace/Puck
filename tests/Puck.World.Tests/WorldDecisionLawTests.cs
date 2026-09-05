using System.Diagnostics;
using Puck.Hosting;
using Puck.Physics.Motion;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

// Run allocation/CPU comparisons after parallel collections; concurrent test activity contaminates the full-step probe.
[Collection(ConsoleRedirectionCollection.Name)]
public sealed class WorldDecisionLawTests {
    private static CellName Name(string value) => CellName.Parse(value);
    private static WorldStateRow Slot(string name, long value = 0) => new(Name(name), CellKind.Int,
        Cells: [new(WorldStateRow.SlotKey, value)]);
    private static ValueExpression Score(decimal value) => new([new ValueToken.Constant(value)]);
    private static WorldDecisionOption Option(string name, decimal score, ActionPredicate? gate = null, params ActionEffect[] effects) =>
        new(Name(name), Score(score), effects, gate);
    private static ActionPredicate Gate(string row, decimal value = 1) => new ActionPredicate.CompareState(row, ActionStateComparison.Equal, value);
    private static WorldRule Rule(WorldDecision policy, string name = "choose", string? forEach = null, ActionPredicate? gate = null) =>
        new(Name(name), [], gate, ForEach: forEach, Decision: policy);
    private static WorldDecision Policy(params WorldDecisionOption[] options) => new(options, 0.01m, ScoreKind: CellKind.Int);
    private static WorldDefinition Document(WorldRule rule, params WorldStateRow[] rows) => Fixtures.BuildDocument() with {
        StateRaw = new(World: rows), Rules = [rule],
    };
    private static WorldAuthorityHostRowCheckpoint Host() => new(0, 0, false, [], 1, [], [], [], null, 0, false, [], []);
    private static WorldAuthorityCheckpoint Capture(WorldFixture fixture) {
        Assert.True(fixture.Server.TryCaptureCheckpoint(Host(), out var captured, out var reason), reason);
        return captured!;
    }
    private static WorldDecisionCheckpoint State(WorldFixture fixture, string rule = "choose", int key = -1) =>
        Assert.Single(Capture(fixture).Server.Decisions!, s => s.Rule == rule && s.Key == key);
    private static long Value(WorldFixture fixture, string row) => WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, row)!.Cells![0].Value;
    private static void Set(WorldFixture fixture, string row, long value) => fixture.Server.EnqueueMutation(
        new WorldMutation.UpsertStateCell(WorldPrincipal.Console, row, WorldStateRow.SlotKey.Value, value, WorldDocumentWriteKind.Set));

    [Fact]
    public void HighestScoreFiltersBeforeScoringAndUsesDocumentOrderForNegativeTies() {
        using var fixture = Fixtures.FreshServer(Document(Rule(Policy(
            Option("forbidden", long.MaxValue, Gate("allowed")), Option("first", -7), Option("second", -7))), Slot("allowed")));
        fixture.Step();
        Assert.Equal(1, State(fixture).Selected);
        Assert.Equal(-7, State(fixture).LastScore);
        Assert.Equal(0UL, State(fixture).DrawCount);
        Assert.Contains("-1:first", fixture.Server.DescribeDecisions());
    }

    [Fact]
    public void InvalidScoreCannotWinOrCrashTheDecision() {
        var broken = new WorldDecisionOption(Name("broken"), new([new ValueToken.Constant(1),
            new ValueToken.Constant(0), new ValueToken.Divide()]), []);
        using var fixture = Fixtures.FreshServer(Document(Rule(Policy(broken, Option("valid", -1)))));
        fixture.Step();
        Assert.Equal(1, State(fixture).Selected);
    }

    [Fact]
    public void StayingDoesNotRepeatEffectsOrRenewCommitment() {
        var policy = Policy(Option("stay", 1, effects: [new ActionEffect.AddState("entered", Value: 1)])) with {
            PeriodSeconds = 0.01m, CommitmentSeconds = 0.03m,
        };
        using var fixture = Fixtures.FreshServer(Document(Rule(policy), Slot("entered")));
        fixture.Step(504);
        Assert.Equal(1512UL, State(fixture).CommitmentRemaining);
        for (var index = 0; index < 10; index++) { fixture.Step(504); }
        Assert.Equal(1, Value(fixture, "entered"));
        Assert.Equal(0UL, State(fixture).CommitmentRemaining);
        Assert.True(State(fixture).Reconsiderations > 1);
    }

    [Fact]
    public void CommitmentBlocksOrdinaryChangesButNotLostEligibility() {
        var policy = Policy(Option("safe", 1, Gate("safe")),
            new(Name("tempting"), new([new ValueToken.State("score")]), [])) with { CommitmentSeconds = 10 };
        using var fixture = Fixtures.FreshServer(Document(Rule(policy), Slot("safe", 1), Slot("score")));
        fixture.Step(504);
        Set(fixture, "score", 10); fixture.Step(504);
        Assert.Equal(0, State(fixture).Selected);
        Set(fixture, "safe", 0); fixture.Step(1);
        Assert.Equal(1, State(fixture).Selected);
        Assert.Equal(2UL, State(fixture).Reconsiderations);
    }

    [Fact]
    public void InterruptIsAnEdgeAndBypassesBothTimers() {
        var policy = Policy(Option("a", 1), Option("b", 1)) with {
            Mode = WorldDecisionMode.Weighted, PeriodSeconds = 10, CommitmentSeconds = 10, Interrupt = Gate("alarm"),
        };
        using var fixture = Fixtures.FreshServer(Document(Rule(policy), Slot("alarm")));
        fixture.Step();
        Set(fixture, "alarm", 1); fixture.Step();
        Assert.Equal(2UL, State(fixture).Reconsiderations);
        for (var index = 0; index < 10; index++) { fixture.Step(); }
        Assert.Equal(2UL, State(fixture).Reconsiderations);
        Assert.Equal(4UL, State(fixture).DrawCount);
        Set(fixture, "alarm", 0); fixture.Step();
        Set(fixture, "alarm", 1); fixture.Step();
        Assert.Equal(3UL, State(fixture).Reconsiderations);
    }

    [Fact]
    public void NoChoiceAndGateClosureAreTransitionsNotLevelEffects() {
        var policy = Policy(Option("choice", 1, Gate("eligible"))) with {
            OnNoChoice = [new ActionEffect.AddState("empty", Value: 1)],
        };
        using var fixture = Fixtures.FreshServer(Document(Rule(policy, gate: Gate("enabled")),
            Slot("eligible"), Slot("enabled", 1), Slot("empty")));
        for (var index = 0; index < 5; index++) { fixture.Step(504); }
        Assert.Equal(1, Value(fixture, "empty"));
        Set(fixture, "eligible", 1); fixture.Step(504);
        Assert.Equal(0, State(fixture).Selected);
        Set(fixture, "enabled", 0); fixture.Step(504);
        Assert.Equal(2, Value(fixture, "empty"));
        Assert.Equal(-1, State(fixture).Selected);
        fixture.Step(504);
        Assert.Equal(2, Value(fixture, "empty"));
    }

    [Fact]
    public void WeightedZeroNegativeAndSingleEligibleOptionsConsumeNoRandomness() {
        using var fixture = Fixtures.FreshServer(Document(Rule(Policy(
            Option("zero", 0), Option("negative", -1)) with { Mode = WorldDecisionMode.Weighted })));
        fixture.Step();
        Assert.Equal(-1, State(fixture).Selected);
        Assert.Equal(0UL, State(fixture).DrawCount);
        using var single = Fixtures.FreshServer(Document(Rule(Policy(
            Option("zero", 0), Option("positive", 2)) with { Mode = WorldDecisionMode.Weighted })));
        single.Step();
        Assert.Equal(1, State(single).Selected);
        Assert.Equal(0UL, State(single).DrawCount);
    }

    [Fact]
    public void WeightedWideTotalsStayInBoundsAndTrackRelativeWeights() {
        // The sum exceeds UInt64: this discriminates against a wrapping cumulative-weight implementation.
        var policy = Policy(Option("small", long.MaxValue / 3), Option("large", long.MaxValue),
            Option("large-two", long.MaxValue), Option("never", 0)) with { Mode = WorldDecisionMode.Weighted };
        using var fixture = Fixtures.FreshServer(Document(Rule(policy)));
        var counts = new int[4];
        for (var index = 0; index < 1400; index++) {
            fixture.Step(504);
            counts[State(fixture).Selected]++;
        }
        Assert.InRange(counts[0], 120, 280);
        Assert.InRange(counts[1], 470, 740);
        Assert.InRange(counts[2], 470, 740);
        Assert.Equal(0, counts[3]);
        Assert.Equal(2800UL, State(fixture).DrawCount);
    }

    [Fact]
    public void UnrelatedDecisionsAndRuleOrderDoNotPerturbTheLocalStream() {
        var policy = Policy(Option("a", 1), Option("b", 3)) with { Mode = WorldDecisionMode.Weighted };
        var chosen = Rule(policy);
        using var alone = Fixtures.FreshServer(Document(chosen));
        using var withOther = Fixtures.FreshServer(Document(chosen) with { Rules = [Rule(policy, "other"), chosen] });
        for (var index = 0; index < 100; index++) {
            alone.Step(504); withOther.Step(504);
            Assert.Equal(State(alone), State(withOther));
        }
        Assert.NotEqual(State(withOther).RandomState, State(withOther, "other").RandomState);
    }

    [Fact]
    public void GateReopeningDoesNotRestartTheRandomStream() {
        var policy = Policy(Option("a", 1), Option("b", 1)) with { Mode = WorldDecisionMode.Weighted };
        using var fixture = Fixtures.FreshServer(Document(Rule(policy, gate: Gate("enabled")), Slot("enabled", 1)));
        fixture.Step();
        var before = State(fixture);
        Set(fixture, "enabled", 0); fixture.Step();
        Assert.Equal(before.RandomState, State(fixture).RandomState);
        Set(fixture, "enabled", 1); fixture.Step();
        Assert.Equal(4UL, State(fixture).DrawCount);
        Assert.NotEqual(before.RandomState, State(fixture).RandomState);
    }

    [Fact]
    public void IncumbentBonusPreventsSmallOscillationsAndSaturatesSafely() {
        var policy = Policy(Option("first", long.MaxValue - 1),
            Option("later", long.MaxValue, Gate("allowed"))) with { IncumbentBonus = long.MaxValue };
        using var fixture = Fixtures.FreshServer(Document(Rule(policy), Slot("allowed")));
        fixture.Step(504);
        Set(fixture, "allowed", 1); fixture.Step(504);
        Assert.Equal(0, State(fixture).Selected);
        Assert.Equal(long.MaxValue, State(fixture).LastScore);
    }

    [Fact]
    public void BindingKeysAreNotAssumedToBePopulationSlotsAndMissingKeysArePruned() {
        var carriers = new WorldStateRow(Name("carriers"), CellKind.Int, Capacity: 3,
            Cells: [new(Name("1000000"), 1), new(Name("0"), 1)]);
        using var fixture = Fixtures.FreshServer(Document(Rule(Policy(Option("a", 1)), forEach: "carriers"), carriers));
        fixture.Step();
        Assert.Equal(new[] { 0, 1000000 }, Capture(fixture).Server.Decisions!.Select(s => s.Key));
        fixture.Server.EnqueueMutation(new WorldMutation.RemoveStateCell(WorldPrincipal.Console, "carriers", "1000000"));
        fixture.Step();
        Assert.Single(Capture(fixture).Server.Decisions!);
    }

    [Fact]
    public void CheckpointWireRoundTripPreservesEveryFutureChoiceAndHash() {
        var policy = Policy(Option("a", 1), Option("b", 2)) with { Mode = WorldDecisionMode.Weighted, CommitmentSeconds = 0.03m };
        using var original = Fixtures.FreshServer(Document(Rule(policy)));
        for (var index = 0; index < 5; index++) { original.Step(); }
        var captured = Capture(original);
        Assert.True(WorldAuthorityCheckpointCodec.TryDecode(WorldAuthorityCheckpointCodec.Encode(captured), out var decoded, out var reason), reason);
        using var restored = Fixtures.FreshServer(Document(Rule(policy)));
        restored.Server.RestoreCheckpoint(decoded!);
        Assert.Equal(WorldRuntimeStateHash.HashAuthoritative(original.Server, 0), WorldRuntimeStateHash.HashAuthoritative(restored.Server, 0));
        for (ulong index = 5; index < 200; index++) {
            var context = new FixedStepContext(Tick: index, ElapsedTicks: (index + 1) * Fixtures.StepTicks, StepTicks: Fixtures.StepTicks);
            original.Server.Step(in context); restored.Server.Step(in context);
            Assert.Equal(WorldRuntimeStateHash.HashAuthoritative(original.Server, index), WorldRuntimeStateHash.HashAuthoritative(restored.Server, index));
        }
    }

    [Fact]
    public void RecompilingUnchangedPolicyPreservesStateButEditingPolicyRestartsIt() {
        var rule = Rule(Policy(Option("a", 1), Option("b", 1)) with { Mode = WorldDecisionMode.Weighted, PeriodSeconds = 10 });
        using var fixture = Fixtures.FreshServer(Document(rule));
        fixture.Step();
        var before = State(fixture);
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateRow(WorldPrincipal.Console, Slot("unrelated")));
        fixture.Step();
        Assert.Equal(before.RandomState, State(fixture).RandomState);
        Assert.Equal(before.DrawCount, State(fixture).DrawCount);
        Assert.Equal(before.Reconsiderations, State(fixture).Reconsiderations);
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertWorldRule(WorldPrincipal.Console, rule with {
            Decision = rule.Decision! with { Seed = 99 },
        }));
        fixture.Step();
        Assert.Equal(1UL, State(fixture).Reconsiderations);
        Assert.NotEqual(before.RandomState, State(fixture).RandomState);
    }

    [Fact]
    public void ANewBodyGenerationCannotInheritAPreviousOccupantsChoiceStream() {
        var carriers = new WorldStateRow(Name("carriers"), CellKind.Int, Capacity: 1, Cells: [new(Name("0"), 1)]);
        using var fixture = Fixtures.FreshServer(Document(Rule(Policy(Option("a", 1), Option("b", 1)) with {
            Mode = WorldDecisionMode.Weighted, PeriodSeconds = 10,
        }, forEach: "carriers"), carriers));
        fixture.Step();
        var before = State(fixture, key: 0);
        Assert.True(fixture.Server.ApplySession(new SessionRequest.Join(WorldPrincipal.Seat(0), 0, null, WorldProtocol.WireProtocolKey)).Accepted);
        fixture.Step();
        var after = State(fixture, key: 0);
        Assert.NotEqual(before.Generation, after.Generation);
        Assert.NotEqual(before.RandomState, after.RandomState);
        Assert.Equal(1UL, after.Reconsiderations);
    }

    [Fact]
    public void NumericAliasesOfABindingAreEvaluatedOnce() {
        var carriers = new WorldStateRow(Name("carriers"), CellKind.Int, Capacity: 2,
            Cells: [new(Name("0"), 1), new(Name("00"), 1)]);
        using var fixture = Fixtures.FreshServer(Document(Rule(Policy(Option("a", 1)), forEach: "carriers"), carriers));
        fixture.Step();
        var state = Assert.Single(Capture(fixture).Server.Decisions);
        Assert.Equal(0, state.Key);
        Assert.Equal(1UL, state.Reconsiderations);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void InvalidCheckpointRefusesBeforeChangingAuthority(int variant) {
        using var fixture = Fixtures.FreshServer(Document(Rule(Policy(Option("a", 1)))));
        fixture.Step();
        var captured = Capture(fixture);
        var state = Assert.Single(captured.Server.Decisions!);
        var invalid = variant switch {
            0 => state with { Selected = 1 }, 1 => state with { PeriodRemaining = ulong.MaxValue },
            2 => state with { DrawCount = 2 }, 3 => state with { Key = 0 }, _ => state with { Evaluated = false },
        };
        var before = WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 0);
        Assert.Throws<InvalidOperationException>(() => fixture.Server.RestoreCheckpoint(captured with {
            Server = captured.Server with { Decisions = [invalid] },
        }));
        Assert.Equal(before, WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 0));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void MalformedPolicyIsRefusedAtCompilation(int variant) {
        var policy = Policy(Option("a", 1));
        policy = variant switch {
            0 => policy with { PeriodSeconds = 0 }, 1 => policy with { CommitmentSeconds = -1 },
            2 => policy with { PeriodSeconds = 0.000001m }, 3 => policy with { IncumbentBonus = -1 },
            4 => policy with { Options = [Option("a", 1), Option("a", 2)] }, _ => policy with { ScoreKind = CellKind.Bool },
        };
        Assert.Throws<WorldRuleException>(() => WorldRuleCompiler.CompileAll(Document(Rule(policy))));
    }

    [Fact]
    public void DecisionCannotCombineAnEdgeLatchOrMismatchedScoreKinds() {
        var rule = Rule(Policy(Option("a", 1)));
        Assert.Throws<WorldRuleException>(() => WorldRuleCompiler.CompileAll(Document(rule with { Mode = ActionTriggerMode.Edge })));
        var typed = Policy(new WorldDecisionOption(Name("a"), new([new ValueToken.State("score")]), [])) with { ScoreKind = CellKind.Fixed };
        Assert.Throws<WorldRuleException>(() => WorldRuleCompiler.CompileAll(Document(Rule(typed), Slot("score", 1))));
    }

    [Fact]
    public void StrictDocumentRoundTripIncludesDecisionsAndTheirFullWorkCost() {
        var rule = Rule(Policy(Option("a", 1), Option("b", 2)));
        var document = Document(rule);
        var roundTrip = WorldDefinitionSerialization.Deserialize(WorldDefinitionSerialization.Serialize(document));
        Assert.Equal(WorldRuleCompiler.CompileAll(document)[0].Decision!.PolicyIdentity,
            WorldRuleCompiler.CompileAll(roundTrip)[0].Decision!.PolicyIdentity);
        Assert.True(WorldDefinitionValidator.TryValidateLocally(roundTrip, out var reason), reason);
        var one = WorldRuleWorkBudget.Measure(Document(Rule(Policy(Option("a", 1))))).WorkUnitsPerTick;
        Assert.True(WorldRuleWorkBudget.Measure(document).WorkUnitsPerTick > one);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    public void DenseDecisionsAndHashesAddNoSteadyStateAllocation(int policyCount) {
        var carriers = new WorldStateRow(Name("carriers"), CellKind.Int, Capacity: 128,
            Cells: Enumerable.Range(0, 128).Select(i => new WorldStateCell(Name(i.ToString()), 1)).ToArray());
        var policy = Policy(Enumerable.Range(0, 32).Select(i => Option($"option-{i}", i + 1)).ToArray()) with { Mode = WorldDecisionMode.Weighted };
        var document = Document(Rule(policy, forEach: "carriers"), carriers) with {
            Rules = Enumerable.Range(0, policyCount).Select(i => Rule(policy, $"policy-{i}", "carriers")).ToArray(),
        };
        Assert.True(WorldDefinitionValidator.TryValidateLocally(document, out var reason), reason);
        using var fixture = Fixtures.FreshServer(document);
        for (var index = 0; index < 100; index++) {
            fixture.Step(504);
            _ = WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 0);
        }
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        for (var index = 0; index < 1000; index++) {
            fixture.Step(504);
            _ = WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 0);
        }
        var elapsed = Stopwatch.GetElapsedTime(start);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        TestContext.Current.TestOutputHelper!.WriteLine($"{policyCount * 128} bindings x 32 options, 1000 full decision steps + authoritative hashes: {elapsed.TotalMilliseconds:F3} ms, {allocated} allocated bytes");
        using var baseline = Fixtures.FreshServer(fixture.Server.Definition with { Rules = [] });
        for (var index = 0; index < 100; index++) {
            baseline.Step(504);
            _ = WorldRuntimeStateHash.HashAuthoritative(baseline.Server, 0);
        }
        var baselineBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++) {
            baseline.Step(504);
            _ = WorldRuntimeStateHash.HashAuthoritative(baseline.Server, 0);
        }
        var baselineAllocated = GC.GetAllocatedBytesForCurrentThread() - baselineBefore;
        TestContext.Current.TestOutputHelper!.WriteLine($"Same world without decisions: {baselineAllocated} allocated bytes");
        Assert.Equal(baselineAllocated, allocated);
        Assert.Equal(policyCount * 128, Capture(fixture).Server.Decisions!.Count);
    }
}
