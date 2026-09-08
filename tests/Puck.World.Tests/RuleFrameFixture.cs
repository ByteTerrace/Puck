using Xunit;

namespace Puck.World.Tests;

/// <summary>Compiles a state-only rule program once per test and reloads independent row values for each case.
/// Physical sampling, mutation admission, and whole-world integration belong in server fixtures.</summary>
internal sealed class RuleFrameFixture {
    private readonly FrameHost m_host;
    private readonly CompiledWorldRule[] m_rules;

    public RuleFrameFixture(WorldDefinition definition) {
        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition, out var reason), reason);
        var errors = new List<string>();
        Assert.True(CompiledPatterns.TryCompileAll(definition.Patterns, out var patterns, errors), string.Join("; ", errors));
        var layout = new FrameLayout(definition.State, name => WorldTopologyCompilation.Find(definition, name));
        m_host = new FrameHost(layout, definition.State, definition.StateCatalog, patterns!, []);
        m_rules = WorldRuleCompiler.CompileAll(definition);
    }

    public void Evaluate(WorldDefinition position) {
        // A server materializes inverses at installation; a raw RowStore contains only authored cells.
        var rows = position.State.Select(row => row.Inverse is { } inverse && row.EffectiveDomain is StateDomain.CellsOf board
            ? row with { Cells = DerivedBoards.Compose(position.State, inverse, WorldTopologyCompilation.Find(position, board.Topology)!) }
            : row).ToArray();
        m_host.Rebind(rows);
        m_host.Frame.Load(new RowStore(rows));
        m_host.Judge(m_rules, 1);
        Assert.Equal(0, m_host.Refusals);
    }

    public long Read(string row, string key = "$value") {
        var source = m_host.Frame.Find(row)!;
        return m_host.Frame.TryStored(source, CellName.Parse(key), out var value, out _)
            ? value : source.EffectiveDomain is StateDomain.CellsOf board ? board.Empty : 0;
    }
}
