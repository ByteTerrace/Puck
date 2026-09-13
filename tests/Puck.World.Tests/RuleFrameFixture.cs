using Xunit;

namespace Puck.World.Tests;

/// <summary>Compiles a state-only rule program once per test and reloads independent row values for each case.
/// Physical sampling, mutation admission, and whole-world integration belong in server fixtures.</summary>
internal sealed class RuleFrameFixture {
    private readonly FrameHost m_host;
    private readonly CompiledWorldRule[] m_rules;

    public RuleFrameFixture(WorldDefinition definition) {
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: definition,
                reason: out var reason
            ),
            userMessage: reason
        );
        var errors = new List<string>();

        Assert.True(
            condition: CompiledPatterns.TryCompileAll(
                definition.Patterns,
                out var patterns,
                errors
            ),
            userMessage: string.Join(
                separator: "; ",
                values: errors
            )
        );
        var layout = new FrameLayout(
            rows: definition.State,
            topology: name => WorldTopologyCompilation.Find(
                definition: definition,
                name: name
            )
        );

        m_host = new FrameHost(
            layout,
            definition.State,
            definition.StateCatalog,
            patterns!,
            []
        );
        m_rules = WorldRuleCompiler.CompileAll(definition: definition);
    }

    public void Evaluate(WorldDefinition position) {
        // A server materializes inverses at installation; a raw RowStore contains only authored cells.
        var rows = position.State.Select(selector: row => (((row.Inverse is { } inverse) && (row.EffectiveDomain is StateDomain.CellsOf board))
            ? row with { Cells = DerivedBoards.Compose(
                position.State,
                inverse,
                WorldTopologyCompilation.Find(
                    definition: position,
                    name: board.Topology
                )!
            ) }
            : row)).ToArray();

        m_host.Rebind(rows: rows);
        m_host.Frame.Load(source: new RowStore(rows: rows));
        m_host.Judge(
            rules: m_rules,
            tick: 1
        );
        Assert.Equal(
            0,
            m_host.Refusals
        );
    }
    public long Read(string row, string key = "$value") {
        var source = m_host.Frame.Find(name: row)!;

        return (m_host.Frame.TryStored(
            source,
            CellName.Parse(candidate: key),
            out var value,
            out _
        )
            ? value
            : ((source.EffectiveDomain is StateDomain.CellsOf board)
                ? board.Empty
                : 0
        ));
    }
}
