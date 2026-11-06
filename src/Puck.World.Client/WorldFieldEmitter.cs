using System.Numerics;
using Puck.SdfVm;
using Puck.SignedDistance;

namespace Puck.World.Client;

/// <summary>Renders a world's field lattice: one height field per height-bearing row (<see cref="SdfHeightField"/>),
/// composed as a union with the row's authored colour. A field's cells reach it the way every presentation read of state
/// does, through the client's state mirror: the presentation manifest registers each height field's row whole
/// (<see cref="WorldStateConversion.Row"/>, the same slot a pass's array bound to <c>state.&lt;field&gt;</c> reads), and
/// the columns' heights are published again whenever that slot moves. A height change publishes the field's heights, which
/// the renderer displaces the field's baked reference by as it draws, so it moves no program revision and keys no mesh;
/// only a column raised from nothing or lowered to nothing keys a new reference mesh. The program rebuilds only when a field
/// joins it, its lattice changes shape, or its colour moves.</summary>
public sealed class WorldFieldEmitter : ISdfSceneEmitter {
    private readonly WorldClient m_client;
    private readonly WorldBakedColors m_colors;

    private float[] m_heights = [];
    // The height field the program names for each field, null until its first publication.
    private SdfHeightField?[] m_surfaces = [];
    private int m_programRevision;
    private int[] m_uploadedChanged = [];
    private (WorldFieldsSection Region, WorldFieldRow Row)[] m_heightFields = [];
    // The height scale each field's heights were taken at.
    private float[] m_uploadedScales = [];
    private IReadOnlyList<WorldFieldsSection>? m_uploadedRegions;

    /// <summary>Initializes a new instance of the <see cref="WorldFieldEmitter"/> class over a client.</summary>
    /// <param name="client">The client whose delivered definition declares the fields and whose state mirror holds
    /// their cells.</param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <see langword="null"/>.</exception>
    public WorldFieldEmitter(WorldClient client) {
        ArgumentNullException.ThrowIfNull(argument: client);

        m_client = client;
        m_colors = new WorldBakedColors(mirror: client.StateMirror);
    }

    // Height-field slots are global across the world's regions; every slot keeps the lattice that owns its row.
    private void Shape(IReadOnlyList<WorldFieldsSection> regions) {
        if (ReferenceEquals(objA: regions, objB: m_uploadedRegions)) { return; }
        var heightFields = regions.SelectMany(selector: static region => region.Fields
            .Where(predicate: static row => (row.HeightScale > 0f))
            .Select(selector: row => (Region: region, Row: row)))
            .ToArray();

        var sameShape = (heightFields.Length == m_heightFields.Length);

        for (var field = 0; (sameShape && (field < heightFields.Length)); field++) {
            var previous = m_heightFields[field];
            var next = heightFields[field];

            sameShape = ((previous.Region.Name == next.Region.Name) &&
                (previous.Region.Lattice == next.Region.Lattice) && (previous.Row.Name == next.Row.Name) &&
                (previous.Row.Max == next.Row.Max) && (previous.Row.HeightScale == next.Row.HeightScale));
        }
        m_uploadedRegions = regions;
        m_heightFields = heightFields;
        if (sameShape) { return; }
        var dropsReadyField = AnyReady();

        m_uploadedChanged = new int[heightFields.Length];
        Array.Fill(array: m_uploadedChanged, value: int.MinValue);
        m_uploadedScales = new float[heightFields.Length];
        m_surfaces = new SdfHeightField?[heightFields.Length];
        if (dropsReadyField) { m_programRevision++; }
    }
    // Whether the live program names any field (Emit names a field only once its first publication made it), so that a
    // reset, which forgets every field, changes the program. A reset before any field is made leaves the program as it was,
    // and moving its revision then would rebuild an unchanged program and render every standing view again.
    private bool AnyReady() => Array.Exists(array: m_surfaces, match: static surface => (surface is not null));
    // The greatest height a row raises a column of its lattice to above the origin.
    private static float TopOf(WorldFieldLatticeDefinition lattice, WorldFieldRow row) => ((row.Max * row.HeightScale) * lattice.Layers);

