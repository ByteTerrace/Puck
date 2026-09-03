using System.Diagnostics;
using System.Text.Json;
using Puck.Hosting;
using Puck.Maths;
using Puck.Physics.Motion;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

[Collection(ConsoleRedirectionCollection.Name)]
public sealed class WorldSocialFlockLawTests {
    private static WorldCellName Name(string text) => WorldCellName.Parse(text);
    private static WorldValueExpression Constant(decimal value) => new([new WorldValueToken.Constant(value)]);
    private static WorldSocialRelationship Relationship(string observer = "left", string subject = "right", string dimension = "affection") =>
        new(new(Body: observer), new(Body: subject), dimension);
    private static WorldValueExpression Social(string dimension = "affection", WorldSocialFacet facet = WorldSocialFacet.Value) =>
        new([new WorldValueToken.Social(new(Relationship(dimension: dimension), facet))]);
    private static WorldFlockProfile Profile(float cadence = 0) => new(20, 1, 4, 3, cadence, WorldFlockSpace.Tangent,
        Separation: 0, Alignment: 0, Cohesion: 1, Goal: 0, Inertia: 0, ArrivalDistance: 0, HalfAngleDegrees: 180, RequiresLineOfSight: false);
    private static WorldDefinition Document(WorldFlockProfile profile, params WorldRule[] rules) {
        var doc = Fixtures.BuildDocument();
        return doc with {
            BodyMotionProgramsRaw = [.. doc.BodyMotionPrograms, new("flock", BodyMotionProgram.CurrentVersion,
                BodyProgramKind.Producer, [BodyMotionOp.ProduceFlockIntent])],
            KitRowsRaw = [doc.Kits[0] with { ProducersRaw = new Dictionary<string, BodyProgramParameters> {
                ["flock"] = new(new Dictionary<string, float>(), new Dictionary<string, string>(), Flock: profile),
            } }],
            StateRaw = new(Social: new([new(Name("affection"), PriorWeight: 1, MaximumChange: 2),
                new(Name("competence"), PriorWeight: 1, MaximumChange: 2)], ImpressionCapacity: 1024, ImpressionsPerObserver: 32,
                ReceiptCapacity: 1024, EvidenceAttemptsPerTick: 128, ExpiredReceiptsPerTick: 128)),
            Rules = rules,
        };
    }
    private static WorldRule Observe(int observer, int subject, string dimension = "affection", decimal value = 1) => new(
        Name($"observe-{observer}-{subject}-{dimension}"), [new ActionEffect.ObserveSocial(new(
            Relationship($"body:{observer}", $"body:{subject}", dimension), new(Body: $"body:{subject}"), "help.outcome",
            Constant(1), Constant(0), Constant(value)))]);
    private static FixedVector3 Position(int x, int z = 0) => new(FixedQ4816.FromInteger(x), FixedQ4816.Zero, FixedQ4816.FromInteger(z));
    private static WorldBody Join(WorldFixture fixture, int slot, int x, bool flock = true) {
        Assert.True(fixture.Server.ApplySession(new SessionRequest.Join(WorldPrincipal.Seat(slot), slot, null, WorldProtocol.WireProtocolKey)).Accepted);
        var body = fixture.Server.Body(slot)!;
        body.Pose(Position(x), FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.Zero);
        body.SetIntentSource(flock ? IntentSource.Producer("flock") : IntentSource.Idle);
        return body;
    }
    private static WorldAuthorityCheckpoint Capture(WorldFixture fixture) {
        Assert.True(fixture.Server.TryCaptureCheckpoint(new(0, 0, false, [], 1, [], [], [], null, 0, false, [], []), out var checkpoint, out var reason), reason);
        return checkpoint!;
    }
    private static FixedVector3 Desired(WorldFixture fixture, int index = 0) => fixture.Server.Population.Capture().Entries[index].Flock.Desired;

    [Fact]
    public void DirectedBeliefsSelectCompanionsAfterObservationWithoutInventingReciprocity() {
        using var fixture = Fixtures.FreshServer(Document(Profile() with { CohesionAffinity = Social() }, Observe(0, 2)));
        var observer = Join(fixture, 0, 0);
        _ = Join(fixture, 1, -2); _ = Join(fixture, 2, 2);
        fixture.Step();
        Assert.Equal(FixedVector3.Zero, Desired(fixture)); // Observation occurs after population movement.
        fixture.Step();
        Assert.Equal(Position(1), Desired(fixture));
        Assert.True(observer.FixedPosition.X > FixedQ4816.Zero);
        Assert.Equal(FixedVector3.Zero, Desired(fixture, 1));
        Assert.Equal(FixedVector3.Zero, Desired(fixture, 2));
        Assert.Equal(6, fixture.Server.Population.FlockStatistics.AffinityEvaluations);
        Assert.Contains("cohesionAffinity=", fixture.Server.Population.DescribeFlocks());
    }

