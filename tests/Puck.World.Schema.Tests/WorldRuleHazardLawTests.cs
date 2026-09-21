using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>The hazard read-back names the pairs the rules' document order decides: an earlier read of a later
/// write, and two writes of one cell with a set among them — never a pair whose gates make it impossible on one
/// tick, and never a pair of plain adds.</summary>
public sealed class WorldRuleHazardLawTests {
    [Fact]
    public void SeparateAnalysisRequestsShareTheCompilationAndRespectRuleEditsWithTheSameCatalog() {
        var definition = Document(
            Rule("faint", new ActionEffect.SetState(State: "fainted", Value: 1m), HpAtMost(value: 0)),
            Rule("damage", new ActionEffect.AddState(State: "hp", Value: -3m))
        );
        var compilation = WorldRuleCompilation.Compile(definition: definition);
        var hazards = WorldRuleHazards.Analyze(compilation: compilation);
        Assert.Single(collection: hazards);
        Assert.Equal(WorldRuleHazards.Analyze(definition: definition), hazards);
        Parallel.For(0, 16, _ => Assert.Same(hazards, WorldRuleHazards.Analyze(compilation: compilation)));
        Assert.Equal(WorldRuleWorkBudget.Measure(definition: definition), WorldRuleWorkBudget.Measure(compilation: compilation));
        Assert.Equal(compilation.WorkBudget, compilation.CostReport.WorkBudget);
        Assert.Same(compilation.WorkContributors, WorldRuleWorkBudget.Contributors(compilation: compilation));
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; (index < 1000); index++) {
            _ = WorldRuleHazards.Analyze(compilation: compilation);
            _ = WorldRuleWorkBudget.Measure(compilation: compilation);
            _ = WorldRuleWorkBudget.Contributors(compilation: compilation);
            _ = compilation.CostReport;
        }
        var allocated = (GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(actual: allocated, expected: 0L);

        Assert.NotNull(@object: definition.Rules);
        var reordered = definition with { Rules = [definition.Rules[1], definition.Rules[0]] };
        Assert.Same(definition.StateCatalog, reordered.StateCatalog);
        var replacement = WorldRuleCompilation.Compile(definition: reordered);
        Assert.Empty(collection: replacement.Hazards);
        Assert.Single(collection: compilation.Hazards);
    }

    private static WorldDefinition Document(params WorldRule[] rules) => new(
        Simulation: new WorldSimulationDefaults(RateHz: 240),
        StateRaw: new WorldStateSection(World: [Slot(name: "phase"), Slot(name: "hp"), Slot(name: "fainted"), Slot(name: "armor")]),
        Rules: rules
    );
    private static ActionPredicate HpAtMost(long value) =>
        new ActionPredicate.CompareState(
            State: "hp",
            Comparison: ActionStateComparison.LessOrEqual,
            Value: value
        );
    private static ActionPredicate PhaseIs(long value) =>
        new ActionPredicate.CompareState(
            State: "phase",
            Comparison: ActionStateComparison.Equal,
            Value: value
        );
    private static WorldRule Rule(string name, ActionEffect effect, ActionPredicate? gate = null) =>
        new(
            CellName.Parse(candidate: name),
            [effect],
            Gate: gate
        );
    private static WorldStateRow Slot(string name) =>
        new(
            CellName.Parse(candidate: name),
            CellKind.Int,
            Cells: [new StateCell(
                    WorldStateRow.SlotKey,
                    CellValue.Int(value: 5L)
                )]
        );

