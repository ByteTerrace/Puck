using System.Numerics;
using Puck.SignedDistance;

namespace Puck.World.Client;

/// <summary>
/// The nets a scene draws: one <see cref="SdfSheetDisplacement"/> per placement carrying a net facet
/// (<see cref="WorldPlacementNet"/>), laid at the rest layout the authority's nets rest at
/// (<see cref="WorldPlacementNet.TrySpecIn"/>), through which the placement's world-space instances are drawn
/// (<see cref="WorldPlacementStamper.EmitStatic"/>), and whose node offsets are published every frame from the client's
/// delivered nodes (<see cref="WorldNetMirror"/>), read at the presentation's interpolation fraction. A net that moves
/// publishes new values and rebuilds no program and keys no mesh.
/// </summary>
/// <remarks>Invariant: a sheet's rest layout is the authority net's, both taken from the one
/// <see cref="WorldPlacementNet.TrySpecIn"/>; a sheet is kept while its placement's facet and frame stand, and made again
/// (which moves the program, since the instances name it) when either moves.</remarks>
public sealed class WorldNetPresentation {
    private readonly Dictionary<string, Sheet> m_sheets = new(comparer: StringComparer.Ordinal);
    private readonly HashSet<string> m_seen = new(comparer: StringComparer.Ordinal);
    private readonly List<string> m_gone = [];
    private Vector3[] m_offsets = [];

    private sealed record Sheet(WorldPlacementNet Facet, Puck.Physics.FixedNetSpec Spec, SdfSheetDisplacement Displacement);

    /// <summary>Returns the sheet a placement's instances are drawn through, made at its rest layout on first ask, or
    /// <see langword="null"/> for a placement that carries no net or does not resolve to one copy.</summary>
    /// <param name="definition">The document the placement belongs to.</param>
    /// <param name="placement">The placement.</param>
    /// <returns>The sheet, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> or <paramref name="placement"/> is
    /// <see langword="null"/>.</exception>
    public SdfDisplacement? DisplacementFor(WorldDefinition definition, WorldPlacement placement) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: placement);

        if ((placement.Net is not { } facet) || !facet.TrySpecIn(definition: definition, placement: placement, spec: out var spec)) { return null; }
        if (m_sheets.TryGetValue(key: placement.Id, value: out var held) && (held.Facet == facet) && (held.Spec == spec)) { return held.Displacement; }

        var sheet = new Sheet(
            Displacement: new SdfSheetDisplacement(
                across: spec.Across.ToVector3(),
                columns: spec.Columns,
                origin: spec.Origin.ToVector3(),
                reach: (float)spec.Reach,
                rows: spec.Rows,
                up: spec.Up.ToVector3()
            ),
            Facet: facet,
            Spec: spec
        );

        m_sheets[placement.Id] = sheet;
        return sheet.Displacement;
    }
    /// <summary>Publishes every sheet's node offsets from the delivered nodes, before the composition reads revisions, and
    /// forgets the sheets of placements the document no longer holds.</summary>
    /// <param name="definition">The delivered document.</param>
    /// <param name="read">Reads a net's node offsets at the presentation's interpolation fraction
    /// (<see cref="WorldClient.NetOffsets"/>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> or <paramref name="read"/> is
    /// <see langword="null"/>.</exception>
    public void Publish(WorldDefinition definition, NetReader read) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: read);

        if (m_sheets.Count == 0) { return; }
        m_seen.Clear();
        foreach (var placement in definition.Placements) {
            if ((placement.Net is null) || !m_sheets.TryGetValue(key: placement.Id, value: out var sheet)) { continue; }
            var nodes = (sheet.Displacement.Columns * sheet.Displacement.Rows);

            if (m_offsets.Length < nodes) { m_offsets = new Vector3[nodes]; }
            read(placementId: placement.Id, destination: m_offsets.AsSpan(start: 0, length: nodes));
            _ = sheet.Displacement.Publish(offsets: m_offsets.AsSpan(start: 0, length: nodes));
            m_seen.Add(item: placement.Id);
        }
        m_gone.Clear();
        foreach (var id in m_sheets.Keys) { if (!m_seen.Contains(item: id)) { m_gone.Add(item: id); } }
        foreach (var id in m_gone) { m_sheets.Remove(key: id); }
    }

    /// <summary>Reads a net's node offsets into a span, row by row.</summary>
    /// <param name="placementId">The placement carrying the net.</param>
    /// <param name="destination">Receives one offset per node.</param>
    public delegate void NetReader(string placementId, Span<Vector3> destination);
}
