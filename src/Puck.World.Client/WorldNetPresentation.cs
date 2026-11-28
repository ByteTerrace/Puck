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
    // The parting plays' clock (presentation seconds the frames advanced) and the bands the latest step published.
    private double m_clock;
    private IReadOnlyList<Puck.SdfVm.SdfFormBand> m_bands = [];
    private readonly List<Puck.SdfVm.SdfFormBand> m_bandScratch = [];

    private sealed record Sheet(WorldPlacementNet Facet, Puck.Physics.FixedNetSpec Spec, SdfSheetDisplacement Displacement) {
        // The opening cell as the latest step read it (null before the first read, so a net met already open plays
        // nothing), and the parting or closing play it began, by its start on the clock.
        public bool? Open { get; set; }
        public double PlayStart { get; set; } = double.NaN;
        public long Plays { get; set; }
    }

    /// <summary>Gets the form bands the nets' partings sweep this frame (<see cref="Puck.SdfVm.ISdfSceneEmitter.FormBands"/>):
    /// one per net whose opening cell changed within its effect's length, keyed by the net's sheet
    /// (<see cref="Puck.SdfVm.SdfFormBand.Displacement"/>), a front growing out from the opening's centre across its rim,
    /// whole on both sides. A new list whenever any plays, the one empty list while none does.</summary>
    public IReadOnlyList<Puck.SdfVm.SdfFormBand> FormBands => m_bands;

    /// <summary>Advances the parting plays: reads each net's opening cell from the presented state, begins a play where it
    /// changed (its effect over the open time when it opened, over the close time when it closed), and publishes the
    /// bands of the plays still running. Presentation only: the seed is the placement's and the play's count, so every
    /// client plays the same parting.</summary>
    /// <param name="mirror">The presented state the opening cells are read from.</param>
    /// <param name="deltaSeconds">The seconds since the previous step.</param>
    /// <exception cref="ArgumentNullException"><paramref name="mirror"/> is <see langword="null"/>.</exception>
    public void StepPartings(WorldStateMirror mirror, float deltaSeconds) {
        ArgumentNullException.ThrowIfNull(argument: mirror);

        m_clock += Math.Max(val1: deltaSeconds, val2: 0f);
        m_bandScratch.Clear();
        foreach (var (id, sheet) in m_sheets) {
            if ((sheet.Facet.Opening is not { Effect: { } effect } opening) || (sheet.Spec.Opening is not { } kernel)) { continue; }

            var open = (mirror.Scalar(fallback: 0f, scalar: opening.By) != 0f);

            if ((sheet.Open is { } was) && (was != open)) {
                sheet.PlayStart = m_clock;
                sheet.Plays++;
            }
            sheet.Open = open;
            if (double.IsNaN(d: sheet.PlayStart) || ((m_clock - sheet.PlayStart) > effect.TotalSeconds)) { continue; }

            var across = Vector3.Normalize(value: sheet.Spec.Across.ToVector3());
            var up = Vector3.Normalize(value: (sheet.Spec.Up.ToVector3() - (across * Vector3.Dot(vector1: sheet.Spec.Up.ToVector3(), vector2: across))));
            var centre = ((sheet.Spec.Origin.ToVector3() + (across * (float)kernel.CenterAcross)) + (up * (float)kernel.CenterUp));
            var band = WorldFormEffects.Band(
                arriving: false,
                direction: Vector3.Cross(vector1: across, vector2: up),
                effect: effect,
                elapsed: (float)(m_clock - sheet.PlayStart - Math.Max(val1: effect.Delay, val2: 0f)),
                firstSlot: 0,
                high: MathF.Max(x: (float)kernel.HalfWidth, y: (float)kernel.HalfHeight),
                low: 0f,
                origin: centre,
                parted: false,
                rotation: Quaternion.Identity,
                seconds: (open ? opening.OpenSeconds : opening.CloseSeconds),
                seed: WorldFormEffects.Seed(body: -1, creation: id, ordinal: sheet.Plays),
                side: Puck.SdfVm.SdfFormBandSide.Whole,
                slotCount: 0
            );

            m_bandScratch.Add(item: (band with { Displacement = sheet.Displacement, Radial = true }));
        }
        m_bands = ((m_bandScratch.Count == 0) ? [] : [.. m_bandScratch]);
    }

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
                reach: (float)spec.Extent,
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
