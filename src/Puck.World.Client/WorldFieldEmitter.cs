using System.Numerics;
using Puck.SdfVm;
using Puck.SignedDistance;

namespace Puck.World.Client;

/// <summary>Renders a world's field lattice: one sampled distance brick per height field, baked on the CPU and composed
/// as a union with the field's authored colour. A field's cells reach it the way every presentation read of state
/// does, through the client's state mirror: the presentation manifest registers each height field's row whole
/// (<see cref="WorldStateConversion.Row"/>, the same slot a pass's array bound to <c>state.&lt;field&gt;</c> reads),
/// and the brick is re-baked whenever that slot moves. The program shape is fixed by the lattice declaration — a
/// value change publishes an immutable voxel snapshot in the program, so baked meshes key and sample the same values.</summary>
public sealed class WorldFieldEmitter : ISdfSceneEmitter {
    private const float InverseLambda = 0.57735026f; // 1/√3 — the brick pool's stored-distance scale (KEEP IN SYNC with sdfSampledRegion).
    private const int Reach = 2;

    private readonly WorldClient m_client;
    private readonly WorldBakedColors m_colors;

    private float[] m_heights = [];
    private float[][] m_snapshots = [];
    private float[][] m_heightSnapshots = [];
    private int m_programRevision;
    private bool[] m_ready = [];
    private int[] m_uploadedChanged = [];
    private (WorldFieldsSection Region, WorldFieldRow Row)[] m_heightFields = [];
    private float[] m_uploadedHeightScales = [];
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
    // pool stores it. Voxel (0,0,0) is centred half a cell above boxMin on every axis.
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
    // Voxels along +Y: one below the origin, the tallest column any height field can raise, one above — the same
    // one-cell pad the X/Z edges carry.
    private static int BrickLayers(WorldFieldsSection document) {
        var tallest = 0f;

        foreach (var row in document.Fields) {
            tallest = MathF.Max(
                x: tallest,
                y: ((row.Max * row.HeightScale) * document.Lattice.Layers)
            );
        }

        return Math.Min(
            val1: SdfBrickPoolLayout.BrickDim,
            val2: (((int)MathF.Ceiling(x: (tallest / document.Lattice.CellSize))) + 3)
        );
    }
    // A sampled brick needs the pool dimension on every axis; a lattice wider than the brick edge renders its first
    // BrickDim columns only.
    private static int Clamp(int cells) => Math.Min(
        val1: SdfBrickPoolLayout.BrickDim,
        val2: cells
    );
    // Height-field slots are global across the world's regions; every slot keeps the lattice that owns its row.
    private void Shape(IReadOnlyList<WorldFieldsSection> regions) {
        if (ReferenceEquals(objA: regions, objB: m_uploadedRegions)) { return; }
        var heightFields = regions.SelectMany(selector: static region => region.Fields
            .Where(predicate: static row => (row.HeightScale > 0f))
            .Select(selector: row => (Region: region, Row: row)))
            .ToArray();

        if (heightFields.Length > SdfBrickPoolLayout.MaxBricks) {
            throw new InvalidOperationException(message: "The declared physical regions exceed the resident sampled-field brick buffer.");
        }
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
        m_uploadedHeightScales = new float[heightFields.Length];
        m_ready = new bool[heightFields.Length];
        m_snapshots = new float[heightFields.Length][];
        m_heightSnapshots = new float[heightFields.Length][];
        if (dropsReadyField) { m_programRevision++; }
    }
    // Whether the live program names any field's brick (Emit names a field only once its first upload is ready), so
    // that a reset of the brick state, which forgets every ready field, changes the program. A reset before any field is
    // ready leaves the program as it was, and moving its revision then would rebuild an unchanged program and render
    // every standing view again.
    private bool AnyReady() => (Array.IndexOf(array: m_ready, value: true) >= 0);

    /// <summary>Publishes changed height fields as immutable sampled values. The ordinary brick-update phase calls
    /// this before capturing geometry revisions; external pool uploads are unnecessary.</summary>
    /// <param name="bakes">The engine's sampling service, supplied by the frame-source contract.</param>
    /// <exception cref="ArgumentNullException"><paramref name="bakes"/> is <see langword="null"/>.</exception>
    public void AdvanceBricks(ISdfBrickBakeService bakes) {
        ArgumentNullException.ThrowIfNull(argument: bakes);

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
            var dimX = Clamp(cells: (lattice.Width + 2));
            var dimZ = Clamp(cells: (lattice.Depth + 2));
            var dimY = BrickLayers(document: document);
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
                ((m_uploadedChanged[field] == changed) && (m_uploadedHeightScales[field] == row.HeightScale))
            ) {
                continue;
            }

            var heightCellCount = (lattice.Width * lattice.Depth);

            if (m_heights.Length < heightCellCount) {
                m_heights = new float[heightCellCount];
            }

            // A row slot's element i is lattice cell i: z, then layer, then x.
            for (var z = 0; (z < lattice.Depth); z++) {
                for (var x = 0; (x < lattice.Width); x++) {
                    var raised = 0f;

                    for (var y = 0; (y < lattice.Layers); y++) {
                        raised += (((float)values[((((z * lattice.Layers) + y) * lattice.Width) + x)]) * row.HeightScale);
                    }

                    // Use the mesh identity's spatial resolution. Sub-resolution simulation changes need no new
                    // presentation geometry; simulation and medium queries retain their full fixed-point values.
                    m_heights[((z * lattice.Width) + x)] = MathF.Round(raised * SdfMeshGeometryBuilder.PositionSteps) / SdfMeshGeometryBuilder.PositionSteps;
                }
            }

            m_uploadedChanged[field] = changed;
            m_uploadedHeightScales[field] = row.HeightScale;
            var heights = m_heights.AsSpan(0, heightCellCount);
            if (m_ready[field] && heights.SequenceEqual(m_heightSnapshots[field])) { continue; }
            m_heightSnapshots[field] = heights.ToArray();
            Bake(
                cell: cell,
                dimX: dimX,
                dimY: dimY,
                dimZ: dimZ,
                heights: m_heights,
                lattice: lattice
            );

            var snapshot = m_voxels.AsSpan(0, total);
            if (m_ready[field] && snapshot.SequenceEqual(m_snapshots[field])) { continue; }
            m_snapshots[field] = snapshot.ToArray();
            m_ready[field] = true;
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
            // The brick box is padded one cell on every side so no column reaches a face.
            var boxMin = new Vector3(
                x: (lattice.Origin.X - lattice.CellSize),
                y: (lattice.Origin.Y - lattice.CellSize),
                z: (lattice.Origin.Z - lattice.CellSize)
            );
            var dimX = Clamp(cells: (lattice.Width + 2));
            var dimZ = Clamp(cells: (lattice.Depth + 2));
            var dimY = BrickLayers(document: document);

            // Keep an uninitialized field out of the live program. The probe reserves every snapshot's full extent.
            if (
                !context.Probe &&
                ((field >= m_ready.Length) || !m_ready[field])
            ) {
                continue;
            }

            // The brick holds only distances, so a state-bound colour is read through the mirror here, and its move
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
                dimX: dimX,
                dimY: dimY,
                dimZ: dimZ,
                values: (context.Probe ? new float[checked(dimX * dimY * dimZ)] : m_snapshots[field]),
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
