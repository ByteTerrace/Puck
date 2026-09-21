using Puck.Assets.Documents;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>CONTRACT UNDER TEST: a job's judge is the rules that can reach a row the job reads, and no others. A rule
/// writing the job's verdict stays, a rule feeding that rule's reads stays with it, and a rule whose writes reach
/// nothing the job names is dropped, so a second game in the same world adds nothing to what one candidate
/// costs.</summary>
public sealed class WorldSearchJudgeScopeLawTests {
    private static WorldStateRow Slot(string name) => new(
        CellName.Parse(candidate: name),
        CellKind.Int,
        Cells: [new StateCell(
                WorldStateRow.SlotKey,
                CellValue.Int(value: 0L)
            )]
    );
    private static WorldStateRow Keyed(string name, string key = "piece") => new(
        CellName.Parse(candidate: name),
        CellKind.Int,
        Domain: StateDomain.Keys.Instance,
        Cells: [new StateCell(
                CellName.Parse(candidate: key),
                CellValue.Int(value: 0L)
            )]
    );
    private static WorldStateRow Derived(string name, string tokens, string codes) => new(
        CellName.Parse(candidate: name),
        CellKind.Int,
        Domain: new StateDomain.CellsOf(Topology: "grid"),
        Inverse: new StateInverse(
            Tokens: CellName.Parse(candidate: tokens),
            Codes: CellName.Parse(candidate: codes)
        )
    );
    private static WorldRule Copy(string name, string from, string to) => new(
        CellName.Parse(candidate: name),
        [new ActionEffect.SetState(
                FromState: from,
                State: to
            )]
    );
    private static WorldRule CopyCell(string name, string from, string fromKey, string to, string? key = null) => new(
        CellName.Parse(candidate: name),
        [new ActionEffect.SetState(
                FromKey: fromKey,
                FromState: from,
                Key: StateChannelRef.OfNullable(spelling: key),
                State: to
            )]
    );
    private static string[] Scoped(WorldSearchRow job) {
        var definition = new WorldDefinition(
            Simulation: new WorldSimulationDefaults(RateHz: 240),
            StateRaw: new WorldStateSection(World: [
                Slot(name: "pieces"),
                Slot(name: "turn"),
                Slot(name: "verdict"),
                Slot(name: "threat"),
                Slot(name: "material"),
                Slot(name: "otherGame"),
                Slot(name: "otherScore"),
            ]),
            Rules: [
                Copy(
                    name: "threat-from-pieces",
                    from: "pieces",
                    to: "threat"
                ),
                Copy(
                    name: "verdict-from-threat",
                    from: "threat",
                    to: "verdict"
                ),
                Copy(
                    name: "material-from-pieces",
                    from: "pieces",
                    to: "material"
                ),
                Copy(
                    name: "other-game",
                    from: "otherGame",
                    to: "otherScore"
                ),
            ]
        );
        var judge = WorldSearchCompilation.JudgeRules(rules: WorldFactsCompiler.CompileAll(definition: definition));

        return [.. WorldSearchCompilation.JudgeRules(
            catalog: definition.StateCatalog,
            context: WorldFactsCompiler.Context(definition: definition),
            judge: judge,
            row: job
        ).Select(selector: static rule => rule.Name)];
    }

    [Fact]
    public void AJudgeKeepsTheRulesThatReachItsVerdictInAuthoredOrder() => Assert.Equal(
        actual: Scoped(job: new WorldSearchRow(
            Name: "moves",
            Tokens: "pieces",
            Turn: "turn",
            Verdict: "verdict"
        )),
        expected: ["threat-from-pieces", "verdict-from-threat"]
    );
    [Fact]
    public void ARowTheScoreReadsBringsTheRuleThatWritesIt() => Assert.Equal(
        actual: Scoped(job: new WorldSearchRow(
            Name: "moves",
            Score: "material",
            Tokens: "pieces",
            Turn: "turn",
            Verdict: "verdict"
        )),
        expected: ["threat-from-pieces", "verdict-from-threat", "material-from-pieces"]
    );

    [Fact]
    public void AReachedDerivedBoardKeepsItsInverseProducersTransitively() {
        var definition = new WorldDefinition(
            Simulation: new WorldSimulationDefaults(RateHz: 240),
            StateRaw: new WorldStateSection(Lattices: [new LatticeTopology.Grid(
                CellSize: 1f,
                Depth: 1,
                Name: "grid",
                Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f),
                Width: 1
            )], World: [
                Keyed(name: "pieces"),
                Keyed(name: "codes"),
                Derived(name: "mask", tokens: "pieces", codes: "codes") with {
                    Cells = [new StateCell(
                        Key: CellName.Parse(candidate: "0"),
                        Value: CellValue.Int(value: 0L)
                    )]
                },
                Slot(name: "turn"),
                Slot(name: "verdict"),
                Slot(name: "unrelated"),
                Slot(name: "unrelatedResult"),
            ]),
            Rules: [
                CopyCell(name: "codes-from-pieces", from: "pieces", fromKey: "piece", to: "codes", key: "piece"),
                CopyCell(name: "verdict-from-mask", from: "mask", fromKey: "0", to: "verdict"),
                Copy(name: "unrelated-rule", from: "unrelated", to: "unrelatedResult"),
            ]
        );
        var judge = WorldSearchCompilation.JudgeRules(rules: WorldFactsCompiler.CompileAll(definition: definition));

        var scoped = WorldSearchCompilation.JudgeRules(
            catalog: definition.StateCatalog,
            context: WorldFactsCompiler.Context(definition: definition),
            judge: judge,
            row: new WorldSearchRow(
                Name: "moves",
                Tokens: "pieces",
                Turn: "turn",
                Verdict: "verdict"
            )
        );

        Assert.Equal(
            ["codes-from-pieces", "verdict-from-mask"],
            scoped.Select(static rule => rule.Name).ToArray()
        );
    }
}
