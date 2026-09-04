using System.Globalization;
using Puck.Maths;

namespace Puck.World.Server;

public sealed partial class WorldPopulation {
    /// <summary>Resyncs every active body's live <see cref="WorldBody.Scale"/> wholesale from
    /// <c>bodies.scaleRow</c> — called from the same <c>WorldServer.Install</c>/<c>InstallRuntimeStateValue</c>
    /// choke points <c>WorldGrants.SyncState</c> is, never per tick. Every active body first resets to
    /// <see cref="FixedQ4816.One"/> (so a removed cell, an un-named body, or a freshly admitted slot never inherits
    /// a stale or a previous occupant's value), then the row's own cells overwrite the bodies they key by 0-based
    /// index. A world authoring no <see cref="WorldBodiesDefaults.ScaleRow"/> is a no-op past the reset.</summary>
    /// <param name="definition">The live document.</param>
    public void SyncBodyScale(WorldDefinition definition) {
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