    /// <summary>Publishes each height field's heights whose row moved, before the composition reads revisions: a field's
    /// first publication makes its height field and moves the program revision; any other moves no revision.</summary>
    public void PrepareFrame() {
        Shape(regions: m_client.Definition.FieldRegions);

        var mirror = m_client.StateMirror;

        for (var field = 0; (field < m_heightFields.Length); field++) {
            var (document, row) = m_heightFields[field];
            var lattice = document.Lattice;
            var cellCount = checked(((lattice.Width * lattice.Layers) * lattice.Depth));
            var rowSlot = mirror.SlotOf(
                binding: new StateBinding(
                    Key: null,
                    Row: row.Name,
                    Target: false
                ),
                conversion: WorldStateConversion.Row
            );
            var values = mirror.RowValues(slot: rowSlot);
            var changed = mirror.Changed(slot: rowSlot);

            if ((values.Length < cellCount) || ((m_uploadedChanged[field] == changed) && (m_uploadedScales[field] == row.HeightScale))) {
                continue;
            }

            var heightCellCount = (lattice.Width * lattice.Depth);

            if (m_heights.Length < heightCellCount) { m_heights = new float[heightCellCount]; }

            // A column's height is its layers' values times the row's height scale, unquantized: the drawn surface follows
            // it at draw time. Simulation and medium queries keep their full fixed-point values. A row slot's element i is
            // lattice cell i: z, then layer, then x.
            for (var z = 0; (z < lattice.Depth); z++) {
                for (var x = 0; (x < lattice.Width); x++) {
                    var raised = 0f;

                    for (var y = 0; (y < lattice.Layers); y++) {
                        raised += (((float)values[((((z * lattice.Layers) + y) * lattice.Width) + x)]) * row.HeightScale);
                    }

                    m_heights[((z * lattice.Width) + x)] = raised;
                }
            }

            m_uploadedChanged[field] = changed;
            m_uploadedScales[field] = row.HeightScale;

            var heights = m_heights.AsSpan(start: 0, length: heightCellCount);

            if (m_surfaces[field] is { } surface) {
                _ = surface.Publish(heights: heights);
                continue;
            }
            m_surfaces[field] = new SdfHeightField(
                cell: lattice.CellSize,
                depth: lattice.Depth,
                heights: heights,
                origin: new Vector3(x: lattice.Origin.X, y: lattice.Origin.Y, z: lattice.Origin.Z),
                top: TopOf(lattice: lattice, row: row),
                width: lattice.Width
            );
            m_programRevision++;
        }
    }
    /// <inheritdoc/>
    public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) {
        ArgumentNullException.ThrowIfNull(argument: builder);

        Shape(regions: m_client.Definition.FieldRegions);
        if (!context.Probe) { m_colors.Begin(); }

        for (var field = 0; (field < m_heightFields.Length); field++) {
            var (document, row) = m_heightFields[field];
            var lattice = document.Lattice;
            var surface = ((field < m_surfaces.Length) ? m_surfaces[field] : null);

            // Keep an unpublished field out of the live program. The probe reserves every field's full extent.
            if (!context.Probe && (surface is null)) {
                continue;
            }

            // The field holds only distances, so a state-bound colour is read through the mirror here, and its move
            // rebuilds the program (WriteRevision) with no re-bake.
            var material = builder.AddMaterial(material: WorldSurfaceMaterials.Resolve(
                entry: row.Material,
                colors: m_colors,
                albedo: m_colors.Resolve(fallback: Vector3.One, value: (row.Material?.Color ?? row.Color)),
                paints: row.Material?.Paints?.Select(paint => paint.ToPaint(resolve: m_colors.LayerColor)).ToArray()
            ));

            // Each surface owns a bake range: mixing it into the unowned world range would classify glass alongside
            // opaque fields, and would bake a small lattice over the world's far-distance cube.
            _ = builder.HeightField(
                field: (surface ?? new SdfHeightField(
                    cell: lattice.CellSize,
                    depth: lattice.Depth,
                    heights: new float[checked(lattice.Width * lattice.Depth)],
                    origin: new Vector3(x: lattice.Origin.X, y: lattice.Origin.Y, z: lattice.Origin.Z),
                    top: TopOf(lattice: lattice, row: row),
                    width: lattice.Width
                )),
                material: material
            );
        }
    }
    /// <inheritdoc/>
    public void WriteRevision(Span<int> destination) {
        if (m_colors.TryTakeMove()) {
            m_programRevision++;
        }

        destination[0] = m_programRevision;
    }

    /// <inheritdoc/>
    public int RevisionComponentCount => 1;
}
