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
public sealed partial class WorldSocialRuleLawTests {
    private static WorldCellName Name(string value) => WorldCellName.Parse(value);
    private static WorldSocialEntityReference Individual(int index) => new(Identity: new("social-test", index, 0));
    private static WorldSocialRelationship Relationship() => new(Individual(0), Individual(1), "helpfulness");
    private static WorldValueExpression Constant(decimal value) => new([new WorldValueToken.Constant(value)]);
    private static WorldValueExpression Clock() => new([new WorldValueToken.SocialClock()]);
    private static WorldValueExpression Query(WorldSocialFacet facet = WorldSocialFacet.Value, WorldSocialRelationship? relationship = null) =>
        new([new WorldValueToken.Social(new(relationship ?? Relationship(), facet))]);
    private static WorldSocialObservation Evidence(decimal value = 1) => new(Relationship(), Individual(2), "help.outcome", Constant(1), Constant(0), Constant(value));
    private static WorldRule Rule(string name, params ActionEffect[] effects) => new(Name(name), effects);
    private static WorldSocialPolicy Policy() => new([new(Name("helpfulness"))], ImpressionCapacity: 256, ImpressionsPerObserver: 256,
        ReceiptCapacity: 512, EvidenceAttemptsPerTick: 128, ExpiredReceiptsPerTick: 128);
    private static WorldDefinition Document(params WorldRule[] rules) => Fixtures.BuildDocument() with { StateRaw = new(Social: Policy()), Rules = rules };
    private static WorldAuthorityCheckpoint Capture(WorldFixture fixture) {
        Assert.True(fixture.Server.TryCaptureCheckpoint(new(0, 0, false, [], 1, [], [], [], null, 0, false, [], []), out var checkpoint, out var reason), reason);
        return checkpoint!;
    }
    private static WorldSocialMemoryCheckpoint Memory(WorldFixture fixture) => Capture(fixture).Server.Social!;
    private static long Read(WorldFixture fixture, string row) => WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, row)!.Cells![0].Value;
    private static WorldStateRow Slot(string row, CellKind kind = CellKind.Int) => new(Name(row), kind, Cells: [new(WorldStateRow.SlotKey, 0)]);

    [Fact]
    public void ObservationQueriesAndDecisionScoringShareTheSameTick() {
        var evidence = Rule("observe", new ActionEffect.ObserveSocial(Evidence()),
            new ActionEffect.SetState("result", Expression: new([new WorldValueToken.SocialResult()])));
        var choice = new WorldRule(Name("choose"), [], Decision: new([
            new(Name("follow"), Query(), [new ActionEffect.SetState("chosen", Value: 1)],
                new ActionPredicate.CompareValue(Query(WorldSocialFacet.Known), ActionStateComparison.Equal, Constant(1), CellKind.Int)),
            new(Name("avoid"), Constant(0.1m), []),
        ], PeriodSeconds: 1));
        var document = Document(evidence, choice) with { StateRaw = new(World: [Slot("chosen"), Slot("result")], Social: Policy()) };
        using var fixture = Fixtures.FreshServer(document);
        fixture.Step();
        Assert.Equal(1, Read(fixture, "chosen"));
        Assert.Equal((long)WorldSocialEvidenceResult.Accepted, Read(fixture, "result"));
        Assert.Equal(0, Assert.Single(Capture(fixture).Server.Decisions).Selected);
        Assert.InRange(Assert.Single(Capture(fixture).Server.Decisions).LastScore, 13000, 13200);
        fixture.Step();
        Assert.Equal((long)WorldSocialEvidenceResult.Duplicate, Read(fixture, "result"));
        Assert.Single(Memory(fixture).Receipts);
    }

    [Fact]
    public void UnknownAbsentIdentityHasBaselineWithoutInventingMemory() {
        var policy = Policy() with { Dimensions = [new(Name("helpfulness"), Baseline: 0.5m)] };
        var doc = Document(Rule("copy", new ActionEffect.SetState("belief", Expression: Query()))) with {
            StateRaw = new(World: [Slot("belief", CellKind.Fixed)], Social: policy),
        };
        using var fixture = Fixtures.FreshServer(doc);
        fixture.Step();
        Assert.Equal(FixedQ4816.One.Value / 2, Read(fixture, "belief"));
        Assert.Empty(Memory(fixture).Impressions);
        Assert.Contains("known=False", fixture.Server.DescribeSocial(WorldPrincipal.Console, new(Relationship())));
    }

    [Fact]
    public void ExplicitGateControlsPerceptionAndInvalidBodyConsumesAnAttempt() {
        var invisible = Rule("invisible", new ActionEffect.ObserveSocial(Evidence())) with {
            Gate = new ActionPredicate.CompareValue(Constant(0), ActionStateComparison.Equal, Constant(1), CellKind.Int),
        };
        var missing = Evidence() with { Relationship = Relationship() with { Observer = new(Body: "body:0") } };
        using var fixture = Fixtures.FreshServer(Document(invisible, Rule("missing", new ActionEffect.ObserveSocial(missing))));
        fixture.Step();
        Assert.Empty(Memory(fixture).Impressions);
        Assert.Equal(1, Memory(fixture).EvidenceAttempts);
        Assert.Equal((int)WorldSocialEvidenceResult.Invalid, Capture(fixture).Server.LastSocialResult);
        Assert.Contains("unresolved", fixture.Server.DescribeSocial(WorldPrincipal.Console, new(missing.Relationship)));
    }

    [Fact]
    public void LiveBodyQueryDoesNotMintMobilityAndSharesItsStableAddress() {
        using var fixture = Fixtures.FreshServer(Document());
        Assert.True(fixture.Server.ApplySession(new SessionRequest.Join(WorldPrincipal.Seat(0), 0, null, WorldProtocol.WireProtocolKey)).Accepted);
        fixture.Step();
        var stable = fixture.Server.Population.ResolveIncarnation(0, fixture.Server.InstanceIdentity);
        Assert.NotNull(stable);
        var before = WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 0);
        var query = new WorldSocialQuery(Relationship() with { Observer = new(Body: "body:0") });
        Assert.Contains(stable.Value.ToString(), fixture.Server.DescribeSocial(WorldPrincipal.Console, query));
        Assert.Equal(before, WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 0));
        Assert.Null(fixture.Server.Population.ResolveIncarnation(-1, "boot"));
    }

    [Fact]
    public void MutationAndReadBackRequireTheirOwnActingPrincipalGrants() {
        using var fixture = Fixtures.FreshServer(Document());
        var stranger = WorldPrincipal.Peer(77, 0);
        var rule = Rule("observe", new ActionEffect.ObserveSocial(Evidence()));
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertWorldRule(stranger, rule));
        fixture.Step();
        Assert.Equal(-1, Capture(fixture).Server.LastSocialResult);
        Assert.Empty(Memory(fixture).Impressions);
        Assert.Contains("denied", fixture.Server.DescribeSocial(stranger, new(Relationship())));
        var reader = WorldPrincipal.Seat(0);
        var readGrant = new WorldGrant(reader, WorldCapability.Observe, GrantSubject.All, false);
        fixture.Server.Revoke(readGrant, WorldPrincipal.Console);
        Assert.Contains("denied", fixture.Server.DescribeSocial(reader, new(Relationship())));
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertWorldRule(WorldPrincipal.Console, rule));
        fixture.Server.Grant(readGrant, WorldPrincipal.Console);
        fixture.Step();
        Assert.Equal((int)WorldSocialEvidenceResult.Accepted, Capture(fixture).Server.LastSocialResult);
        Assert.Contains("known=True", fixture.Server.DescribeSocial(reader, new(Relationship())));
    }

    [Fact]
    public void AttemptBudgetIncludesDuplicatesAndReportsAnExplicitOutcome() {
        var observation = new ActionEffect.ObserveSocial(Evidence());
        using var fixture = Fixtures.FreshServer(Document(Rule("observe", observation, observation, observation)) with {
            StateRaw = new(Social: Policy() with { EvidenceAttemptsPerTick = 2 }),
        });
        fixture.Step();
        Assert.Equal(2, Memory(fixture).EvidenceAttempts);
        Assert.Single(Memory(fixture).Impressions);
        Assert.Equal((int)WorldSocialEvidenceResult.WorkLimited, Capture(fixture).Server.LastSocialResult);
        Assert.Contains("attempts 2/2", fixture.Server.DescribeSocialBudget());
    }

    [Fact]
    public void ForgetDoesNotAllowAnOldRumorToRecreateAnImpression() {
        var observation = new ActionEffect.ObserveSocial(Evidence());
        using var fixture = Fixtures.FreshServer(Document(Rule("observe", observation, new ActionEffect.ForgetSocial(Relationship()), observation)));
        fixture.Step();
        Assert.Empty(Memory(fixture).Impressions);
        Assert.Single(Memory(fixture).Receipts);
        Assert.Equal((int)WorldSocialEvidenceResult.Duplicate, Capture(fixture).Server.LastSocialResult);
    }

    [Fact]
    public void OrdinaryStateMutationRetainsTheSameSocialMemory() {
        using var fixture = Fixtures.FreshServer(Document(Rule("observe", new ActionEffect.ObserveSocial(Evidence()))));
        fixture.Step();
        var before = Assert.Single(Memory(fixture).Impressions);
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateRow(WorldPrincipal.Console, Slot("unrelated")));
        fixture.Step();
        Assert.Equal(before, Assert.Single(Memory(fixture).Impressions));
        Assert.Single(Memory(fixture).Receipts);
    }

    [Fact]
    public void CheckpointWireRoundTripPreservesSocialClockReceiptsDecisionsAndFutureHashes() {
        var evidence = Evidence() with { Sequence = Clock(), OccurredAt = Clock(), Source = Individual(3) };
        var doc = Document(Rule("observe", new ActionEffect.ObserveSocial(evidence)));
        using var original = Fixtures.FreshServer(doc);
        for (var index = 0; index < 8; index++) { original.Step(); }
        var checkpoint = Capture(original);
        Assert.True(WorldAuthorityCheckpointCodec.TryDecode(WorldAuthorityCheckpointCodec.Encode(checkpoint), out var decoded, out var reason), reason);
        Assert.Equal(checkpoint.Server.Social!.Receipts, decoded!.Server.Social!.Receipts);
        using var restored = Fixtures.FreshServer(doc);
        restored.Server.RestoreCheckpoint(decoded);
        Assert.Equal(WorldRuntimeStateHash.HashAuthoritative(original.Server, 7), WorldRuntimeStateHash.HashAuthoritative(restored.Server, 7));
        for (ulong index = 8; index < 100; index++) {
            var context = new FixedStepContext(Tick: index, ElapsedTicks: (index + 1) * Fixtures.StepTicks, StepTicks: Fixtures.StepTicks);
            original.Server.Step(in context); restored.Server.Step(in context);
            Assert.Equal(WorldRuntimeStateHash.HashAuthoritative(original.Server, index), WorldRuntimeStateHash.HashAuthoritative(restored.Server, index));
        }
    }

    [Fact]
    public void AuthorityWirePreservesNegativeMemoryAnchorsAndFutureOriginalEventTimestamps() {
        using var fixture = Fixtures.FreshServer(Document()); fixture.Step();
        var checkpoint = Capture(fixture);
        var bank = new WorldSocialMemory(CompiledWorldSocialPolicy.Compile(Policy()));
        bank.Advance(ulong.MaxValue - 100000);
        var relationship = new WorldSocialImpressionKey(new("social-test", 0, 0), new("social-test", 1, 0), 0);
        var report = new WorldSocialEvidence(relationship, new(new("social-test", 2, 0), "help.outcome", 1),
            bank.EngineTick, FixedQ4816.One.Value, FixedQ4816.One.Value, new("social-test", 3, 0));
        Assert.Equal(WorldSocialEvidenceResult.Accepted, bank.Observe(report));
        bank.Advance(bank.EngineTick + 10000);
        var carried = bank.CaptureObserver(relationship.Observer, checkpoint.Server.LastCompletedEngineTicks);
        Assert.True(carried.Impressions[0].UpdatedAt < 0); Assert.True(carried.Receipts[0].LocalOccurredAt < 0);
        var changed = checkpoint with { Server = checkpoint.Server with { Social = carried } };
        Assert.True(WorldAuthorityCheckpointCodec.TryDecode(WorldAuthorityCheckpointCodec.Encode(changed), out var decoded, out var reason), reason);
        Assert.Equal(carried.Impressions, decoded!.Server.Social!.Impressions);
        Assert.Equal(carried.Receipts, decoded.Server.Social.Receipts);
        fixture.Server.RestoreCheckpoint(decoded);
        Assert.Contains("ageTicks=10000", fixture.Server.DescribeSocial(WorldPrincipal.Console, new(Relationship(), WorldSocialFacet.Age)));
        var restored = WorldSocialMemory.Restore(bank.Policy, Memory(fixture));
        Assert.Equal(WorldSocialEvidenceResult.Upgraded, restored.Observe(report with { Source = null }));
        Assert.Equal(WorldSocialEvidenceResult.Duplicate, restored.Observe(report with { Source = null }));
        // Sign/high limbs affect the digest, not just the low 64 bits that a truncated codec might retain.
        var shifted = carried with { Impressions = [carried.Impressions[0] with { UpdatedAt = carried.Impressions[0].UpdatedAt - ((Int128)1 << 64) }] };
        Assert.NotEqual(WorldSocialMemory.Restore(bank.Policy, carried).StateHash, WorldSocialMemory.Restore(bank.Policy, shifted).StateHash);
        var shiftedReceipt = carried with { Receipts = [carried.Receipts[0] with { LocalOccurredAt = carried.Receipts[0].LocalOccurredAt - ((Int128)1 << 64) }] };
        Assert.NotEqual(WorldSocialMemory.Restore(bank.Policy, carried).StateHash, WorldSocialMemory.Restore(bank.Policy, shiftedReceipt).StateHash);
        foreach (var anchor in new[] { (Int128)(-1), -((Int128)1 << 64) - 17, Int128.MinValue }) {
            var extreme = carried with {
                Impressions = [carried.Impressions[0] with { UpdatedAt = anchor }],
                Receipts = [carried.Receipts[0] with { LocalOccurredAt = anchor }],
            };
            var wire = WorldAuthorityCheckpointCodec.Encode(checkpoint with { Server = checkpoint.Server with { Social = extreme } });
            Assert.True(WorldAuthorityCheckpointCodec.TryDecode(wire, out var roundTrip, out reason), reason);
            Assert.Equal(extreme.Impressions, roundTrip!.Server.Social!.Impressions);
            Assert.Equal(extreme.Receipts, roundTrip.Server.Social.Receipts);
            Assert.Equal(WorldSocialMemory.Restore(bank.Policy, extreme).StateHash,
                WorldSocialMemory.Restore(bank.Policy, roundTrip.Server.Social).StateHash);
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void InvalidSocialCheckpointRefusesBeforeChangingLiveState(int variant) {
        using var fixture = Fixtures.FreshServer(Document(Rule("observe", new ActionEffect.ObserveSocial(Evidence()))));
        fixture.Step();
        var checkpoint = Capture(fixture);
        var server = checkpoint.Server;
        server = variant switch {
            0 => server with { Social = null },
            1 => server with { Social = server.Social! with { EngineTick = 0 } },
            2 => server with { Social = server.Social! with { PolicyIdentity = "wrong policy" } },
            _ => server with { LastSocialResult = 256 },
        };
        var hash = WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 0);
        Assert.Throws<InvalidOperationException>(() => fixture.Server.RestoreCheckpoint(checkpoint with { Server = server }));
        Assert.Equal(hash, WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 0));
    }

    [Fact]
    public void ComparisonArithmeticFailureClosesGateWithoutExecutingEffects() {
        var division = new WorldValueExpression([new WorldValueToken.Constant(1), new WorldValueToken.Constant(0), new WorldValueToken.Divide()]);
        using var fixture = Fixtures.FreshServer(Document(Rule("unsafe", new ActionEffect.ObserveSocial(Evidence())) with {
            Gate = new ActionPredicate.CompareValue(division, ActionStateComparison.Equal, Constant(0)),
        }));
        fixture.Step();
        Assert.Empty(Memory(fixture).Receipts);
        Assert.Equal(0, Memory(fixture).EvidenceAttempts);
    }

    [Fact]
    public void WirePreservesConflictingReportsAndDirectUpgradeWithoutInventingEvents() {
        using var fixture = Fixtures.FreshServer(Document(Rule("evidence",
            new ActionEffect.ObserveSocial(Evidence() with { Source = Individual(3) }),
            new ActionEffect.ObserveSocial(Evidence(-1) with { Source = Individual(4) }),
            new ActionEffect.ObserveSocial(Evidence(-1)))));
        fixture.Step();
        var state = Capture(fixture);
        Assert.Equal((int)WorldSocialEvidenceResult.Upgraded, state.Server.LastSocialResult);
        var receipt = Assert.Single(state.Server.Social!.Receipts);
        Assert.True(receipt.Direct);
        Assert.True(receipt.ConflictSeen);
        Assert.Equal(1UL, Assert.Single(state.Server.Social.Impressions).IndependentEvents);
        Assert.True(WorldAuthorityCheckpointCodec.TryDecode(WorldAuthorityCheckpointCodec.Encode(state), out var decoded, out var reason), reason);
        Assert.Equal(receipt, Assert.Single(decoded!.Server.Social!.Receipts));
        fixture.Server.RestoreCheckpoint(decoded);
        Assert.Equal(receipt, Assert.Single(Memory(fixture).Receipts));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MalformedWireCountIsRefusedBeforeAllocatingItsClaimedCapacity(bool receipts) {
        using var fixture = Fixtures.FreshServer(Document());
        fixture.Step();
        var checkpoint = Capture(fixture);
        // Deliberately violates IReadOnlyList enumeration/count consistency to encode an impossible wire count.
        var social = receipts
            ? checkpoint.Server.Social! with { Receipts = new MissingRows<WorldSocialReceiptCheckpoint>() }
            : checkpoint.Server.Social! with { Impressions = new MissingRows<WorldSocialImpressionCheckpoint>() };
        var malformed = checkpoint with { Server = checkpoint.Server with { Social = social } };
        var bytes = WorldAuthorityCheckpointCodec.Encode(malformed);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.False(WorldAuthorityCheckpointCodec.TryDecode(bytes, out _, out var reason));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Contains(receipts ? "social receipts" : "social impressions", reason);
        Assert.True(allocated < 4 * 1024 * 1024, $"Decoder allocated {allocated} bytes for {bytes.Length} bytes of input");
    }

    private sealed class MissingRows<T> : IReadOnlyList<T> {
        public int Count => CompiledWorldSocialPolicy.MaximumEntries;
        public T this[int index] => throw new InvalidOperationException();
        public IEnumerator<T> GetEnumerator() { yield break; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void ReservedSocialOwnershipSurvivesAuthorityWireRestoreAndRefusesRuleWrites() {
        using var fixture = Fixtures.FreshServer(Document(Rule("observe", new ActionEffect.ObserveSocial(Evidence()))));
        var checkpoint = Capture(fixture);
        var bank = WorldSocialMemory.Restore(CompiledWorldSocialPolicy.Compile(Policy()), checkpoint.Server.Social!);
        var observer = new WorldEntityAddress("social-test", 0, 0);
        Assert.True(bank.TryReserveImport(new("upstream", 17), [new(observer, 2, 4)], out var reason), reason);
        checkpoint = checkpoint with { Server = checkpoint.Server with { Social = bank.Capture() } };
        Assert.True(WorldAuthorityCheckpointCodec.TryDecode(WorldAuthorityCheckpointCodec.Encode(checkpoint), out var decoded, out reason), reason);
        Assert.Equal(bank.StateHash, WorldSocialMemory.Restore(bank.Policy, decoded!.Server.Social!).StateHash);
        fixture.Server.RestoreCheckpoint(decoded);
        Assert.Contains("imports 1 groups/1 observers holding 2 impressions/4 receipts", fixture.Server.DescribeSocial(WorldPrincipal.Console));
        fixture.Step();
        Assert.Equal((int)WorldSocialEvidenceResult.ObserverReserved, Capture(fixture).Server.LastSocialResult);
        Assert.Empty(Memory(fixture).Impressions); Assert.Single(Memory(fixture).ImportReservations!);
        var live = Capture(fixture);
        var hash = WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 0);
        var held = Assert.Single(live.Server.Social!.ImportReservations!);
        var invalid = live with { Server = live.Server with { Social = live.Server.Social with {
            ImportReservations = [held with { Members = [new(observer, -1, 4)] }],
        } } };
        Assert.Throws<InvalidOperationException>(() => fixture.Server.RestoreCheckpoint(invalid));
        Assert.Equal(hash, WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 0));
        Assert.True(WorldAuthorityCheckpointCodec.TryDecode(WorldAuthorityCheckpointCodec.Encode(live), out var repeated, out reason), reason);
        fixture.Server.RestoreCheckpoint(repeated!);
        Assert.Equal(hash, WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImpossibleReservationWireCountsRefuseBeforeAllocatingClaims(bool members) {
        using var fixture = Fixtures.FreshServer(Document());
        var checkpoint = Capture(fixture);
        IReadOnlyList<WorldSocialImportReservationCheckpoint> rows = members
            ? [new(new("upstream", 1), new MissingReservationRows<WorldSocialImportAllowance>())]
            : new MissingReservationRows<WorldSocialImportReservationCheckpoint>();
        var malformed = checkpoint with { Server = checkpoint.Server with { Social = checkpoint.Server.Social! with { ImportReservations = rows } } };
        var bytes = WorldAuthorityCheckpointCodec.Encode(malformed);
        Assert.False(WorldAuthorityCheckpointCodec.TryDecode(bytes, out _, out var reason));
        Assert.Contains(members ? "social import observers" : "social import reservation", reason);
    }

    private sealed class MissingReservationRows<T> : IReadOnlyList<T> {
        public int Count => WorldSocialMemory.MaximumReservedObservers;
        public T this[int index] => throw new InvalidOperationException();
        public IEnumerator<T> GetEnumerator() { yield break; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void CompilerRefusesMalformedReferencesDimensionsFacetsAndTypes(int variant) {
        var relationship = Relationship();
        var query = new WorldSocialQuery(relationship);
        query = variant switch {
            0 => query with { Relationship = relationship with { Observer = new() } },
            1 => query with { Relationship = relationship with { Observer = new("body:0", new("x", 0, 0)) } },
            2 => query with { Relationship = relationship with { Dimension = "undeclared" } },
            3 => query with { Facet = (WorldSocialFacet)255 },
            4 => query with { Relationship = relationship with { Observer = new(Body: "each") } },
            _ => query with { Relationship = relationship with { Observer = new(Identity: new("x", -1, 0)) } },
        };
        Assert.Throws<WorldRuleException>(() => WorldRuleCompiler.CompileSocialQuery(query, Document()));
        var mismatch = Rule("mismatch", new ActionEffect.ObserveSocial(Evidence() with { Sequence = Query() }));
        Assert.Throws<WorldRuleException>(() => WorldRuleCompiler.Compile(mismatch, Document(mismatch)));
    }

    [Fact]
    public void DisabledPolicyAndUnknownJsonMembersFailClosed() {
        Assert.Throws<WorldRuleException>(() => WorldRuleCompiler.CompileSocialQuery(new(Relationship()), Fixtures.BuildDocument()));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"relationship\":{},\"magic\":true}", WorldJsonContext.Default.WorldSocialQuery));
        var document = Document(Rule("observe", new ActionEffect.ObserveSocial(Evidence())));
        Assert.True(WorldDefinitionValidator.TryValidateLocally(document, out var reason), reason);
        var bytes = WorldDefinitionSerialization.Serialize(document);
        Assert.Equal(bytes, WorldDefinitionSerialization.Serialize(WorldDefinitionSerialization.Deserialize(bytes)));
    }

    [Fact]
    public void QueryCostIncludesEveryReductionVisitAndBothComparisonExpressions() {
        var row = new WorldStateRow(Name("candidates"), CellKind.Int, Capacity: 256, Cells: [new(Name("0"), 1)]);
        var relationship = Relationship() with { Subject = new(Body: "argmax:candidates") };
        var rule = Rule("observe", new ActionEffect.ObserveSocial(Evidence() with { Relationship = relationship })) with {
            Gate = new ActionPredicate.CompareValue(Query(relationship: relationship), ActionStateComparison.Equal, Query(relationship: relationship)),
        };
        var doc = Document(rule) with { StateRaw = new(World: [row], Social: Policy()) };
        Assert.True(WorldRuleWorkBudget.Measure(doc).WorkUnitsPerTick >= 3 * 256);
    }

    [Fact]
    public void DenseSocialQueriesAndIngestionAddNoSteadyStateAllocation() {
        var effects = Enumerable.Range(0, 128).Select(index => (ActionEffect)new ActionEffect.ObserveSocial(Evidence() with {
            Relationship = Relationship() with { Observer = Individual(index + 100) }, Sequence = Clock(), OccurredAt = Clock(),
        })).ToArray();
        // Four rules stay under the per-rule effect ceiling; 128 observers fill 512 receipts and reclaim boundedly.
        var rules = effects.Chunk(32).Select((part, index) => Rule($"observe-{index}", part) with {
            Gate = new ActionPredicate.CompareValue(Query(), ActionStateComparison.Equal, Constant(0)),
        }).ToArray();
        var doc = Document(rules) with { StateRaw = new(Social: Policy() with { EvidenceLifetimeSeconds = 0.01m }) };
        Assert.True(WorldDefinitionValidator.TryValidateLocally(doc, out var reason), reason);
        using var fixture = Fixtures.FreshServer(doc);
        using var baseline = Fixtures.FreshServer(doc with { Rules = [] });
        static (long Bytes, TimeSpan Time) Run(WorldFixture world) {
            for (var index = 0; index < 100; index++) { world.Step(); _ = WorldRuntimeStateHash.HashAuthoritative(world.Server, 0); }
            var before = GC.GetAllocatedBytesForCurrentThread(); var start = Stopwatch.GetTimestamp();
            for (var index = 0; index < 1000; index++) { world.Step(); _ = WorldRuntimeStateHash.HashAuthoritative(world.Server, 0); }
            return (GC.GetAllocatedBytesForCurrentThread() - before, Stopwatch.GetElapsedTime(start));
        }
        var withSocial = Run(fixture); var without = Run(baseline);
        TestContext.Current.TestOutputHelper!.WriteLine($"128 social ingestions + gates + full server/hash x1000: {withSocial.Time.TotalMilliseconds:F3}ms, {withSocial.Bytes}B; baseline {without.Time.TotalMilliseconds:F3}ms, {without.Bytes}B");
        Assert.Equal(without.Bytes, withSocial.Bytes);
        Assert.Equal(128, Memory(fixture).Impressions.Count);
        Assert.Equal(128, Memory(fixture).EvidenceAttempts);
    }
}
