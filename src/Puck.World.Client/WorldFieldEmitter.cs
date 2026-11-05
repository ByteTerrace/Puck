using System.Numerics;
using Puck.SdfVm;
using Puck.SignedDistance;

namespace Puck.World.Client;

/// <summary>Renders a world's field lattice: one sampled distance field per height field (<see cref="SdfSampledField"/>),
/// baked on the CPU and composed as a union with the field's authored colour. A field's cells reach it the way every
/// presentation read of state does, through the client's state mirror: the presentation manifest registers each height
/// field's row whole (<see cref="WorldStateConversion.Row"/>, the same slot a pass's array bound to
/// <c>state.&lt;field&gt;</c> reads), and the voxels are baked again whenever that slot moves. A drawn height change
/// publishes a new snapshot of the field and moves no program revision: the program names the field, the frame carries
/// its snapshot, and the mesh bake keys and samples that snapshot. The program rebuilds only when a field joins it, its
/// lattice changes shape, or its colour moves.</summary>
public sealed class WorldFieldEmitter : ISdfSceneEmitter {
    private const float InverseLambda = 0.57735026f; // 1/√3 — the sampled field's stored-distance scale (KEEP IN SYNC with sdfSampledRegion).
    private const int Reach = 2;

    private readonly WorldClient m_client;
    private readonly WorldBakedColors m_colors;

    private float[] m_heights = [];
    // Each field's drawn heights, and the sampled field the program names for it, null until its first bake.
    private float[][] m_heightSnapshots = [];
    private SdfSampledField?[] m_surfaces = [];
    private int m_programRevision;
    private int[] m_uploadedChanged = [];
    private (WorldFieldsSection Region, WorldFieldRow Row)[] m_heightFields = [];
    // The height scale and surface step each field's drawn heights were taken at.
    private (float HeightScale, float SurfaceStep)[] m_uploadedScales = [];
    private IReadOnlyList<WorldFieldsSection>? m_uploadedRegions;
    private float[] m_voxels = [];

    /// <summary>Initializes a new instance of the <see cref="WorldFieldEmitter"/> class over a client.</summary>
    /// <param name="client">The client whose delivered definition declares the fields and whose state mirror holds
    /// their cells.</param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <see langword="null"/>.</exception>
    public WorldFieldEmitter(WorldClient client) {
        ArgumentNullException.ThrowIfNull(argument: client);

        m_client = client;
        m_colors = new WorldBakedColors(mirror: client.StateMirror);
    }

