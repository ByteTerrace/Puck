using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>
/// The <see cref="IWorldStateView"/> a client implements over the definition it was last delivered, until the
/// presentation view replaces the delivered definition. It reads a row by its catalog ordinal, which indexes the
/// document lane of <see cref="WorldDefinition.State"/> directly, and one cell of it through the engine's own
/// <see cref="StateReader"/> computations, so a bound read and a rule read never disagree about a value.
/// </summary>
/// <param name="definition">Returns the definition currently delivered; read on every call.</param>
public sealed class WorldDocumentStateView(Func<WorldDefinition> definition) : IWorldStateView {
    private readonly Func<WorldDefinition> m_definition = (definition ?? throw new ArgumentNullException(paramName: nameof(definition)));

    /// <inheritdoc/>
    public WorldPresentationManifest Manifest => WorldPresentationManifest.Of(definition: m_definition());

    /// <inheritdoc/>
    public bool TryResolveRow(string rowName, out int ordinal) {
        ArgumentNullException.ThrowIfNull(argument: rowName);

        var document = m_definition();

        if (
            document.StateCatalog.TryResolve(
            handle: out var handle,
            lane: StateLane.Document,
            name: rowName
        ) &&
            (handle.Ordinal < document.State.Count)
        ) {
            ordinal = handle.Ordinal;

            return true;
        }

        ordinal = -1;

        return false;
    }
    /// <inheritdoc/>
    public bool TryRead(int ordinal, string? key, bool target, ulong tick, ulong engineTick, out WorldStateSample sample) {
        var document = m_definition();
        var rows = document.State;

        if (((uint)ordinal) >= ((uint)rows.Count)) {
            sample = default;

            return false;
        }

        var row = rows[ordinal];

        if (
            !CellName.TryParse(
            candidate: (key ?? StateRow.SlotKey.Value),
            name: out var cellKey,
            reason: out _
        ) ||
            (StateRows.FindCell(
            cells: row.Cells,
            key: cellKey
        ) is not { } cell)
        ) {
            sample = new WorldStateSample(
                Max: row.Max,
                Min: row.Min,
                Motion: WorldStateMotion.Still,
                Value: default
            );

            return true;
        }

        var behavior = EffectiveBehavior.Resolve(
            cell: cell,
            row: row
        );
        var motion = (behavior switch {
            { Advance.PerSecondNumerator: not 0L } => WorldStateMotion.Advancing,
            { Cycle: not null } => WorldStateMotion.Stepping,
            _ => WorldStateMotion.Still,
        });

        StateReader.ReadCell(
            engineTick: engineTick,
            key: key,
            rawValue: out var raw,
            row: row,
            text: out var text,
            tick: tick
        );

        // The stored truth a .$target read answers never moves between writes; only the eased follower does.
        if (
            !target &&
            (raw is not null) &&
            (behavior.Dynamics is not null) &&
            StateReader.TryEvaluateDynamics(
            cell: cell,
            dynamics: document.Dynamics,
            row: row,
            sample: out var eased,
            tick: tick,
            ticksPerSecond: document.SimulationRateHz,
            trait: out _
        )
        ) {
            var stored = StateReader.DynamicsRowRawToFixed(
                raw: cell.Value.Raw,
                row: row
            );

            if (
                (eased.Value != stored) ||
                (eased.Velocity != FixedQ4816.Zero)
            ) {
                motion = WorldStateMotion.Easing;
            }

            raw = row.ClampToEnvelope(value: StateReader.DynamicsFixedToRowRaw(
                row: row,
                value: eased.Value
            ));
        }

        sample = new WorldStateSample(
            Max: row.Max,
            Min: row.Min,
            Motion: motion,
            Value: (row.Kind switch {
                CellKind.Vector => cell.Value,
                CellKind.Text => ((raw is not null)
                    ? CellValue.Text(value: text)
                    : default),
                _ => ((raw is { } number)
                    ? CellValue.FromNumber(
                        kind: row.Kind,
                        raw: number
                    )
                    : default),
            })
        );

        return true;
    }
}