    [Fact]
    public void CompetenceCanAlignWithOneNeighborWhileAffectionCohesivelyFollowsAnother() {
        using var fixture = Fixtures.FreshServer(Document(Profile() with {
            Cohesion = 0.5f, Alignment = 0.5f, CohesionAffinity = Social(), AlignmentAffinity = Social("competence"),
        }, Observe(0, 2), Observe(0, 1, "competence")));
        _ = Join(fixture, 0, 0);
        var expert = Join(fixture, 1, -2, false); var friend = Join(fixture, 2, 2, false);
        fixture.Step();
        expert.ApplyIntegrationResidue(expert.CaptureIntegrationResidue() with { PreviousPosition = expert.FixedPosition - Position(0, 1) });
        friend.ApplyIntegrationResidue(friend.CaptureIntegrationResidue() with { PreviousPosition = friend.FixedPosition - Position(0, -1) });
        fixture.Step();
        var desired = Desired(fixture);
        Assert.Equal(FixedQ4816.One.Value / 2, desired.X.Value);
        Assert.Equal(FixedQ4816.One.Value / 2, desired.Z.Value);
        Assert.Equal(4, fixture.Server.Population.FlockStatistics.AffinityEvaluations);
    }

    [Theory]
    [InlineData(-1, false)] [InlineData(0, false)] [InlineData(1, true)] [InlineData(2, true)]
    public void FinalAffinityIsClampedAndDoesNotScaleTheNormalizedTerm(int affinity, bool follows) {
        using var fixture = Fixtures.FreshServer(Document(Profile() with { CohesionAffinity = Constant(affinity) }));
        var body = Join(fixture, 0, 0); _ = Join(fixture, 1, 2, false);
        fixture.Step();
        Assert.Equal(follows, body.FixedPosition.X > FixedQ4816.Zero);
        Assert.Equal(follows ? Position(1) : FixedVector3.Zero, Desired(fixture));
    }

    [Fact]
    public void FailedAffinityIsZeroAndCannotDisableSeparation() {
        var invalid = new WorldValueExpression([new WorldValueToken.Constant(1), new WorldValueToken.Constant(0), new WorldValueToken.Divide()]);
        using var fixture = Fixtures.FreshServer(Document(Profile() with {
            Separation = 1, SeparationRadius = 4, CohesionAffinity = invalid, AlignmentAffinity = Constant(0),
        }));
        var body = Join(fixture, 0, 0); _ = Join(fixture, 1, 2, false);
        fixture.Step();
        Assert.True(body.FixedPosition.X < FixedQ4816.Zero);
        Assert.Equal(1, fixture.Server.Population.FlockStatistics.AffinityFailures);
        Assert.Equal(2, fixture.Server.Population.FlockStatistics.AffinityEvaluations);
    }

    [Fact]
    public void UnknownConfidenceAndValueAreDifferentInputs() {
        foreach (var facet in new[] { WorldSocialFacet.Value, WorldSocialFacet.Confidence }) {
            var doc = Document(Profile() with { CohesionAffinity = Social(facet: facet) });
            doc = doc with { StateRaw = doc.StateRaw! with { Social = doc.StateRaw.Social! with {
                Dimensions = [new(Name("affection"), Baseline: 0.5m)],
            } } };
            using var fixture = Fixtures.FreshServer(doc);
            var body = Join(fixture, 0, 0); _ = Join(fixture, 1, 2, false);
            fixture.Step();
            Assert.Equal(facet == WorldSocialFacet.Value, body.FixedPosition.X > FixedQ4816.Zero);
            Assert.Empty(Capture(fixture).Server.Social!.Impressions);
        }
    }

    [Fact]
    public void SocialChangesRespectCadenceAndReselectionRefreshesImmediately() {
        using var fixture = Fixtures.FreshServer(Document(Profile(10) with { CohesionAffinity = Social() }, Observe(0, 1)));
        var body = Join(fixture, 0, 0); _ = Join(fixture, 1, 2, false);
        fixture.Step(); fixture.Step();
        Assert.Equal(FixedVector3.Zero, Desired(fixture));
        Assert.Equal(0, fixture.Server.Population.FlockStatistics.AffinityEvaluations);
        body.SetIntentSource(IntentSource.Idle); fixture.Step();
        body.SetIntentSource(IntentSource.Producer("flock")); fixture.Step();
        Assert.True(body.FixedPosition.X > FixedQ4816.Zero);
        Assert.Equal(1, fixture.Server.Population.FlockStatistics.AffinityEvaluations);
    }

