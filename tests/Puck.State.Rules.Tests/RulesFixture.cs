using Puck.Assets.Documents;
using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>The one section every compiler law suite here compiles against: a bounded numeric slot, a token domain
/// and its codes feeding a derived board, three ordered piles over that domain, a keyed attribute table, a ring, a
/// text slot, a fixed slot, a host-owned lattice, a draw site, a phase row, a countdown, and a two-member family.</summary>
public static class RulesFixture {
    /// <summary>The name of the host-owned lattice row.</summary>
    public const string HostField = "field";

    /// <summary>Returns a validated cell name.</summary>
    /// <param name="value">The name's text.</param>
    /// <returns>The name.</returns>
    public static CellName Name(string value) => CellName.Parse(candidate: value);
    /// <summary>Builds the fixture's section.</summary>
    /// <returns>The section.</returns>
    public static StateSection Section() => new(
        Families: [new StateFamily(
                Name: Name(value: "slot"),
                Size: 2
            )],
        Lattices: [new LatticeTopology.Grid(
                Name: "map",
                Origin: new DocumentVector3(
                    x: 0f,
                    y: 0f,
                    z: 0f
                ),
                CellSize: 1f,
                Width: 2,
                Depth: 2
            )],
        Rows: [
            new StateRow(
                Name: Name(value: "score"),
                Kind: CellKind.Int,
                Max: 100L,
                Min: 0L,
                Cells: [new StateCell(
                        Key: StateRow.SlotKey,
                        Value: CellValue.Int(value: 5L)
                    )]
            ),
            new StateRow(
                Name: Name(value: "tokens"),
                Kind: CellKind.Int,
                Capacity: 4,
                Cells: [
                    new StateCell(
                        Key: Name(value: "a"),
                        Value: CellValue.Int(value: 0L)
                    ),
                    new StateCell(
                        Key: Name(value: "b"),
                        Value: CellValue.Int(value: 1L)
                    ),
                ]
            ),
            new StateRow(
                Name: Name(value: "codes"),
                Kind: CellKind.Int,
                Capacity: 4,
                Domain: new StateDomain.KeysOf(Row: Name(value: "tokens")),
                Cells: [
                    new StateCell(
                        Key: Name(value: "a"),
                        Value: CellValue.Int(value: 1L)
                    ),
                    new StateCell(
                        Key: Name(value: "b"),
                        Value: CellValue.Int(value: 1L)
                    ),
                ]
            ),
            new StateRow(
                Name: Name(value: "board"),
                Kind: CellKind.Int,
                Domain: new StateDomain.CellsOf(
                    Empty: -1L,
                    Topology: "map"
                )
            ),
            new StateRow(
                Name: Name(value: "deck"),
                Kind: CellKind.Int,
                Capacity: 4,
                Domain: new StateDomain.KeysOf(
                    Ordered: true,
                    Row: Name(value: "tokens")
                ),
                Cells: [new StateCell(
                        Key: Name(value: "a"),
                        Value: CellValue.Int(value: 11L)
                    )]
            ),
            new StateRow(
                Name: Name(value: "hand"),
                Kind: CellKind.Int,
                Capacity: 4,
                Domain: new StateDomain.KeysOf(
                    Ordered: true,
                    Row: Name(value: "tokens")
                )
            ),
            new StateRow(
                Name: Name(value: "pile"),
                Kind: CellKind.Int,
                Capacity: 4,
                Domain: new StateDomain.KeysOf(
                    Ordered: true,
                    Row: Name(value: "tokens")
                )
            ),
            new StateRow(
                Name: Name(value: "history"),
                Kind: CellKind.Int,
                Domain: new StateDomain.Ring(
                    Capacity: 3,
                    Empty: -1L
                )
            ),
            new StateRow(
                Name: Name(value: "label"),
                Kind: CellKind.Text,
                Cells: [new StateCell(
                        Key: StateRow.SlotKey,
                        Value: CellValue.Text(value: "hi")
                    )]
            ),
            new StateRow(
                Name: Name(value: "ratio"),
                Kind: CellKind.Fixed,
                Cells: [new StateCell(
                        Key: StateRow.SlotKey,
                        Value: CellValue.Fixed(rawBits: 0L)
                    )]
            ),
            new StateRow(
                Name: Name(value: HostField),
                Kind: CellKind.Fixed,
                Domain: new StateDomain.CellsOf(Topology: "map")
            ) {
                HostOwned = true,
            },
            new StateRow(
                Name: Name(value: "deal"),
                Kind: CellKind.Int,
                Draw: new Draw(
                    Generator: Stream(),
                    Timing: DrawTiming.Event
                )
            ),
            new StateRow(
                Name: Name(value: "boot"),
                Kind: CellKind.Int,
                Draw: new Draw(
                    Generator: Stream(),
                    Timing: DrawTiming.Boot
                )
            ),
            new StateRow(
                Name: Name(value: "turn"),
                Kind: CellKind.Int,
                Capacity: 1,
                Phase: new StatePhase(Sequence: 0L)
            ),
            new StateRow(
                Name: Name(value: "cooldown"),
                Kind: CellKind.Int,
                Min: 0L,
                Cells: [new StateCell(
                        Key: StateRow.SlotKey,
                        Value: CellValue.Int(value: 0L)
                    )]
            ),
            new StateRow(
                Name: Name(value: "slot0"),
                Kind: CellKind.Int,
                Capacity: 2,
                Cells: [new StateCell(
                        Key: StateRow.SlotKey,
                        Value: CellValue.Int(value: 1L)
                    )]
            ),
            new StateRow(
                Name: Name(value: "slot1"),
                Kind: CellKind.Int,
                Capacity: 2,
                Cells: [new StateCell(
                        Key: StateRow.SlotKey,
                        Value: CellValue.Int(value: 2L)
                    )]
            ),
            new StateRow(
                Name: Name(value: "node"),
                Kind: CellKind.Int,
                Cells: [new StateCell(
                        Key: StateRow.SlotKey,
                        Value: CellValue.Int(value: 3L)
                    )]
            ),
        ]
    );
    /// <summary>Builds the fixture's pattern rows, which a section does not carry.</summary>
    /// <returns>The pattern rows.</returns>
    public static PatternRow[] Patterns() => [new PatternRow(
            Name: Name(value: "run"),
            Kind: CellKind.Int,
            Symbols: [new PatternSymbol(
                Name: Name(value: "one"),
                Min: 1m,
                Max: 1m
            )],
            Pattern: new PatternNode.Plus(Item: new PatternNode.Symbol(Name: "one")),
            Attribute: "codes"
        )];
    /// <summary>An exhausting numeric draw, so a site over it may be redrawn.</summary>
    /// <returns>The generator.</returns>
    public static StateGenerator Stream() => new(
        Bound: 4,
        Mode: GeneratorMode.WithReplacement,
        RangeMax: 3L,
        RangeMin: 0L,
        Source: GeneratorSource.StreamDraw
    );
    /// <summary>Builds a compile context over the fixture's section.</summary>
    /// <returns>The context.</returns>
    public static RuleCompileContext Context() {
        var section = Section();

        return new RuleCompileContext(
            catalog: StateCatalog.Compile(section: section),
            generators: null,
            patterns: Patterns(),
            section: section,
            simulationRateHz: 30,
            tables: null,
            vocabulary: RuleVocabulary.Core
        );
    }
    /// <summary>Compiles one rule against a fresh context.</summary>
    /// <param name="rule">The authored rule.</param>
    /// <returns>The compiled rule.</returns>
    public static CompiledRule Compile(Rule rule) => RuleCompiler.Compile(
        context: Context(),
        rule: rule
    );
    /// <summary>Compiles one rule and returns the refusal it raises.</summary>
    /// <param name="rule">The authored rule.</param>
    /// <returns>The refusal category.</returns>
    public static Enum Refusal(Rule rule) {
        var exception = Assert.Throws<RuleException>(testCode: () => Compile(rule: rule));

        return exception.Refusal;
    }
    /// <summary>Builds a rule with one gate and one effect.</summary>
    /// <param name="name">The rule's name.</param>
    /// <param name="effects">The authored effects.</param>
    /// <param name="gate">The authored gate.</param>
    /// <param name="forEach">The iterated row, or <see langword="null"/>.</param>
    /// <param name="zones">The zone table, or <see langword="null"/>.</param>
    /// <returns>The rule.</returns>
    public static Rule Rule(string name, IReadOnlyList<ActionEffect>? effects = null, ActionPredicate? gate = null, string? forEach = null, IReadOnlyList<string>? zones = null) => new(
        Name: Name(value: name),
        Effects: (effects ?? [new ActionEffect.SetState(
                State: "score",
                Value: 1m
            )]),
        ForEach: forEach,
        Gate: gate,
        Zones: zones
    );
    /// <summary>Parses an infix expression into the authored program every law spells its arithmetic with.</summary>
    /// <param name="text">The infix spelling.</param>
    /// <returns>The program.</returns>
    public static ExpressionProgram Program(string text) {
        Assert.True(
            condition: ExpressionSpelling.TryParse(
                error: out var error,
                program: out var program,
                text: text
            ),
            userMessage: error
        );

        return program!;
    }
}
