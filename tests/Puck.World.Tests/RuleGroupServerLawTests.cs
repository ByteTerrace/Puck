using Xunit;

using RuleGroupDeclaration = Puck.State.Rules.RuleGroupDeclaration;
using RuleGroupShape = Puck.State.Rules.RuleGroupShape;
using RuleGroupStep = Puck.State.Rules.RuleGroupStep;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: the server compiles the document's <c>ruleGroups</c> member and runs it. A triggerless fixpoint group
/// that never settles reports one counted ceiling breach per ceiling's worth of ticks and re-arms, because nothing
/// can lift a latched breach on a group no trigger arms; and a group's members are evaluated only through their
/// group, never a second time as ordinary rules.
/// </summary>
public sealed class RuleGroupServerLawTests {
    private const int Ceiling = 2;
    private const string ScoreRow = "score";
    private const int Ticks = 6;

    private static long ScoreCell(WorldFixture fixture) => fixture.Row(name: ScoreRow).Cells!.Single().Value.Raw;
    private static WorldStateRow Score(long max) => new(
        Name: CellName.Parse(candidate: ScoreRow),
        Kind: CellKind.Int,
        Min: 0L,
        Max: max,
        Cells: [new StateCell(
                Key: WorldStateRow.SlotKey,
                Value: CellValue.Int(value: 0L)
            )]
    );
    // One member that inverts the score every pass, so every pass leaves the group's write set different from where
    // it began and the fixpoint never closes.
    private static WorldDefinition OscillatingDocument() {
        var document = Fixtures.BuildDocument();

        return document with {
            StateRaw = new WorldStateSection(World: [Score(max: 1L)]),
            Rules = [new WorldRule(
                Name: CellName.Parse(candidate: "toggle"),
                Effects: [new ActionEffect.SetState(
                        Expression: ExpressionProgram.Parse(text: $"1 - {ScoreRow}"),
                        State: ScoreRow
                    )]
            )],
            RuleGroupsRaw = [new RuleGroupDeclaration(
                Name: CellName.Parse(candidate: "flip"),
                Shape: RuleGroupShape.Fixpoint,
                Steps: [new RuleGroupStep(Rule: CellName.Parse(candidate: "toggle"))],
                Passes: Ceiling
            )],
        };
    }
    // One member that counts its own evaluations, claimed by a group or not: the cell reads the number of times the
    // engine ran it.
    private static WorldDefinition TallyDocument(bool group) {
        var document = Fixtures.BuildDocument();

        return document with {
            StateRaw = new WorldStateSection(World: [Score(max: 1000L)]),
            Rules = [new WorldRule(
                Name: CellName.Parse(candidate: "tally"),
                Effects: [new ActionEffect.AddState(
                        State: ScoreRow,
                        Value: 1m
                    )]
            )],
            RuleGroupsRaw = (group
                ? [new RuleGroupDeclaration(
                    Name: CellName.Parse(candidate: "counting"),
                    Shape: RuleGroupShape.Fixpoint,
                    Steps: [new RuleGroupStep(Rule: CellName.Parse(candidate: "tally"))]
                )]
                : null
            ),
        };
    }

    [Fact]
    public void ATriggerlessFixpointGroupReArmsAfterEveryCeilingBreachOnTheServer() {
        using var fixture = Fixtures.FreshServer(definition: OscillatingDocument());

        for (var tick = 0; (tick < Ticks); tick++) {
            fixture.Step();
        }

        var breaches = fixture.Server.RuleRuntimeDiagnostics().Where(predicate: static diagnostic =>
            diagnostic.Refusal.Equals(obj: RuleEffectRefusal.GroupPassCeiling)).ToArray();

        Assert.Single(collection: breaches);
        Assert.Equal(
            actual: breaches[0].Count,
            expected: ((ulong)(Ticks / Ceiling))
        );
        Assert.Equal(
            actual: breaches[0].Rule,
            expected: "flip"
        );
    }
    // A group naming a rule the section does not declare cannot compile. On this host that is a validation line, so
    // the document does not install at all and no member of the group runs. KEEP IN SYNC with the browser session,
    // which drops the same members from its direct pass.
    [Fact]
    public void ADocumentWhoseGroupRefusesCompilationDoesNotValidate() {
        var document = TallyDocument(group: true);

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: document,
            reason: out var admitted
        ), userMessage: admitted);

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: (document with {
                RuleGroupsRaw = [new RuleGroupDeclaration(
                    Name: CellName.Parse(candidate: "counting"),
                    Shape: RuleGroupShape.Fixpoint,
                    Steps: [
                        new RuleGroupStep(Rule: CellName.Parse(candidate: "tally")),
                        new RuleGroupStep(Rule: CellName.Parse(candidate: "missing")),
                    ]
                )],
            }),
            reason: out var refused
        ));
        Assert.Contains(
            actualString: refused,
            expectedSubstring: "missing"
        );
    }
    [Fact]
    public void TheUngroupedPartitionKeepsAGroupsMemberOutOfTheDirectPass() {
        using var grouped = Fixtures.FreshServer(definition: TallyDocument(group: true));
        using var ungrouped = Fixtures.FreshServer(definition: TallyDocument(group: false));

        for (var tick = 0; (tick < Ticks); tick++) {
            grouped.Step();
            ungrouped.Step();
        }

        Assert.Equal(
            actual: ScoreCell(fixture: ungrouped),
            expected: ((long)Ticks)
        );
        // Claiming the rule into a group moves where it is evaluated, never how often.
        Assert.Equal(
            actual: ScoreCell(fixture: grouped),
            expected: ScoreCell(fixture: ungrouped)
        );
    }
}