    [Fact]
    public void StatePairBindingsRecompileAfterRowRemovalWithoutPopulationRebuild() {
        var expression = new WorldValueExpression([new WorldValueToken.State("preference", "$right")]);
        var doc = Document(Profile() with { CohesionAffinity = expression });
        doc = doc with { StateRaw = doc.StateRaw! with { World = [
            new(Name("padding"), CellKind.Fixed, Cells: [new(WorldStateRow.SlotKey, 0)]),
            new(Name("preference"), CellKind.Fixed, Capacity: 4, Cells: [new(Name("1"), 0), new(Name("2"), FixedQ4816.One.Value)]),
        ] } };
        using var fixture = Fixtures.FreshServer(doc);
        _ = Join(fixture, 0, 0); _ = Join(fixture, 1, -2, false); _ = Join(fixture, 2, 2, false);
        fixture.Step(); Assert.Equal(Position(1), Desired(fixture));
        fixture.Server.EnqueueMutation(new WorldMutation.RemoveStateRow(WorldPrincipal.Console, "padding"));
        fixture.Step();
        Assert.Single(fixture.Server.Definition.State);
        Assert.Equal(Position(1), Desired(fixture));
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateRow(WorldPrincipal.Console,
            new(Name("preference"), CellKind.Fixed, Capacity: 4, Cells: [new(Name("1"), FixedQ4816.One.Value), new(Name("2"), 0)])));
        fixture.Step(); Assert.Equal(Position(-1), Desired(fixture));
    }

    [Fact]
    public void ReusedBodySlotDoesNotInheritSocialAffinity() {
        var doc = Document(Profile() with { CohesionAffinity = Social() }, Observe(0, 4));
        doc = doc with { PopulationRaw = doc.Population with { CapacityRaw = 5, NetworkPlayers = 1 } };
        using var fixture = Fixtures.FreshServer(doc);
        _ = Join(fixture, 0, 0);
        Assert.Equal(1, fixture.Server.Population.SetSimulatedCount(1));
        fixture.Server.Body(4)!.Pose(Position(2), FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.Zero);
        fixture.Server.Body(4)!.SetIntentSource(IntentSource.Idle);
        fixture.Step(); fixture.Step(); Assert.Equal(Position(1), Desired(fixture));
        fixture.Server.Population.SetSimulatedCount(0); fixture.Server.Population.SetSimulatedCount(1);
        fixture.Server.Body(4)!.Pose(Position(2), FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.Zero);
        fixture.Server.Body(4)!.SetIntentSource(IntentSource.Idle);
        fixture.Step(); // New occupant has no memory yet; the rule may observe it only after this sample.
        Assert.Equal(FixedVector3.Zero, Desired(fixture));
    }

    [Fact]
    public void WireCheckpointPreservesCachedAffinitiesAndFutureLearning() {
        var doc = Document(Profile(0.02f) with { CohesionAffinity = Social() }, Observe(0, 1));
        using var original = Fixtures.FreshServer(doc);
        _ = Join(original, 0, 0); _ = Join(original, 1, 2, false);
        for (var index = 0; index < 8; index++) { original.Step(); }
        var saved = Capture(original);
        Assert.True(WorldAuthorityCheckpointCodec.TryDecode(WorldAuthorityCheckpointCodec.Encode(saved), out var decoded, out var reason), reason);
        using var restored = Fixtures.FreshServer(doc);
        restored.Server.RestoreCheckpoint(decoded!);
        for (ulong index = 8; index < 120; index++) {
            var context = new FixedStepContext(Tick: index, ElapsedTicks: (index + 1) * Fixtures.StepTicks, StepTicks: Fixtures.StepTicks);
            original.Server.Step(in context); restored.Server.Step(in context);
            Assert.Equal(WorldRuntimeStateHash.HashAuthoritative(original.Server, index), WorldRuntimeStateHash.HashAuthoritative(restored.Server, index));
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void InvalidKindsBindingsAndMovementPassFactsAreRefused(int variant) {
        var expression = variant switch {
            0 => Social(facet: WorldSocialFacet.Known),
            1 => new WorldValueExpression([new WorldValueToken.Social(new(Relationship(observer: "each")))]),
            2 => new WorldValueExpression([new WorldValueToken.State("$distance:left:right")]),
            3 => new WorldValueExpression([new WorldValueToken.Add()]),
            4 => Social("undeclared"),
            _ => new WorldValueExpression([]),
        };
        var doc = Document(Profile() with { CohesionAffinity = expression });
        Assert.False(WorldDefinitionValidator.TryValidateLocally(doc, out var reason));
        Assert.Contains("cohesionAffinity", reason);
        // Failed pair compilation must not leak left/right into unrelated standalone queries.
        Assert.Throws<WorldRuleException>(() => WorldRuleCompiler.CompileSocialQuery(new(Relationship()), Document(Profile())));
    }

    [Fact]
    public void AffinityWorkIncludesIndirectCandidateVisitsAndRejectsOverBudgetAuthoring() {
        var query = new WorldValueExpression([new WorldValueToken.Social(new(Relationship(subject: "argmax:candidates")))]);
        var doc = Document(Profile() with { CandidateBudget = 128, MaxNeighbors = 128, CohesionAffinity = query });
        doc = doc with { PopulationRaw = doc.Population with { CapacityRaw = 128 }, StateRaw = doc.StateRaw! with {
            World = [new(Name("candidates"), CellKind.Int, Capacity: 128, Cells: [new(Name("1"), 1)])],
        } };
        var cost = WorldRuleWorkBudget.Measure(doc);
        Assert.Equal(128L * 128 * 130, cost.FlockAffinityWorkUnitsPerTick);
        Assert.Equal(cost.FlockAffinityWorkUnitsPerTick, cost.WorkUnitsPerTick);
        Assert.False(WorldDefinitionValidator.TryValidateLocally(doc, out var reason));
        Assert.Contains("work units", reason);
    }

    [Fact]
    public void AffinityDocumentRoundTripsStrictly() {
        var doc = Document(Profile() with { CohesionAffinity = Social(), AlignmentAffinity = Social("competence") });
        var bytes = WorldDefinitionSerialization.Serialize(doc);
        Assert.Equal(bytes, WorldDefinitionSerialization.Serialize(WorldDefinitionSerialization.Deserialize(bytes)));
        var text = JsonSerializer.Serialize(Profile() with { CohesionAffinity = Social() }, WorldJsonContext.Default.WorldFlockProfile);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(text.Replace("\"cohesionAffinity\":", "\"misspelledAffinity\":"), WorldJsonContext.Default.WorldFlockProfile));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DenseAffinitiesStayInsideAttentionBudgetWithoutAdditionalSteadyAllocation(bool zeroDivisor) {
        var profile = Profile() with { CandidateBudget = 32, MaxNeighbors = 16, Range = 100, Cohesion = 0, Alignment = 0,
            CohesionAffinity = Social(), AlignmentAffinity = Social("competence") };
        if (zeroDivisor) {
            profile = profile with { CohesionAffinity = new([.. Social().Tokens, new WorldValueToken.Constant(0), new WorldValueToken.Divide()]) };
        }
        var doc = Document(profile);
        doc = doc with { PopulationRaw = doc.Population with { CapacityRaw = 128, NetworkPlayers = 124 } };
        using var subject = Fixtures.FreshServer(doc);
        var baselineDoc = Document(profile with { CohesionAffinity = null, AlignmentAffinity = null }) with { PopulationRaw = doc.PopulationRaw };
        using var baseline = Fixtures.FreshServer(baselineDoc);
        static void Populate(WorldFixture fixture) {
            for (var index = 0; index < 4; index++) { _ = Join(fixture, index, 0); }
            Assert.Equal(124, fixture.Server.Population.SetSimulatedCount(124));
            for (var index = 4; index < 128; index++) {
                fixture.Server.Body(index)!.Pose(Position(0), FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.Zero);
                fixture.Server.Body(index)!.SetIntentSource(IntentSource.Producer("flock"));
            }
        }
        Populate(subject); Populate(baseline);
        static (long Bytes, TimeSpan Elapsed) Run(WorldFixture fixture) {
            for (var index = 0; index < 512; index++) { fixture.Step(); _ = WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 0); }
            var bytes = GC.GetAllocatedBytesForCurrentThread(); var start = Stopwatch.GetTimestamp();
            for (var index = 0; index < 1000; index++) { fixture.Step(); _ = WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 0); }
            return (GC.GetAllocatedBytesForCurrentThread() - bytes, Stopwatch.GetElapsedTime(start));
        }
        var measured = Run(subject); var control = Run(baseline);
        TestContext.Current.TestOutputHelper!.WriteLine($"128 dense bodies, 4096 affinity evaluations/full step + hash x1000, zeroDivisor={zeroDivisor}: {measured.Elapsed.TotalMilliseconds:F3}ms/{measured.Bytes}B; uniform {control.Elapsed.TotalMilliseconds:F3}ms/{control.Bytes}B");
        Assert.Equal(control.Bytes, measured.Bytes);
        var work = subject.Server.Population.FlockStatistics;
        Assert.Equal(128 * 16 * 2, work.AffinityEvaluations);
        Assert.Equal(128 * 32, work.Candidates);
        Assert.Equal(128 * 16 * (zeroDivisor ? 8 : 6), work.AffinityWorkUnits);
        Assert.Equal(zeroDivisor ? 128 * 16 : 0, work.AffinityFailures);
    }
}
