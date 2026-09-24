using Xunit;

using ArenaEffectHost = Puck.State.Rules.ArenaEffectHost;
using CompiledRule = Puck.State.Rules.CompiledRule;
using RuleEvaluator = Puck.State.Rules.RuleEvaluator;
using RuleLatch = Puck.State.Rules.RuleLatch;

namespace Puck.World.Tests;

/// <summary>Compiles a state-only rule program once per test and judges an independent position for each case.
/// Physical sampling, mutation admission, and whole-world integration belong in server fixtures.</summary>
/// <remarks>One judge is one tick over one position, never a continuation of the last, so
/// <see cref="RuleLatch.Reset"/> runs before every one of them: the shipped tabletop judges are <c>Edge</c> rules,
/// which fire on their gate's crossing, and a latch carrying its crossings and its binding memos across positions
/// would judge the first alone. Every position loads into the arena the rules were compiled against, because a
/// compiled operand's row ordinal and interned cell key belong to that catalog.
/// <para>A position loads only the rows whose content the arena may not already hold: a row is skipped when it is
/// the same <see cref="StateRow"/> instance this fixture last loaded at that ordinal and the arena's
/// <see cref="StateArena.RowGeneration"/> has not moved since, which every write, judge, and rewind of the row
/// moves. <see cref="StateArena.TryLoad"/> keeps an omitted row as it stands, so the arena a judge reads is the one a
/// load of every row would have produced.</para></remarks>
internal sealed class RuleArenaFixture {
    private readonly StateArena m_arena;
    private readonly RuleEvaluator m_evaluator;
    private readonly ArenaEffectHost m_host;

    private readonly RuleLatch m_latch = new();

    private readonly ulong[] m_loadedGenerations;
    private readonly StateRow?[] m_loadedRows;

    private readonly List<(int Ordinal, StateRow Row)> m_pending = [];

    private readonly CompiledRule[] m_rules;

    private IReadOnlyList<StateRow> m_rows;

    public RuleArenaFixture(WorldDefinition definition) {
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: definition,
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.True(
            condition: StateArena.TryCreate(
                arena: out var arena,
                catalog: definition.StateCatalog,
                options: null,
                reason: out var refusal,
                section: definition.StateRaw,
                time: ArenaTime.Origin
            ),
            userMessage: refusal
        );

        m_arena = arena;
        m_host = new ArenaEffectHost(
            arena: arena,
            documentSeed: (definition.Generation?.WorldSeed ?? 0UL),
            dynamics: definition.Dynamics,
            generators: definition.Generators,
            ticksPerSecond: definition.SimulationRateHz
        );
        m_evaluator = new RuleEvaluator(host: m_host);
        m_loadedGenerations = new ulong[arena.Layout.RowCount];
        m_loadedRows = new StateRow?[arena.Layout.RowCount];
        m_rows = definition.State;
        m_rules = WorldFactsCompiler.CompileAll(definition: definition);
    }

    /// <summary>Gets how many distinct refusals this fixture's judges have drawn.</summary>
    public int Refusals => m_evaluator.Diagnostics().Count;

    /// <summary>Loads a position's authored rows and judges it as one tick.</summary>
    /// <param name="position">The position.</param>
    public void Evaluate(WorldDefinition position) {
        var time = ArenaTime.Origin;
        var rows = new List<StateRow>(capacity: position.State.Count);

        m_pending.Clear();

        foreach (var row in position.State) {
            if (!m_arena.Catalog.TryResolve(
                handle: out var handle,
                lane: StateLane.Document,
                name: row.Name
            )) {
                rows.Add(item: row);

                continue;
            }

            var ordinal = handle.Ordinal;

            if (
                ReferenceEquals(
                objA: m_loadedRows[ordinal],
                objB: row
            ) &&
                (m_arena.RowGeneration(rowOrdinal: ordinal) == m_loadedGenerations[ordinal])
            ) {
                continue;
            }

            rows.Add(item: row);
            m_pending.Add(item: (ordinal, row));
        }

        Assert.True(
            condition: m_arena.TryLoad(
                reason: out var reason,
                rows: rows,
                time: in time
            ),
            userMessage: reason
        );
        Assert.Equal(
            actual: reason,
            expected: string.Empty
        );

        foreach (var (ordinal, row) in m_pending) {
            m_loadedRows[ordinal] = row;
            m_loadedGenerations[ordinal] = m_arena.RowGeneration(rowOrdinal: ordinal);
        }

        m_rows = position.State;

        Judge();
    }
    /// <summary>Judges the arena as it stands.</summary>
    public void Judge() {
        m_host.Advance(
            engineTick: 1UL,
            tick: 1UL
        );
        m_latch.Reset();

        _ = m_evaluator.Evaluate(
            latch: m_latch,
            rules: m_rules,
            stepTicks: 1UL
        );

        Assert.Equal(
            0,
            Refusals
        );
    }
    public long Read(string row, string key = "$value") {
        if (
            m_arena.Catalog.Keys.TryResolve(
            key: out var cell,
            name: CellName.Parse(candidate: key)
        ) &&
            m_arena.TryRead(
            key: cell,
            rowOrdinal: Ordinal(row: row),
            value: out var value
        )
        ) {
            return value.Raw;
        }

        // An absent cell of a board reads as the board's own empty value, the way an install reads it.
        return ((StateRows.FindStateRow(
            name: row,
            rows: m_rows
        )?.EffectiveDomain is StateDomain.CellsOf board)
            ? board.Empty
            : 0L
        );
    }
    /// <summary>Reads the cell at one declaration position of a keyed row.</summary>
    /// <param name="row">The row's name.</param>
    /// <param name="position">The declaration position.</param>
    /// <returns>The stored number, or zero when the position holds no cell.</returns>
    public long ReadAt(string row, int position) => (m_arena.TryReadAt(
        position: position,
        rowOrdinal: Ordinal(row: row),
        value: out var value
    )
        ? value.Raw
        : 0L
    );
    /// <summary>Writes one cell, recomputing every board derived from it.</summary>
    /// <param name="row">The row's name.</param>
    /// <param name="key">The cell key.</param>
    /// <param name="value">The value.</param>
    public void Write(string row, string key, long value) => Assert.True(
        condition: m_arena.TryWrite(
            key: m_arena.Catalog.Keys.Intern(name: CellName.Parse(candidate: key)),
            operand: value,
            reason: out var reason,
            rowOrdinal: Ordinal(row: row),
            write: StateWriteKind.Set
        ),
        userMessage: reason
    );

    private int Ordinal(string row) {
        Assert.True(
            condition: m_arena.Catalog.TryResolve(
                handle: out var handle,
                lane: StateLane.Document,
                name: row
            ),
            userMessage: row
        );

        return handle.Ordinal;
    }
}
