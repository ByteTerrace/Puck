
namespace Puck.State.Rules.Tests;

/// <summary>A vocabulary whose one registered arm cannot be rewound, so the compiler defers it to commit and refuses
/// a later effect of the same firing that reads what it wrote.</summary>
public static class IrreversibleFixture {
    /// <summary>An authored arm whose firing leaves the arena.</summary>
    public sealed record Stamp : ActionEffect;

    /// <summary>Builds a compile context whose vocabulary registers the irreversible arm.</summary>
    /// <returns>The context.</returns>
    public static RuleCompileContext Context() {
        var section = RulesFixture.Section();

        return new RuleCompileContext(
            catalog: StateCatalog.Compile(section: section),
            generators: null,
            patterns: RulesFixture.Patterns(),
            section: section,
            simulationRateHz: 30,
            tables: null,
            vocabulary: new RuleVocabulary(
                effects: [new StampArm()],
                keys: [],
                operands: [],
                predicates: []
            )
        );
    }

    private sealed class StampArm : EffectFamily {
        public override string Discriminator => "stamp";
        public override Type EffectType => typeof(Stamp);

        public override IRuleEffect Compile(ActionEffect effect, string ruleName, RuleCompileContext context) => new StampEffect(rowOrdinal: RuleCompiler.ResolveRowOrdinal(
            context: context,
            name: "score"
        ));
    }
    private sealed class StampEffect : RuleEffect {
        private readonly int m_rowOrdinal;

        public StampEffect(int rowOrdinal) : base(describe: "stamp") => m_rowOrdinal = rowOrdinal;

        public override EffectNeeds Needs => EffectNeeds.Irreversible;

        public override void CollectWrites(List<CellAccess> into) {
            ArgumentNullException.ThrowIfNull(argument: into);

            into.Add(item: new CellAccess(
                IsSet: true,
                Key: default,
                RowOrdinal: m_rowOrdinal
            ));
        }
        public override RuleWork Cost(IRuleCostContext context) => 1L;
    }
}
/// <summary>A section carrying two vector spaces, so a space mismatch and every vector shape refusal has a source.</summary>
public static class VectorFixture {
    /// <summary>Builds a compile context over the vector section.</summary>
    /// <param name="hidden">The row seat 1 alone may read, or <see langword="null"/> for a section every reader sees.</param>
    /// <returns>The context.</returns>
    public static RuleCompileContext Context(string? hidden = null) {
        var section = Section(hidden: hidden);

        return new RuleCompileContext(
            catalog: StateCatalog.Compile(section: section),
            generators: null,
            patterns: null,
            section: section,
            simulationRateHz: 30,
            tables: null,
            vocabulary: RuleVocabulary.Core
        );
    }
    /// <summary>Builds the vector section.</summary>
    /// <param name="hidden">The row seat 1 alone may read, or <see langword="null"/> for a section every reader sees.</param>
    /// <returns>The section.</returns>
    public static StateSection Section(string? hidden = null) => new(
        Rows: [.. Rows().Select(selector: row => (((hidden is not null) && row.Name.Equals(other: RulesFixture.Name(value: hidden)))
            ? (row with { Visibility = new StateVisibility(Readers: ["seat1"]) })
            : row))],
        Spaces: [
            new StateSpace(
                name: RulesFixture.Name(value: "wide16"),
                model: "model",
                revision: "r1",
                dimensions: 16
            ),
            new StateSpace(
                name: RulesFixture.Name(value: "narrow8"),
                model: "model",
                revision: "r1",
                dimensions: 8
            ),
        ]
    );

    private static StateRow[] Rows() => [
        new StateRow(
            Name: RulesFixture.Name(value: "score"),
            Kind: CellKind.Int,
            Cells: [new StateCell(
                    Key: StateRow.SlotKey,
                    Value: CellValue.Int(value: 0L)
                )]
        ),
        new StateRow(
            Name: RulesFixture.Name(value: "wide"),
            Kind: CellKind.Vector,
            Space: "wide16"
        ),
        new StateRow(
            Name: RulesFixture.Name(value: "narrow"),
            Kind: CellKind.Vector,
            Space: "narrow8"
        ),
        new StateRow(
            Name: RulesFixture.Name(value: "bank"),
            Kind: CellKind.Vector,
            Capacity: 4,
            Space: "wide16"
        ),
        new StateRow(
            Name: RulesFixture.Name(value: "ranks"),
            Kind: CellKind.Int,
            Capacity: 4
        ),
    ];
}
