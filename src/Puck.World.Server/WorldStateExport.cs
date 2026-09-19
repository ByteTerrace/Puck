using System.Globalization;
using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;

namespace Puck.World.Server;

/// <summary>The canonical JSON rendering of a live server's state substrate — the same lanes
/// <see cref="WorldStateHashComposition"/> folds, written out as readable, diffable values instead of a digest.</summary>
/// <remarks>
/// <para>The document carries five members. <c>state</c> is the live <see cref="WorldDefinition.StateRaw"/> section
/// serialized through <see cref="WorldDefinitionSerialization"/>, so every stored row and cell field the hash walks
/// — kind, envelope, capacity, overflow, <c>gatesDrive</c>, <c>evicts</c>, domain, <c>valuesFrom</c>,
/// <c>phaseOf</c>, inverse, knowledge, visibility, the advance/draw/dynamics/field/cycle traits, the draw and
/// history cursors, the phase sequence, the drawn masks, and each cell's key, value, observation, text, provenance,
/// behavior, clock and vector — is present verbatim, in declaration order, with no second walk to keep in step.
/// The discrete and physical topologies the hash normalizes live in the same section as <c>state.lattices</c>.
/// <c>resolved</c> carries the value and text each cell RESOLVES to at the export's tick, which the hash folds
/// beside the stored value. <c>fields</c> carries every physical field lattice cell, field-major then cell-major —
/// the same order <see cref="Puck.Physics.Fields.FieldLattice.AppendStateHash"/> folds and
/// <see cref="Puck.Physics.Fields.FieldLattice.Capture"/> captures.</para>
/// <para><c>hashes</c> pins the four <see cref="WorldStateHashScope"/> digests. Two lanes the authoritative scope
/// folds have no public walk to render as values — every body's pose and rigid residue
/// (<see cref="WorldReplaySnapshot.HashState"/>), and the server's private runtime feature lanes (decision, rule
/// and interaction gate latches, board enforcement, per-body action state, cached navigation and flock perception,
/// and search). The <c>pose</c> and <c>authoritative</c> digests are what this document carries for them.</para>
/// </remarks>
public static class WorldStateExport {
    private static JsonArray? Fields(Puck.Physics.Fields.FieldLattice? lattice) {
        if (lattice is null) {
            return null;
        }

        var captured = lattice.Capture();
        var fields = new JsonArray();

        for (var field = 0; (field < lattice.FieldCount); field++) {
            var cells = new JsonArray();
            var raw = captured.Raw[field];

            for (var cell = 0; (cell < raw.Length); cell++) {
                cells.Add(value: raw[cell]);
            }

            fields.Add(value: new JsonObject {
                ["name"] = lattice.Input.Fields[field].Name,
                ["cells"] = cells,
            });
        }

        return fields;
    }
    private static JsonObject Hashes(WorldServer server, ulong tick) {
        var hashes = new JsonObject();

        foreach (var scope in new[] { WorldStateHashScope.Authoritative, WorldStateHashScope.Capture, WorldStateHashScope.Pose, WorldStateHashScope.World }) {
            hashes[propertyName: scope.ToString().ToLowerInvariant()] = WorldStateHashComposition.Hash(
                scope: scope,
                server: server,
                tick: tick
            ).ToString(
                format: "x16",
                provider: CultureInfo.InvariantCulture
            );
        }

        return hashes;
    }
    private static JsonArray Resolved(WorldDefinition definition, ulong tick, ulong engineTick) {
        var catalog = definition.StateCatalog;
        var rows = definition.State;
        var resolved = new JsonArray();

        for (var rowIndex = 0; (rowIndex < rows.Count); rowIndex++) {
            var row = rows[rowIndex];

            if (!catalog.TryResolve(
                lane: StateLane.Document,
                name: row.Name,
                handle: out var handle
            )) {
                continue;
            }

            var cells = (row.Cells ?? []);
            var values = new JsonArray();

            for (var cellIndex = 0; (cellIndex < cells.Count); cellIndex++) {
                var cell = cells[cellIndex];

                if (!WorldStateReader.TryReadHandle(
                    catalog: catalog,
                    definition: definition,
                    engineTick: engineTick,
                    handle: handle,
                    key: cell.Key,
                    rawValue: out var rawValue,
                    row: out _,
                    text: out var text,
                    tick: tick
                )) {
                    continue;
                }

                values.Add(value: new JsonObject {
                    ["key"] = cell.Key.Value,
                    ["value"] = rawValue,
                    ["text"] = text,
                });
            }

            resolved.Add(value: new JsonObject {
                ["row"] = row.Name.Value,
                ["cells"] = values,
            });
        }

        return resolved;
    }

    /// <summary>Renders <paramref name="server"/>'s live state substrate as canonical UTF-8 JSON (no BOM, LF
    /// newlines, two-space indentation, one trailing newline).</summary>
    /// <param name="server">The live server to export.</param>
    /// <returns>The canonical UTF-8 byte form of the export document.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> is <see langword="null"/>.</exception>
    public static byte[] ToCanonicalJson(WorldServer server) {
        ArgumentNullException.ThrowIfNull(argument: server);

        var definition = server.Definition;
        var tick = (server.NextInputTick - 1UL);
        // The document's own serializer is the one walk over the row shape; lifting its `state` member keeps this
        // export from becoming a second, drift-prone description of the same rows.
        var document = JsonNode.Parse(utf8Json: WorldDefinitionSerialization.Serialize(definition: definition))!.AsObject();
        var export = new JsonObject {
            ["schema"] = "puck.world.state-export.v1",
            ["tick"] = tick,
            ["engineTick"] = server.CompletedEngineTicks,
            ["hashes"] = Hashes(
                server: server,
                tick: tick
            ),
            ["state"] = document[propertyName: "state"]?.DeepClone(),
            ["resolved"] = Resolved(
                definition: definition,
                engineTick: server.CompletedEngineTicks,
                tick: tick
            ),
            ["fields"] = Fields(lattice: server.Population.Fields),
        };

        return CanonicalJsonDocument.Serialize(node: export);
    }
}