    [Fact]
    public void APairThatCannotFireOnOneTickIsNotAHazard() {
        Assert.Empty(collection: WorldRuleHazards.Analyze(definition: Document(
            Rule(
                "faint",
                new ActionEffect.SetState(
                    State: "fainted",
                    Value: 1m
                ),
                new ActionPredicate.All(Predicates: [PhaseIs(value: 0), HpAtMost(value: 0)])
            ),
            Rule(
                "damage",
                new ActionEffect.AddState(
                    State: "hp",
                    Value: -3m
                ),
                PhaseIs(value: 1)
            )
        )));
        Assert.Single(collection: WorldRuleHazards.Analyze(definition: Document(
            Rule(
                "faint",
                new ActionEffect.SetState(
                    State: "fainted",
                    Value: 1m
                ),
                new ActionPredicate.All(Predicates: [PhaseIs(value: 0), HpAtMost(value: 0)])
            ),
            Rule(
                "damage",
                new ActionEffect.AddState(
                    State: "hp",
                    Value: -3m
                ),
                PhaseIs(value: 0)
            )
        )));
    }
    [Fact]
    public void AnEarlierReadOfALaterWriteIsAWriteAfterReadHazard() {
        var hazards = WorldRuleHazards.Analyze(definition: Document(
            Rule(
                "faint",
                new ActionEffect.SetState(
                    State: "fainted",
                    Value: 1m
                ),
                HpAtMost(value: 0)
            ),
            Rule(
                "damage",
                new ActionEffect.AddState(
                    State: "hp",
                    Value: -3m
                )
            )
        ));
        var hazard = Assert.Single(collection: hazards);

        Assert.Equal(
            actual: hazard.Kind,
            expected: Puck.State.Rules.RuleHazardKind.WriteAfterRead
        );
        Assert.Equal(
            ("faint", "damage", $"hp.{WorldStateRow.SlotKey}"),
            (hazard.First, hazard.Second, hazard.Cell)
        );
        Assert.Contains(
            "previous tick",
            hazard.Detail,
            StringComparison.Ordinal
        );

        // Declared the other way round, the check sees the new value and nothing is decided silently.
        Assert.Empty(collection: WorldRuleHazards.Analyze(definition: Document(
            Rule(
                "damage",
                new ActionEffect.AddState(
                    State: "hp",
                    Value: -3m
                )
            ),
            Rule(
                "faint",
                new ActionEffect.SetState(
                    State: "fainted",
                    Value: 1m
                ),
                HpAtMost(value: 0)
            )
        )));
    }
    [Fact]
    public void ReadAndWriteSetsFollowIndirectionsAndBranches() {
        var definition = Document(Rule(
            "r",
            new ActionEffect.Transaction(Effects: [
                new ActionEffect.SetState(
                    State: "armor",
                    Value: 1m
                ),
            ]),
            HpAtMost(value: 0)
        ));
        var rule = WorldFactsCompiler.CompileAll(definition: definition)[0];
        var catalog = definition.StateCatalog;
        var slot = catalog.Keys.Intern(name: WorldStateRow.SlotKey);

        int Ordinal(string row) {
            Assert.True(condition: catalog.TryResolve(
                handle: out var handle,
                lane: StateLane.Document,
                name: row
            ));

            return handle.Ordinal;
        }

        Assert.Contains(
            new CellAccess(
                Key: slot,
                RowOrdinal: Ordinal(row: "hp")
            ),
            Puck.State.Rules.RuleDataflow.Reads(rule: rule)
        );
        Assert.Contains(
            new CellAccess(
                IsSet: true,
                Key: slot,
                RowOrdinal: Ordinal(row: "armor")
            ),
            Puck.State.Rules.RuleDataflow.Writes(rule: rule)
        );
    }
    [Fact]
    public void TwoWritesWithASetAmongThemAreAWriteAfterWriteHazardAndTwoAddsAreNot() {
        var sets = WorldRuleHazards.Analyze(definition: Document(
            Rule(
                "poison",
                new ActionEffect.SetState(
                    State: "hp",
                    Value: 1m
                )
            ),
            Rule(
                "regen",
                new ActionEffect.SetState(
                    State: "hp",
                    Value: 9m
                )
            )
        ));
        var hazard = Assert.Single(collection: sets);

        Assert.Equal(
            actual: hazard.Kind,
            expected: Puck.State.Rules.RuleHazardKind.WriteAfterWrite
        );
        Assert.Contains(
            "'regen' wins",
            hazard.Detail,
            StringComparison.Ordinal
        );

        var setAfterAdd = Assert.Single(collection: WorldRuleHazards.Analyze(definition: Document(
            Rule(
                "poison",
                new ActionEffect.AddState(
                    State: "hp",
                    Value: -1m
                )
            ),
            Rule(
                "reset",
                new ActionEffect.SetState(
                    State: "hp",
                    Value: 9m
                )
            )
        )));

        Assert.Contains(
            "the add is discarded",
            setAfterAdd.Detail,
            StringComparison.Ordinal
        );

        Assert.Empty(collection: WorldRuleHazards.Analyze(definition: Document(
            Rule(
                "poison",
                new ActionEffect.AddState(
                    State: "hp",
                    Value: -1m
                )
            ),
            Rule(
                "regen",
                new ActionEffect.AddState(
                    State: "hp",
                    Value: 2m
                )
            )
        )));
    }
    [Fact]
    public void ValidationReturnsProgramsForOnlyTheExactCandidateAndNoResultOnFailure() {
        var definition = Document(Rule(
            "damage",
            new ActionEffect.AddState(
                "hp",
                Value: -3
            )
        ));

        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                compilation: out var compilation,
                definition: definition,
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.NotNull(@object: compilation);
        Assert.Same(
            definition,
            compilation.Definition
        );
        Assert.Equal(
            "damage",
            Assert.Single(collection: compilation.Rules).Name
        );
        Assert.Empty(collection: compilation.Interactions);
        Assert.Empty(collection: compilation.Tables);
        Assert.Equal(WorldRuleWorkBudget.Measure(definition: definition), compilation.WorkBudget);
        Assert.Equal(WorldRuleWorkBudget.Contributors(definition: definition), compilation.WorkContributors);
        Assert.Same(compilation.WorkContributors, compilation.CostReport.Contributors);
        var changed = definition with {
            Rules = [Rule(
                "heal",
                new ActionEffect.AddState(
                    "hp",
                    Value: 4
                )
            )],
        };

        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                compilation: out var next,
                definition: changed,
                reason: out reason
            ),
            userMessage: reason
        );
        Assert.NotSame(
            compilation.Definition,
            next!.Definition
        );
        Assert.Equal(
            "heal",
            Assert.Single(collection: next.Rules).Name
        );
        var invalid = changed with {
            Rules = [Rule(
                "bad",
                new ActionEffect.SetState(
                    "missing",
                    Value: 1
                )
            )],
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            compilation: out var refused,
            definition: invalid,
            reason: out _
        ));
        Assert.Null(@object: refused);
    }
}
