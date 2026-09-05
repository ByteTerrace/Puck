using Puck.Assets.Documents;
using Puck.Maths;
using Puck.Physics.Motion;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the compositional rule extensions at their authoring, deterministic execution, and atomicity
/// boundaries.</summary>
public sealed class WorldRuleExtensionLawTests {
    // A rule count large enough to saturate the work budget and to exercise per-rule key resolution at scale.
    private const int ManyRules = 128;
    [Fact]
    public void ExtendedVocabularyRoundTripsThroughTheStrictWorldDocumentWireShape() {
        var definition = Document(
            state: [Slot("source", 2L), Slot("target", 0L)],
            rules: [new WorldRule(
                Name: Name("round-trip"),
                Gate: new ActionPredicate.Not(Predicate: new ActionPredicate.Any(Predicates: [
                    new ActionPredicate.CompareState(State: "source", Comparison: ActionStateComparison.Equal, Value: 0m),
                ])),
                Effects: [new ActionEffect.Transaction(Effects: [
                    new WorldTransactionStep.SetCell(
                        State: "target",
                        Expression: new WorldValueExpression(Tokens: [
                            new WorldValueToken.State(Name: "source"),
                            new WorldValueToken.Constant(Value: 3m),
                            new WorldValueToken.Add(),
                        ])
                    ),
                    new WorldTransactionStep.ScheduleCell(State: "target", DelaySeconds: 0.01m),
                ])]
            )]
        );

        var parsed = WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: definition));
        var rule = Assert.Single(collection: parsed.Rules ?? []);
        var not = Assert.IsType<ActionPredicate.Not>(@object: rule.Gate);
        _ = Assert.IsType<ActionPredicate.Any>(@object: not.Predicate);
        var transaction = Assert.IsType<ActionEffect.Transaction>(@object: Assert.Single(collection: rule.Effects));
        var set = Assert.IsType<WorldTransactionStep.SetCell>(@object: transaction.Effects[0]);

        Assert.Collection(
            collection: Assert.IsType<WorldValueExpression>(@object: set.Expression).Tokens,
            token => _ = Assert.IsType<WorldValueToken.State>(@object: token),
            token => _ = Assert.IsType<WorldValueToken.Constant>(@object: token),
            token => _ = Assert.IsType<WorldValueToken.Add>(@object: token)
        );
        _ = Assert.IsType<WorldTransactionStep.ScheduleCell>(@object: transaction.Effects[1]);
    }

    [Fact]
    public void AnyNotAndNumericExpressionComposeWithoutChangingNumericKind() {
        var definition = Document(
            state: [Slot("a", 1L), Slot("b", 0L), Slot("result", 0L)],
            rules: [new WorldRule(
                Name: Name("boolean-expression"),
                Gate: new ActionPredicate.Any(Predicates: [
                    new ActionPredicate.CompareState(State: "a", Comparison: ActionStateComparison.Equal, Value: 2m),
                    new ActionPredicate.Not(Predicate: new ActionPredicate.CompareState(State: "b", Comparison: ActionStateComparison.Equal, Value: 1m)),
                ]),
                Effects: [new ActionEffect.SetState(
                    State: "result",
                    Expression: new WorldValueExpression(Tokens: [
                        new WorldValueToken.State(Name: "a"),
                        new WorldValueToken.Constant(Value: 2m),
                        new WorldValueToken.Add(),
                        new WorldValueToken.Constant(Value: 3m),
                        new WorldValueToken.Multiply(),
                        new WorldValueToken.Constant(Value: 0m),
                        new WorldValueToken.Constant(Value: 8m),
                        new WorldValueToken.Clamp(),
                    ])
                )]
            )]
        );

        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();

        Assert.Equal(expected: 8L, actual: Value(fixture: fixture, row: "result"));
    }

    [Fact]
    public void TransactionRefusalRollsBackMainBranchAndRunsFailureBranch() {
        var definition = Document(
            state: [Slot("target", 5L), Slot("failed", 0L), Keyed("bag", 2, [])],
            rules: [new WorldRule(
                Name: Name("atomic"),
                Effects: [new ActionEffect.Transaction(
                    Effects: [
                        new WorldTransactionStep.SetCell(State: "target", Value: 9m),
                        new WorldTransactionStep.RemoveCell(State: "bag", Key: "missing"),
                    ],
                    OnFailure: [new WorldTransactionStep.SetCell(State: "failed", Value: 1m)]
                )]
            )]
        );

        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();

        Assert.Equal(expected: 5L, actual: Value(fixture: fixture, row: "target"));
        Assert.Equal(expected: 1L, actual: Value(fixture: fixture, row: "failed"));
    }

    [Fact]
    public void CrossDomainTransactionPreflightDoesNotLeakCueOrBodyStateOnLaterRefusal() {
        var bounded = new WorldStateRow(
            Name: Name("bounded"),
            Kind: CellKind.Int,
            Min: 0L,
            Max: 10L,
            Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 2L)]
        );
        var definition = Document(
            state: [bounded, Slot("failed", 0L)],
            rules: [new WorldRule(
                Name: Name("cross-domain-atomic"),
                Effects: [new ActionEffect.Transaction(
                    Effects: [
                        new WorldTransactionStep.EmitCueStep(Name: "atomic.probe", Key: "0"),
                        new WorldTransactionStep.SetBodyVerticalVelocityStep(Key: "0", Velocity: 7m),
                        new WorldTransactionStep.SetCell(State: "bounded", Value: 99m),
                    ],
                    OnFailure: [new WorldTransactionStep.SetCell(State: "failed", Value: 1m)]
                )]
            )]
        );
        using var fixture = Fixtures.FreshServer(definition: definition);
        WorldGameplayCue? cue = null;

        Assert.True(fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: WorldPrincipal.Seat(slot: 0), Slot: 0, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);
        fixture.Server.GameplayCueTap = value => cue = value;
        fixture.Step();

        Assert.Null(cue);
        Assert.True(condition: fixture.Server.Body(index: 0)!.CaptureTransferState().VerticalVelocity < FixedQ4816.Zero);
        Assert.Equal(expected: 2L, actual: Value(fixture: fixture, row: "bounded"));
        Assert.Equal(expected: 1L, actual: Value(fixture: fixture, row: "failed"));
    }

    [Fact]
    public void TransactionPreflightRejectsASelfDesignationBeforeEarlierWritesCanLeak() {
        var definition = Document(
            state: [Slot("target", 5L), Slot("failed", 0L)],
            rules: [new WorldRule(
                Name: Name("self-designation-atomic"),
                Effects: [new ActionEffect.Transaction(
                    Effects: [
                        new WorldTransactionStep.SetCell(State: "target", Value: 9m),
                        new WorldTransactionStep.DesignateBodyStep(
                            Key: "0",
                            Register: "focus",
                            Kind: WorldBodyDesignationKind.Body,
                            TargetKey: "0"
                        ),
                    ],
                    OnFailure: [new WorldTransactionStep.SetCell(State: "failed", Value: 1m)]
                )]
            )]
        ) with {
            TargetRegistersRaw = [new WorldTargetRegister(
                Name: "focus",
                MaximumRange: 100f,
                MaximumHalfAngleDegrees: 180f,
                RequiresLineOfSight: false
            )],
        };
        using var fixture = Fixtures.FreshServer(definition: definition);

        Assert.True(fixture.Server.ApplySession(request: new SessionRequest.Join(
            Principal: WorldPrincipal.Seat(slot: 0),
            Slot: 0,
            IdentityName: null,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);
        fixture.Step();

        Assert.Equal(expected: 5L, actual: Value(fixture: fixture, row: "target"));
        Assert.Equal(expected: 1L, actual: Value(fixture: fixture, row: "failed"));
    }

    [Fact]
    public void RuntimeRefusalsAreCountedWithoutGrowingTheDiagnosticShape() {
        var definition = Document(
            state: [Slot("target", 0L)],
            rules: [new WorldRule(
                Name: Name("diagnostic"),
                Effects: [new ActionEffect.SetState(
                    State: "target",
                    Expression: new WorldValueExpression(Tokens: [
                        new WorldValueToken.Constant(Value: 1m),
                        new WorldValueToken.Constant(Value: 0m),
                        new WorldValueToken.Divide(),
                    ])
                )]
            )]
        );
        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();
        fixture.Step();

        var diagnostic = Assert.Single(collection: fixture.Server.RuleRuntimeDiagnostics());
        Assert.Equal(expected: WorldRuleEffectRefusal.Arithmetic, actual: diagnostic.Refusal);
        Assert.Equal(expected: 2UL, actual: diagnostic.Count);
        Assert.Equal(expected: 2UL, actual: diagnostic.LastTick);
        Assert.Contains(expectedSubstring: "count=2", actualString: fixture.Server.DescribeRuleRuntimeDiagnostics(), comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void InteractionRangeCompilesFromExactDecimalWithoutABinary32RoundTrip() {
        const string Property = "tag";
        var definition = Document(state: [Keyed(Property, 2, [])], rules: []) with {
            Properties = new WorldPropertyRegistrySection(Names: [Property]),
            Interactions = new WorldInteractionsSection(Interactions: [new WorldInteraction(
                Name: Name("exact-range"),
                Left: Property,
                Right: Property,
                CoOccurrence: WorldInteractionCoOccurrence.Distance,
                Range: 0.100006103515625m,
                Effects: [new ActionEffect.EmitCue(Name: "range.hit")]
            )]),
        };

        var interaction = Assert.Single(collection: WorldRuleCompiler.CompileAllInteractions(definition: definition));

        Assert.Equal(expected: 6_554L, actual: interaction.Interaction!.Value.Range.Value);
    }

    [Fact]
    public void AggregateRuleBudgetRejectsAValidButPathologicallyExpensiveProgram() {
        var effects = Enumerable.Range(start: 0, count: WorldRuleCapacity.MaxEffectsPerRule)
            .Select(static _ => (ActionEffect)new ActionEffect.AddState(State: "counter", Value: 1m))
            .ToArray();
        var rules = Enumerable.Range(start: 0, count: ManyRules)
            .Select(index => new WorldRule(Name: Name($"heavy{index}"), Effects: effects))
            .ToArray();
        var definition = Document(state: [Slot("counter", 0L)], rules: rules);

        var budget = WorldRuleWorkBudget.Measure(definition: definition);

        Assert.True(condition: budget.WorkUnitsPerTick > WorldRuleCapacity.MaxWorkUnitsPerTick);
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason));
        Assert.Contains(expectedSubstring: "work units per tick", actualString: reason, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void DynamicBodyKeysDoNotAllocatePerRuleEvaluationAfterWarmup() {
        static WorldRule[] Rules(string key) => Enumerable.Range(start: 0, count: ManyRules)
            .Select(index => new WorldRule(
                Name: WorldCellName.Parse(candidate: $"key-{index}"),
                Gate: new ActionPredicate.CompareState(State: "values", Comparison: ActionStateComparison.Equal, Value: 2m, Key: key),
                Effects: [new ActionEffect.SetState(State: "target", Value: 1m)]
            )).ToArray();
        var state = new WorldStateRow[] {
            Keyed("index", 1, [Cell("source", ManyRules - 1)]),
            Keyed(
                "values",
                ManyRules,
                [Cell((ManyRules - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), 1L)]
            ),
            Slot("target", 0L),
        };
        var dynamicDefinition = Document(
            state: [
                .. state,
            ],
            rules: Rules(key: "$cell:index:source")
        );
        var staticDefinition = Document(state: state, rules: Rules(key: (ManyRules - 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        using var dynamicFixture = Fixtures.FreshServer(definition: dynamicDefinition);
        using var staticFixture = Fixtures.FreshServer(definition: staticDefinition);

        for (var warmup = 0; warmup < 16; warmup++) {
            dynamicFixture.Step();
            staticFixture.Step();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var sample = 0; sample < 256; sample++) {
            dynamicFixture.Step();
        }
        var dynamicAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var sample = 0; sample < 256; sample++) {
            staticFixture.Step();
        }
        var staticAllocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(
            condition: dynamicAllocated <= (staticAllocated + (64 * 1024)),
            userMessage: $"dynamic={dynamicAllocated}, static={staticAllocated}; dynamic body keys must reuse the bounded cache"
        );
    }

    [Fact]
    public void ReplayAuthoritativeTraceDetectsStateChangesThatPoseHashesCannotSee() {
        static WorldReplaySnapshot Snapshot(WorldDefinition definition) => new() {
            DefinitionJson = WorldDefinitionSerialization.Serialize(definition: definition),
            MountedAddons = [],
            RecordedHashes = new ulong[2],
            RecordedAuthoritativeHashes = new ulong[2],
            Seats = [],
            SimulationRate = (uint)definition.SimulationRateHz,
            Ticks = [
                new WorldReplayTickInput(Authority: [], Intents: []),
                new WorldReplayTickInput(Authority: [], Intents: []),
            ],
        };
        var evolving = Document(
            state: [Slot("counter", 0L)],
            rules: [new WorldRule(Name: Name("advance"), Effects: [new ActionEffect.AddState(State: "counter", Value: 1m)])]
        );
        var inert = evolving with {
            Rules = [new WorldRule(Name: Name("advance"), Effects: [new ActionEffect.AddState(State: "counter", Value: 0m)])],
        };
        using var fixture = Fixtures.FreshServer(definition: evolving);

        var evolvingTraces = Snapshot(definition: evolving).DriveTraces(
            profiles: fixture.Server.Profiles,
            engines: [],
            addonHostFactory: static (_, _) => new NullAddonHost()
        );
        var inertTraces = Snapshot(definition: inert).DriveTraces(
            profiles: fixture.Server.Profiles,
            engines: [],
            addonHostFactory: static (_, _) => new NullAddonHost()
        );

        Assert.Equal(expected: inertTraces.Pose, actual: evolvingTraces.Pose);
        Assert.NotEqual(expected: inertTraces.Authoritative[0], actual: evolvingTraces.Authoritative[0]);
    }

    [Fact]
    public void TransactionPreflightObservesEarlierWritesBeforeAdmittingLaterExpressions() {
        var bounded = new WorldStateRow(
            Name: Name("bounded"),
            Kind: CellKind.Int,
            Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 2L)],
            Min: 0L,
            Max: 6L
        );
        var definition = Document(
            state: [bounded, Slot("failed", 0L)],
            rules: [new WorldRule(
                Name: Name("sequential-preflight"),
                Effects: [new ActionEffect.Transaction(
                    Effects: [
                        new WorldTransactionStep.SetCell(State: "bounded", Value: 5m),
                        new WorldTransactionStep.AddCell(
                            State: "bounded",
                            Expression: new WorldValueExpression(Tokens: [new WorldValueToken.State(Name: "bounded")])
                        ),
                    ],
                    OnFailure: [new WorldTransactionStep.SetCell(State: "failed", Value: 1m)]
                )]
            )]
        );

        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();

        Assert.Equal(expected: 2L, actual: Value(fixture: fixture, row: "bounded"));
        Assert.Equal(expected: 1L, actual: Value(fixture: fixture, row: "failed"));
    }

    [Fact]
    public void ExpressionArithmeticFailureIsATransactionalRefusalNotAProcessException() {
        var definition = Document(
            state: [Slot("target", 5L), Slot("failed", 0L)],
            rules: [new WorldRule(
                Name: Name("arithmetic-refusal"),
                Effects: [new ActionEffect.Transaction(
                    Effects: [new WorldTransactionStep.SetCell(
                        State: "target",
                        Expression: new WorldValueExpression(Tokens: [
                            new WorldValueToken.Constant(Value: 1m),
                            new WorldValueToken.Constant(Value: 0m),
                            new WorldValueToken.Divide(),
                        ])
                    )],
                    OnFailure: [new WorldTransactionStep.SetCell(State: "failed", Value: 1m)]
                )]
            )]
        );

        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();

        Assert.Equal(expected: 5L, actual: Value(fixture: fixture, row: "target"));
        Assert.Equal(expected: 1L, actual: Value(fixture: fixture, row: "failed"));
    }

    [Fact]
    public void FixedExpressionOverflowIsATransactionalRefusalRatherThanWrapping() {
        var definition = Document(
            state: [FixedSlot("target", FixedQ4816.FromInteger(value: 5).Value), Slot("failed", 0L)],
            rules: [new WorldRule(
                Name: Name("fixed-overflow-refusal"),
                Effects: [new ActionEffect.Transaction(
                    Effects: [new WorldTransactionStep.SetCell(
                        State: "target",
                        Expression: new WorldValueExpression(Tokens: [
                            new WorldValueToken.Constant(Value: 100_000_000m),
                            new WorldValueToken.Constant(Value: 100_000_000m),
                            new WorldValueToken.Multiply(),
                        ])
                    )],
                    OnFailure: [new WorldTransactionStep.SetCell(State: "failed", Value: 1m)]
                )]
            )]
        );

        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();

        Assert.Equal(expected: FixedQ4816.FromInteger(value: 5).Value, actual: Value(fixture: fixture, row: "target"));
        Assert.Equal(expected: 1L, actual: Value(fixture: fixture, row: "failed"));
    }

    [Fact]
    public void ScheduleUsesSimulationTicksAndRoundsUpRatherThanFiringEarly() {
        var definition = Document(
            state: [Keyed("deadlines", 4, [])],
            rules: [new WorldRule(
                Name: Name("schedule"),
                Mode: ActionTriggerMode.Edge,
                Effects: [new ActionEffect.ScheduleState(State: "deadlines", DelaySeconds: 0.01m, Key: "job")]
            )]
        );

        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();

        // The first server step is tick 1. At 240 Hz, 0.01 s is 2.4 ticks and rounds up to a delay of 3: due tick 4.
        Assert.Equal(expected: 4L, actual: Value(fixture: fixture, row: "deadlines", key: "job"));
    }

    [Fact]
    public void FilteredReduceAndArgmaxSeeOnlyEligibleLiveBodies() {
        var definition = Document(
            state: [
                Keyed("scores", 4, [Cell("0", 100L), Cell("1", 7L)]),
                Keyed("eligible", 4, [Cell("0", 0L), Cell("1", 1L)]),
                Slot("matched", 0L),
            ],
            rules: [new WorldRule(
                Name: Name("filtered-selectors"),
                Gate: new ActionPredicate.All(Predicates: [
                    new ActionPredicate.CompareState(State: "$argmax:scores:where:eligible", Comparison: ActionStateComparison.Equal, Value: 1m),
                    new ActionPredicate.CompareState(State: "$reduce:sum:scores:where:eligible", Comparison: ActionStateComparison.Equal, Value: 7m),
                ]),
                Effects: [new ActionEffect.SetState(State: "matched", Value: 1m)]
            )]
        );

        using var fixture = Fixtures.FreshServer(definition: definition);

        Assert.True(fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: WorldPrincipal.Seat(slot: 0), Slot: 0, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);
        Assert.True(fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: WorldPrincipal.Seat(slot: 1), Slot: 1, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);
        fixture.Step();

        Assert.Equal(expected: 1L, actual: Value(fixture: fixture, row: "matched"));
    }

    [Fact]
    public void EmitCuePublishesStablePayloadBodyAndTick() {
        var definition = Document(
            state: [],
            rules: [new WorldRule(
                Name: Name("cue"),
                Mode: ActionTriggerMode.Edge,
                Effects: [new ActionEffect.EmitCue(Name: "round.start", Payload: "blue", Key: "0")]
            )]
        );

        using var fixture = Fixtures.FreshServer(definition: definition);
        WorldGameplayCue? observed = null;

        Assert.True(fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: WorldPrincipal.Seat(slot: 0), Slot: 0, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);
        fixture.Server.GameplayCueTap = cue => observed = cue;
        fixture.Step();

        var cue = Assert.IsType<WorldGameplayCue>(@object: observed);
        Assert.Equal(expected: "round.start", actual: cue.Name);
        Assert.Equal(expected: "blue", actual: cue.Payload);
        Assert.Equal(expected: 0, actual: cue.Body);
        Assert.Equal(expected: 1UL, actual: cue.Tick);
    }

    [Fact]
    public void AudioCueTableAdmitsTokensEmittedByThisWorldRules() {
        var rule = new WorldRule(
            Name: Name("custom-audio"),
            Effects: [new ActionEffect.EmitCue(Name: "round.start")]
        );
        var audio = new WorldAudioDefaults(
            MasterGain: 1f,
            DefaultSpeakerRadius: 10f,
            DefaultCurve: WorldAudioDefaults.CurveLinear,
            DefaultBedFadeSeconds: 0f,
            Listener: WorldAudioDefaults.ListenerFocus,
            Cues: [new WorldAudioCue(Event: "round.start", PatchId: "missing", Placement: WorldAudioCue.PlacementListener)]
        );
        var withProducer = Document(state: [], rules: [rule]) with { AudioRaw = audio };
        var withoutProducer = withProducer with { Rules = [] };

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: withProducer, reason: out var producerReason));
        Assert.Contains(expectedSubstring: "patchId 'missing'", actualString: producerReason, comparisonType: StringComparison.Ordinal);
        Assert.DoesNotContain(expectedSubstring: "neither emitted by a world rule", actualString: producerReason, comparisonType: StringComparison.Ordinal);

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: withoutProducer, reason: out var absentReason));
        Assert.Contains(expectedSubstring: "neither emitted by a world rule", actualString: absentReason, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void WorldBodyEffectWritesKinematicStateWithoutMintingAnAffectingBody() {
        var definition = Document(
            state: [],
            rules: [new WorldRule(
                Name: Name("launch"),
                Mode: ActionTriggerMode.Edge,
                Effects: [
                    new ActionEffect.SetBodyVerticalVelocity(Key: "0", Velocity: 5m),
                    new ActionEffect.ScaleBodyVerticalVelocity(Key: "0", Factor: 0.5m),
                    new ActionEffect.ApplyBodyImpulse(Key: "0", BodyDirection: new DocumentVector3(x: 0f, y: 0f, z: 1f), Speed: 3m, DurationSeconds: 0.01m),
                    new ActionEffect.DesignateBody(Key: "0", Register: "focus", Kind: WorldBodyDesignationKind.Body, TargetKey: "1"),
                ]
            )]
        ) with {
            TargetRegistersRaw = [new WorldTargetRegister(Name: "focus", MaximumRange: 100f, MaximumHalfAngleDegrees: 180f, RequiresLineOfSight: false)],
        };

        using var fixture = Fixtures.FreshServer(definition: definition);

        Assert.True(fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: WorldPrincipal.Seat(slot: 0), Slot: 0, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);
        Assert.True(fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: WorldPrincipal.Seat(slot: 1), Slot: 1, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);
        fixture.Step();

        var body = Assert.IsType<WorldBody>(@object: fixture.Server.Body(index: 0));
        var state = body.CaptureTransferState();

        Assert.Equal(expected: FixedQ4816.FromDouble(value: 2.5), actual: state.VerticalVelocity);
        Assert.Equal(expected: FixedQ4816.FromInteger(value: 3), actual: state.OverlayVelocity.Z);
        Assert.Equal(expected: 504UL, actual: state.OverlayRemainingTicks);
        Assert.Equal(expected: -1, actual: body.CaptureIntegrationResidue().AffectingSubject);
        Assert.Equal(expected: 1, actual: fixture.Server.Population.CaptureDesignations(slot: 0).Single().Index);
    }

    [Fact]
    public void PaintFieldWritesClippedSphereAndClampsEveryCell() {
        var fields = new WorldFieldsSection(
            Lattice: new WorldFieldLatticeDefinition(
                Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f),
                CellSize: 1f,
                Width: 3,
                Depth: 3,
                Layers: 1,
                StepEveryTicks: 1
            ),
            Fields: [new WorldFieldRow(Name: "heat", Min: 0f, Max: 10f)]
        );
        var definition = Fixtures.WithLattice(definition: Fixtures.BuildDocument(), composite: fields) with {
            Rules = [new WorldRule(
                Name: Name("paint"),
                Mode: ActionTriggerMode.Edge,
                Effects: [new ActionEffect.PaintField(Field: "heat", X: 1, Y: 0, Z: 1, Value: 12m, Radius: 1)]
            )],
        };

        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();

        var lattice = Assert.IsType<WorldFieldLattice>(@object: fixture.Server.Population.Fields);
        var painted = 0;
        for (var cell = 0; cell < lattice.CellCount; cell++) {
            if (lattice.Value(field: 0, cell: cell) != FixedQ4816.Zero) {
                painted++;
                Assert.Equal(expected: FixedQ4816.FromInteger(value: 10), actual: lattice.Value(field: 0, cell: cell));
            }
        }

        Assert.Equal(expected: 5, actual: painted);
    }

    [Fact]
    public void NoOpFieldPaintIsASuccessNotAnUnavailableFieldRefusal() {
        var fields = new WorldFieldsSection(
            Lattice: new WorldFieldLatticeDefinition(
                Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f),
                CellSize: 1f,
                Width: 1,
                Depth: 1,
                Layers: 1,
                StepEveryTicks: 1
            ),
            Fields: [new WorldFieldRow(Name: "heat", Min: 0f, Max: 10f)]
        );
        var definition = Fixtures.WithLattice(definition: Fixtures.BuildDocument(), composite: fields) with {
            Rules = [new WorldRule(
                Name: Name("no-op-paint"),
                Effects: [new ActionEffect.PaintField(Field: "heat", X: 0, Y: 0, Z: 0, Value: 0m, Radius: 0)]
            )],
        };
        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();

        Assert.Empty(collection: fixture.Server.RuleRuntimeDiagnostics());
    }

    private static WorldDefinition Document(IReadOnlyList<WorldStateRow> state, IReadOnlyList<WorldRule> rules) => Fixtures.BuildDocument() with {
        StateRaw = new WorldStateSection(World: state),
        Rules = rules,
    };
    private static WorldStateRow Slot(string name, long value) => new(Name: Name(name), Kind: CellKind.Int, Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: value)]);
    private static WorldStateRow FixedSlot(string name, long value) => new(Name: Name(name), Kind: CellKind.Fixed, Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: value)]);
    private static WorldStateRow Keyed(string name, int capacity, IReadOnlyList<WorldStateCell> cells) => new(Name: Name(name), Kind: CellKind.Int, Capacity: capacity, Cells: cells);
    private static WorldStateCell Cell(string key, long value) => new(Key: Name(key), Value: value);
    private static WorldCellName Name(string value) => WorldCellName.Parse(candidate: value);
    private static long Value(WorldFixture fixture, string row, string? key = null) {
        var declared = WorldDefinitionRows.FindStateRow(rows: fixture.Server.Definition.State, name: row)!;
        var cellName = ((key is null) ? WorldStateRow.SlotKey : Name(key));

        return WorldDefinitionRows.FindCell(cells: declared.Cells, key: cellName)!.Value;
    }
}
