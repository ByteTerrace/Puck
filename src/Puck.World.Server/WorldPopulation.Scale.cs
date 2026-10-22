using System.Globalization;
using Puck.Maths;

namespace Puck.World.Server;

public sealed partial class WorldPopulation {
    // Resyncs every active body's live WorldBody.Scale wholesale from bodies.scaleRow. Every active body first resets to
    // one (so a removed cell, an un-named body, or a freshly admitted slot never inherits a stale or a previous
    // occupant's value), then the row's own cells overwrite the bodies they key by 0-based index.
    private void SyncBodyScale(WorldDefinition definition) {
        for (var index = 0; (index < m_entries.Length); index++) {
            if (m_entries[index].Body is { } resident) {
                resident.SetScale(value: FixedQ4816.One);
            }
        }

        if (definition.Population.ScaleRow is not { } scaleRow) {
            return;
        }

        if (WorldDefinitionRows.FindStateRow(
            rows: definition.State,
            name: scaleRow
        ) is not { } row) {
            return;
        }

        foreach (var cell in (row.Cells ?? [])) {
            if (
                !int.TryParse(
                s: cell.Key,
                style: NumberStyles.Integer,
                provider: CultureInfo.InvariantCulture,
                result: out var bodyIndex
            ) ||
                (bodyIndex < 0) ||
                (bodyIndex >= m_entries.Length) ||
                (m_entries[bodyIndex].Body is not { } body)
            ) {
                continue;
            }

            if (
                WorldStateReader.TryRead(
                definition: definition,
                rowName: row.Name,
                key: cell.Key.Value,
                tick: 0UL,
                engineTick: 0UL,
                row: out _,
                rawValue: out var raw,
                text: out _
            ) &&
                (raw is { } bits)
            ) {
                body.SetScale(value: FixedQ4816.FromRawBits(value: bits));
            }
        }
    }
}
