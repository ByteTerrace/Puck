using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>The section every evaluator law compiles and fires against: four plain integer slots, a slot whose
/// envelope refuses every non-zero write, a keyed table, a ring, a boolean slot, and a text slot.</summary>
public static class EvaluatorFixture {
    /// <summary>Builds the section.</summary>
    /// <returns>The section.</returns>
    public static StateSection Section() => new(Rows: [
        Slot(
            name: "score",
            value: 0L
        ),
        Slot(
            name: "other",
            value: 0L
        ),
        Slot(
            name: "flag",
            value: 0L
        ),
        Slot(
            name: "third",
            value: 0L
        ),
        new StateRow(
            Name: RulesFixture.Name(value: "locked"),
            Kind: CellKind.Int,
            Max: 0L,
            Min: 0L,
            Cells: [new StateCell(
                    Key: StateRow.SlotKey,
                    Value: CellValue.Int(value: 0L)
                )]
        ),
        new StateRow(
            Name: RulesFixture.Name(value: "hand"),
            Kind: CellKind.Int,
            Capacity: 8,
            Cells: [
                new StateCell(
                    Key: RulesFixture.Name(value: "a"),
                    Value: CellValue.Int(value: 1L)
                ),
                new StateCell(
                    Key: RulesFixture.Name(value: "b"),
                    Value: CellValue.Int(value: 2L)
                ),
            ]
        ),
        new StateRow(
            Name: RulesFixture.Name(value: "log"),
            Kind: CellKind.Int,
            Domain: new StateDomain.Ring(
                Capacity: 3,
                Empty: -1L
            )
        ),
        new StateRow(
            Name: RulesFixture.Name(value: "ready"),
            Kind: CellKind.Bool,
            Cells: [new StateCell(
                    Key: StateRow.SlotKey,
                    Value: CellValue.Bool(value: false)
                )]
        ),
        new StateRow(
            Name: RulesFixture.Name(value: "label"),
            Kind: CellKind.Text,
            Cells: [new StateCell(
                    Key: StateRow.SlotKey,
                    Value: CellValue.Text(value: "hi")
                )]
        ),
    ]);
    /// <summary>Builds one integer slot row.</summary>
    /// <param name="name">The row's name.</param>
    /// <param name="value">The slot's value.</param>
    /// <returns>The row.</returns>
    public static StateRow Slot(string name, long value) => new(
        Name: RulesFixture.Name(value: name),
        Kind: CellKind.Int,
        Cells: [new StateCell(
                Key: StateRow.SlotKey,
                Value: CellValue.Int(value: value)
            )]
    );
    /// <summary>Builds a compile context over a section.</summary>
    /// <param name="section">The section.</param>
    /// <param name="vocabulary">The vocabulary, or <see langword="null"/> for the library's own.</param>
    /// <returns>The context.</returns>
    public static RuleCompileContext Context(StateSection section, RuleVocabulary? vocabulary = null) => new(
        catalog: StateCatalog.Compile(section: section),
        generators: null,
        patterns: null,
        section: section,
        simulationRateHz: 30,
        tables: null,
        vocabulary: (vocabulary ?? RuleVocabulary.Core)
    );
    /// <summary>Builds an arena-backed host over a section, at tick zero.</summary>
    /// <param name="section">The section.</param>
    /// <param name="context">The context the rules were compiled against, whose catalog the arena shares.</param>
    /// <returns>The host.</returns>
    public static ArenaEffectHost Host(StateSection section, RuleCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);

        return new ArenaEffectHost(arena: new StateArena(
            catalog: context.Catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        ));
    }
    /// <summary>Compiles rules and returns them beside a fresh host and latch.</summary>
    /// <param name="rules">The authored rules.</param>
    /// <param name="section">The section, or <see langword="null"/> for the fixture's own.</param>
    /// <param name="vocabulary">The vocabulary, or <see langword="null"/> for the library's own.</param>
    /// <returns>The host, the evaluator, the compiled rules, the latch and the context.</returns>
    public static (ArenaEffectHost Host, RuleEvaluator Evaluator, CompiledRule[] Rules, RuleLatch Latch, RuleCompileContext Context) Arrange(IReadOnlyList<Rule> rules, StateSection? section = null, RuleVocabulary? vocabulary = null) {
        var built = (section ?? Section());
        var context = Context(
            section: built,
            vocabulary: vocabulary
        );
        var compiled = RuleCompiler.CompileAll(
            context: context,
            rules: rules
        );
        var host = Host(
            context: context,
            section: built
        );

        return (host, new RuleEvaluator(host: host), compiled, new RuleLatch(), context);
    }
    /// <summary>Reads one cell's stored integer.</summary>
    /// <param name="host">The host.</param>
    /// <param name="row">The row's name.</param>
    /// <param name="key">The cell key, or <see langword="null"/> for the slot key.</param>
    /// <returns>The value.</returns>
    public static long Cell(ArenaEffectHost host, string row, string? key = null) {
        ArgumentNullException.ThrowIfNull(argument: host);

        var arena = host.Arena;
        var ordinal = Ordinal(
            host: host,
            row: row
        );

        Assert.True(
            condition: arena.Catalog.Keys.TryResolve(
                key: out var cell,
                name: RulesFixture.Name(value: (key ?? StateRow.SlotKey.Value))
            ),
            userMessage: (key ?? StateRow.SlotKey.Value)
        );
        Assert.True(
            condition: arena.TryRead(
                key: cell,
                rowOrdinal: ordinal,
                value: out var value
            ),
            userMessage: row
        );

        return value.AsInt;
    }
    /// <summary>Returns a row's catalog ordinal.</summary>
    /// <param name="host">The host.</param>
    /// <param name="row">The row's name.</param>
    /// <returns>The ordinal.</returns>
    public static int Ordinal(ArenaEffectHost host, string row) {
        ArgumentNullException.ThrowIfNull(argument: host);

        Assert.True(
            condition: host.Arena.Catalog.TryResolve(
                handle: out var handle,
                lane: StateLane.Document,
                name: row
            ),
            userMessage: row
        );

        return handle.Ordinal;
    }
    /// <summary>Builds a <c>setState</c> effect against a row's slot.</summary>
    /// <param name="row">The row's name.</param>
    /// <param name="value">The authored value.</param>
    /// <returns>The effect.</returns>
    public static ActionEffect Set(string row, decimal value) => new ActionEffect.SetState(
        State: row,
        Value: value
    );
    /// <summary>Builds an <c>addState</c> effect against a row's slot.</summary>
    /// <param name="row">The row's name.</param>
    /// <param name="value">The authored addend.</param>
    /// <returns>The effect.</returns>
    public static ActionEffect Add(string row, decimal value) => new ActionEffect.AddState(
        State: row,
        Value: value
    );
    /// <summary>Builds a gate comparing a row's slot against a constant.</summary>
    /// <param name="row">The row's name.</param>
    /// <param name="comparison">The comparison.</param>
    /// <param name="value">The constant.</param>
    /// <returns>The predicate.</returns>
    public static ActionPredicate Compare(string row, ExpressionOp comparison, decimal value) => new ActionPredicate.CompareState(
        Comparison: comparison,
        State: row,
        Value: value
    );
}