    // Each voxel holds the distance to the union of the nearby raised columns (boxes from one cell below the origin
    // to the column top), exact within Reach cells and a conservative lower bound beyond, scaled by 1/√3 as the
    // sampled field stores it. Voxel (0,0,0) is centred half a cell above boxMin on every axis.
    private void Bake(WorldFieldLatticeDefinition lattice, float[] heights, float cell, int dimX, int dimY, int dimZ) {
        var far = (Reach * cell);

        for (var vz = 0; (vz < dimZ); vz++) {
            var pz = (((vz + 0.5f) * cell) - cell);

            for (var vy = 0; (vy < dimY); vy++) {
                var py = (((vy + 0.5f) * cell) - cell);

                for (var vx = 0; (vx < dimX); vx++) {
                    var px = (((vx + 0.5f) * cell) - cell);
                    var best = far;

                    for (var z = Math.Max(
                        val1: 0,
                        val2: ((vz - 1) - Reach)
                    ); (z <= Math.Min(
                        val1: (lattice.Depth - 1),
                        val2: ((vz - 1) + Reach)
                    )); z++) {
                        for (var x = Math.Max(
                            val1: 0,
                            val2: ((vx - 1) - Reach)
                        ); (x <= Math.Min(
                            val1: (lattice.Width - 1),
                            val2: ((vx - 1) + Reach)
                        )); x++) {
                            var top = heights[((z * lattice.Width) + x)];

                            if (top <= 0f) {
                                continue;
                            }

                            var dx = MathF.Max(
                                x: ((x * cell) - px),
                                y: (px - ((x + 1) * cell))
                            );
                            var dy = MathF.Max(
                                x: (-cell - py),
                                y: (py - top)
                            );
                            var dz = MathF.Max(
                                x: ((z * cell) - pz),
                                y: (pz - ((z + 1) * cell))
                            );
                            var ox = MathF.Max(
                                x: dx,
                                y: 0f
                            );
                            var oy = MathF.Max(
                                x: dy,
                                y: 0f
                            );
                            var oz = MathF.Max(
                                x: dz,
                                y: 0f
                            );
                            var distance = (MathF.Sqrt(x: (((ox * ox) + (oy * oy)) + (oz * oz))) + MathF.Min(
                                x: MathF.Max(
                                    x: dx,
                                    y: MathF.Max(
                                        x: dy,
                                        y: dz
                                    )
                                ),
                                y: 0f
                            ));

                            if (distance < best) {
                                best = distance;
                            }
                        }
                    }

                    m_voxels[((((vz * dimY) + vy) * dimX) + vx)] = (best * InverseLambda);
                }
            }
        }
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
                (previous.Region.Lattice == next.Region.Lattice) && (previous.Row.Name == next.Row.Name));
        }
        m_uploadedRegions = regions;
        m_heightFields = heightFields;
        if (sameShape) { return; }
        var dropsReadyField = AnyReady();

        m_uploadedChanged = new int[heightFields.Length];
        Array.Fill(array: m_uploadedChanged, value: int.MinValue);
        m_uploadedScales = new (float, float)[heightFields.Length];
        m_surfaces = new SdfSampledField?[heightFields.Length];
        m_heightSnapshots = new float[heightFields.Length][];
        if (dropsReadyField) { m_programRevision++; }
    }
    // Whether the live program names any field (Emit names a field only once its first bake made it), so that a reset,
    // which forgets every field, changes the program. A reset before any field is made leaves the program as it was, and
    // moving its revision then would rebuild an unchanged program and render every standing view again.
    private bool AnyReady() => Array.Exists(array: m_surfaces, match: static surface => (surface is not null));

    /// <summary>Bakes each height field whose drawn heights moved and publishes its voxels as a new snapshot of its
    /// sampled field, before the composition reads revisions: a field's first bake, or one whose lattice changed shape,
    /// makes a new field and moves the program revision; any other moves no revision.</summary>
    public void PrepareFrame() {
        Shape(regions: m_client.Definition.FieldRegions);

        var mirror = m_client.StateMirror;
        var heightFieldCount = m_heightFields.Length;

        for (var offset = 0; (offset < heightFieldCount); offset++) {
            var candidate = offset;

            var (document, row) = m_heightFields[candidate];
            var field = candidate;
            var lattice = document.Lattice;
            var cell = lattice.CellSize;
            var cellCount = checked(((lattice.Width * lattice.Layers) * lattice.Depth));
            var (dimX, dimY, dimZ) = WorldFieldCapacity.SurfaceVoxels(region: document);
            var total = checked(((dimX * dimY) * dimZ));

            if (m_voxels.Length < total) { m_voxels = new float[total]; }

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

            if (
                (values.Length < cellCount) ||
                ((m_uploadedChanged[field] == changed) && (m_uploadedScales[field] == (row.HeightScale, row.SurfaceStep)))
            ) {
                continue;
            }

            var heightCellCount = (lattice.Width * lattice.Depth);

            if (m_heights.Length < heightCellCount) {
                m_heights = new float[heightCellCount];
            }

            // The drawn heights move by whole surface steps (WorldFieldRow.SurfaceStep), at least the mesh identity's
            // own resolution, and a column moves only once its raised height has left the step drawn by a whole step:
            // a continuously changing store rebakes its surface once a step, never each lattice step, a value hovering
            // about a step boundary redraws nothing, and a height revisited keys a mesh already baked. A scale change
            // or step change or a first upload draws every column at its nearest step. Simulation and medium queries keep their full
            // fixed-point values.
            var step = MathF.Max(x: row.SurfaceStep, y: (1f / SdfMeshGeometryBuilder.PositionSteps));
            var surface = m_surfaces[field];
            var drawn = (((surface is not null) && (m_uploadedScales[field] == (row.HeightScale, row.SurfaceStep))) ? m_heightSnapshots[field] : null);

            // A row slot's element i is lattice cell i: z, then layer, then x.
            for (var z = 0; (z < lattice.Depth); z++) {
                for (var x = 0; (x < lattice.Width); x++) {
                    var raised = 0f;

                    for (var y = 0; (y < lattice.Layers); y++) {
                        raised += (((float)values[((((z * lattice.Layers) + y) * lattice.Width) + x)]) * row.HeightScale);
                    }

                    var column = ((z * lattice.Width) + x);

                    m_heights[column] = (((drawn is not null) && (MathF.Abs(x: (raised - drawn[column])) < step))
                        ? drawn[column]
                        : (MathF.Round(x: (raised / step)) * step));
                }
            }

            m_uploadedChanged[field] = changed;
            m_uploadedScales[field] = (row.HeightScale, row.SurfaceStep);
            var heights = m_heights.AsSpan(0, heightCellCount);
            if ((surface is not null) && heights.SequenceEqual(m_heightSnapshots[field])) { continue; }
            m_heightSnapshots[field] = heights.ToArray();
            Bake(
                cell: cell,
                dimX: dimX,
                dimY: dimY,
                dimZ: dimZ,
                heights: m_heights,
                lattice: lattice
            );

            var voxels = m_voxels.AsSpan(0, total);

            if ((surface is not null) && (surface.DimX == dimX) && (surface.DimY == dimY) && (surface.DimZ == dimZ)) {
                if (!voxels.SequenceEqual(other: surface.Current.Values)) { _ = surface.Publish(values: voxels); }
                continue;
            }
            m_surfaces[field] = new SdfSampledField(dimX: dimX, dimY: dimY, dimZ: dimZ, values: voxels);
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
            // The box is padded one cell on every side so no column reaches a face.
            var boxMin = new Vector3(
                x: (lattice.Origin.X - lattice.CellSize),
                y: (lattice.Origin.Y - lattice.CellSize),
                z: (lattice.Origin.Z - lattice.CellSize)
            );
            var (dimX, dimY, dimZ) = WorldFieldCapacity.SurfaceVoxels(region: document);
            var surface = ((field < m_surfaces.Length) ? m_surfaces[field] : null);

            // Keep an unbaked field out of the live program. The probe reserves every field's full extent.
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

            // Each surface owns a bake range: mixing it into the unowned world range would classify glass
            // alongside opaque fields, and would bake a small lattice over the world's far-distance cube.
            var extent = new Vector3(dimX, dimY, dimZ) * lattice.CellSize;
            _ = builder.BeginInstance(boundCenter: (boxMin + extent * 0.5f), boundRadius: extent.Length() * 0.5f,
                box: (boxMin, boxMin + extent)).ResetPoint().SampledValues(
                boxMin: boxMin,
                cellSize: lattice.CellSize,
                field: (surface ?? new SdfSampledField(dimX: dimX, dimY: dimY, dimZ: dimZ, values: new float[checked((dimX * dimY) * dimZ)])),
                boundaryFloor: (lattice.CellSize * InverseLambda),
                material: material,
                blend: SdfBlendOp.Union
            ).EndInstance();
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
