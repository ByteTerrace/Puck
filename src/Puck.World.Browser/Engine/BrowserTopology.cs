using System.Numerics;
using System.Text.Json;

namespace Puck.World.Browser.Engine;

/// <summary>One compiled topology cell's ordinal geometry — the shape <c>Cells</c> exports, so a studio viewport
/// never re-derives hex/box/ring ordinal order from scratch (see <see cref="CompiledTopology"/>'s own remarks on
/// direction/coordinate conventions per <see cref="TopologyKind"/>).</summary>
/// <param name="Ordinal">The cell's ordinal — the same integer its authored <c>cells[].key</c> string spells.</param>
/// <param name="Key">The cell's key string (<see cref="CompiledTopology.Key"/>).</param>
/// <param name="X">The cell centre's world-space X.</param>
/// <param name="Y">The cell centre's world-space Y.</param>
/// <param name="Z">The cell centre's world-space Z.</param>
public readonly record struct BrowserCellGeometry(int Ordinal, string Key, float X, float Y, float Z);

/// <summary>Compiles a standalone <c>LatticeTopology</c> JSON object into its per-cell world-space geometry — no
/// document, no session: a topology is self-contained once authored (<c>origin</c>/<c>cellSize</c>/its own shape
/// fields), so this needs no installed <see cref="BrowserSession"/> to answer.</summary>
public static class BrowserTopology {
    /// <summary>Compiles <paramref name="topologyJson"/>'s cell geometry.</summary>
    /// <param name="topologyJson">One <c>LatticeTopology</c> JSON object, the same shape a <c>state.world.lattices[]</c> row authors.</param>
    /// <param name="cells">Each cell's ordinal geometry, ordinal order, on success.</param>
    /// <param name="reason">Why the topology could not be parsed or compiled, or empty on success.</param>
    /// <returns><see langword="true"/> when the topology parsed, validated, and compiled.</returns>
    public static bool TryCells(string topologyJson, out IReadOnlyList<BrowserCellGeometry> cells, out string reason) {
        cells = [];

        LatticeTopology? topology;

        try {
            topology = JsonSerializer.Deserialize(json: topologyJson, jsonTypeInfo: WorldJsonContext.Default.LatticeTopology);
        } catch (JsonException exception) {
            reason = $"the topology is not valid JSON: {exception.Message}";

            return false;
        }

        if (topology is null) {
            reason = "the topology deserialized to null.";

            return false;
        }

        if (!TopologyCompilation.TryValidate(topology: topology, reason: out reason)) {
            return false;
        }

        var compiled = TopologyCompilation.Compile(topology: topology, anchorOffset: Vector3.Zero);
        var result = new BrowserCellGeometry[compiled.CellCount];

        for (var cell = 0; (cell < compiled.CellCount); cell++) {
            var centre = compiled.CellCentre(cell: cell);

            result[cell] = new BrowserCellGeometry(
                Ordinal: cell,
                Key: compiled.Key(cell: cell),
                X: ((float)((double)centre.X)),
                Y: ((float)((double)centre.Y)),
                Z: ((float)((double)centre.Z))
            );
        }

        cells = result;
        reason = string.Empty;

        return true;
    }
}
