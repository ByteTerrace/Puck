using System.Numerics;
using Puck.SdfVm;
using Puck.SignedDistance;

namespace Puck.World.Client;

/// <summary>Renders a world's field lattice: one sampled distance brick per height field, baked on the CPU from the
/// client's mirror whenever it changes and composed as a union with the field's authored colour. The program shape
/// is fixed by the lattice declaration — a value change re-uploads a brick, never rebuilds the program.</summary>
public sealed class WorldFieldEmitter : ISdfSceneEmitter {
    private const float InverseLambda = 0.57735026f; // 1/√3 — the brick pool's stored-distance scale (KEEP IN SYNC with sdfSampledRegion).
    private const int Reach = 2;

    private readonly WorldClient m_client;
    private int m_cursor;
    private int m_pendingField = -1;
    private int m_pendingRevision;
    private ulong m_pendingSerial;
    private int m_pendingSlot;
    private int m_programRevision;
    private bool[] m_ready = [];
    private float[] m_heights = [];
    private int[] m_uploadedRevisions = [];
    private WorldClientFieldLattice? m_uploadedLattice;
    private ISdfBrickBakeService? m_uploadService;
    private float[] m_voxels = [];

    public WorldFieldEmitter(WorldClient client) {
        ArgumentNullException.ThrowIfNull(argument: client);

        m_client = client;
    }

    // Voxels along +Y: one below the origin, the tallest column any height field can raise, one above — the same
    // one-cell pad the X/Z edges carry.
    private static int BrickLayers(WorldFieldsSection document) {
        var tallest = 0f;

        foreach (var row in document.Fields) {
            tallest = MathF.Max(tallest, ((row.Max * row.HeightScale) * document.Lattice.Layers));
        }

        return Math.Min(
            val1: SdfBrickPoolLayout.BrickDim,
            val2: ((int)MathF.Ceiling(tallest / document.Lattice.CellSize) + 3)
        );
    }
    private static Vector3 ParseColor(string? hex) {
        if (
            (hex is null) ||
            (hex.Length != 7) ||
            (hex[0] != '#')
        ) {
            return Vector3.One;
        }

        return new Vector3(
            x: (Convert.ToInt32(value: hex.Substring(startIndex: 1, length: 2), fromBase: 16) / 255f),
            y: (Convert.ToInt32(value: hex.Substring(startIndex: 3, length: 2), fromBase: 16) / 255f),
            z: (Convert.ToInt32(value: hex.Substring(startIndex: 5, length: 2), fromBase: 16) / 255f)
        );
    }
    // A sampled brick needs the pool dimension on every axis; a lattice wider than the brick edge renders its first
    // BrickDim columns only.
    private static int Clamp(int cells) => Math.Min(
        val1: SdfBrickPoolLayout.BrickDim,
        val2: cells
    );
    private static int HeightFieldCount(WorldFieldsSection document) {
        var count = 0;

        for (var field = 0; (field < document.Fields.Count); field++) {
            if (document.Fields[field].HeightScale > 0f) {
                count++;
            }
        }

        return Math.Min(
            val1: count,
            val2: SdfBrickPoolLayout.MaxBricks
        );
    }
    private static (int Field, WorldFieldRow Row, int Slot) HeightField(WorldFieldsSection document, int ordinal) {
        var slot = 0;

        for (var field = 0; (field < document.Fields.Count); field++) {
            var row = document.Fields[field];

            if (row.HeightScale <= 0f) {
                continue;
            }

            if (slot == ordinal) {
                return (field, row, slot);
            }

            slot++;
        }

        throw new ArgumentOutOfRangeException(paramName: nameof(ordinal));
    }

    /// <inheritdoc/>
    public int RevisionComponentCount => 1;
    /// <inheritdoc/>
    public void WriteRevision(Span<int> destination) => destination[0] = m_programRevision;
    /// <inheritdoc/>
    public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) {
        ArgumentNullException.ThrowIfNull(argument: builder);

        if (m_client.Definition.Fields is not { } document) {
            return;
        }

        var lattice = document.Lattice;
        // The brick box is padded one cell on every side so no column reaches a face: outside the box the kernel
        // returns dist(p, box) + boundaryFloor, and a union brick whose solid touched a face would render the face.
        var boxMin = new Vector3(
            x: (lattice.Origin.X - lattice.CellSize),
            y: (lattice.Origin.Y - lattice.CellSize),
            z: (lattice.Origin.Z - lattice.CellSize)
        );
        var dimX = Clamp(cells: (lattice.Width + 2));
        var dimZ = Clamp(cells: (lattice.Depth + 2));
        var dimY = BrickLayers(document: document);

        var heightFieldCount = HeightFieldCount(document: document);

        for (var ordinal = 0; (ordinal < heightFieldCount); ordinal++) {
            var (field, row, slot) = HeightField(document: document, ordinal: ordinal);

            // The pool's zero-filled/uninitialized bytes describe a surface, not empty space. Keep a live program from
            // naming a slot until its first upload has completed; the probe still declares every possible brick so the
            // frozen capacity envelope dominates the later rebuild.
            if (
                !context.Probe &&
                ((field >= m_ready.Length) || !m_ready[field])
            ) {
                continue;
            }

            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: ParseColor(hex: row.Color)));

            _ = builder.ResetPoint().SampledRegion(
                boxMin: boxMin,
                cellSize: lattice.CellSize,
                dimX: dimX,
                dimY: dimY,
                dimZ: dimZ,
                brickWordOffset: SdfBrickPoolLayout.SlotWordOffset(slot: slot),
                boundaryFloor: (lattice.CellSize * InverseLambda),
                material: material,
                blend: SdfBlendOp.Union
            );
        }
    }
    /// <summary>Re-bakes and uploads every height field's brick when the mirror has changed.</summary>
    /// <param name="bakes">The engine's brick service.</param>
    public void AdvanceBricks(ISdfBrickBakeService bakes) {
        ArgumentNullException.ThrowIfNull(argument: bakes);

        if (!ReferenceEquals(bakes, m_uploadService)) {
            m_uploadService = bakes;
            m_uploadedLattice = null;
            m_ready = [];
            m_uploadedRevisions = [];
            m_pendingField = -1;
            m_programRevision++;
        }

        if (
            !bakes.BrickBakeAvailable ||
            (m_client.Fields is not { } lattice)
        ) {
            return;
        }

        if (!ReferenceEquals(lattice, m_uploadedLattice)) {
            m_uploadedLattice = lattice;
            m_uploadedRevisions = new int[lattice.FieldCount];
            Array.Fill(array: m_uploadedRevisions, value: int.MinValue);
            m_ready = new bool[lattice.FieldCount];
            m_pendingField = -1;
            m_cursor = 0;
            m_programRevision++;
        }

        if (m_pendingField >= 0) {
            var status = bakes.GetBrickState(slot: m_pendingSlot);

            if (
                (status.State != BrickBakeState.Ready) ||
                (status.Serial == m_pendingSerial)
            ) {
                return;
            }

            m_uploadedRevisions[m_pendingField] = m_pendingRevision;

            if (!m_ready[m_pendingField]) {
                m_ready[m_pendingField] = true;
                m_programRevision++;
            }

            m_pendingField = -1;
        }

        var document = lattice.Document;
        var cell = document.Lattice.CellSize;
        var dimX = Clamp(cells: (lattice.Width + 2));
        var dimZ = Clamp(cells: (lattice.Depth + 2));
        var dimY = BrickLayers(document: document);
        var total = ((dimX * dimY) * dimZ);

        if (m_voxels.Length < total) {
            m_voxels = new float[total];
        }

        var heightFieldCount = HeightFieldCount(document: document);

        for (var offset = 0; (offset < heightFieldCount); offset++) {
            var candidate = ((m_cursor + offset) % heightFieldCount);
            var (field, row, slot) = HeightField(document: document, ordinal: candidate);
            var revision = lattice.FieldRevision(field: field);

            if (m_uploadedRevisions[field] == revision) {
                continue;
            }

            var heightCellCount = (lattice.Width * lattice.Depth);

            if (m_heights.Length < heightCellCount) {
                m_heights = new float[heightCellCount];
            }

            // Queue at most one field per produced frame. The engine owns one upload staging region per ring slot;
            // repeatedly replacing a whole multi-field queue faster than it drains starves every slot but the first.
            for (var z = 0; (z < lattice.Depth); z++) {
                for (var x = 0; (x < lattice.Width); x++) {
                    var raised = 0f;

                    for (var y = 0; (y < lattice.Layers); y++) {
                        raised += (lattice.Value(
                            cell: lattice.CellIndex(x: x, y: y, z: z),
                            field: field
                        ) * row.HeightScale);
                    }

                    m_heights[(z * lattice.Width) + x] = raised;
                }
            }

            Bake(
                cell: cell,
                dimX: dimX,
                dimY: dimY,
                dimZ: dimZ,
                heights: m_heights,
                lattice: lattice
            );

            var before = bakes.GetBrickState(slot: slot);

            bakes.UploadBrick(
                dimX: dimX,
                dimY: dimY,
                dimZ: dimZ,
                slot: slot,
                voxels: m_voxels.AsSpan(
                    length: total,
                    start: 0
                )
            );
            m_pendingField = field;
            m_pendingRevision = revision;
            m_pendingSerial = before.Serial;
            m_pendingSlot = slot;
            m_cursor = ((candidate + 1) % heightFieldCount);

            return;
        }
    }
    // Each voxel holds the distance to the union of the nearby raised columns (boxes from one cell below the origin
    // to the column top), exact within Reach cells and a conservative lower bound beyond, scaled by 1/√3 as the
    // pool stores it. Voxel (0,0,0) is centred half a cell above boxMin on every axis.
    private void Bake(WorldClientFieldLattice lattice, float[] heights, float cell, int dimX, int dimY, int dimZ) {
        var far = (Reach * cell);

        for (var vz = 0; (vz < dimZ); vz++) {
            var pz = (((vz + 0.5f) * cell) - cell);

            for (var vy = 0; (vy < dimY); vy++) {
                var py = (((vy + 0.5f) * cell) - cell);

                for (var vx = 0; (vx < dimX); vx++) {
                    var px = (((vx + 0.5f) * cell) - cell);
                    var best = far;

                    for (var z = Math.Max(0, (vz - 1 - Reach)); (z <= Math.Min((lattice.Depth - 1), (vz - 1 + Reach))); z++) {
                        for (var x = Math.Max(0, (vx - 1 - Reach)); (x <= Math.Min((lattice.Width - 1), (vx - 1 + Reach))); x++) {
                            var top = heights[(z * lattice.Width) + x];

                            if (top <= 0f) {
                                continue;
                            }

                            var dx = MathF.Max(((x * cell) - px), (px - ((x + 1) * cell)));
                            var dy = MathF.Max((-cell - py), (py - top));
                            var dz = MathF.Max(((z * cell) - pz), (pz - ((z + 1) * cell)));
                            var ox = MathF.Max(dx, 0f);
                            var oy = MathF.Max(dy, 0f);
                            var oz = MathF.Max(dz, 0f);
                            var distance = (MathF.Sqrt(((ox * ox) + (oy * oy)) + (oz * oz)) + MathF.Min(MathF.Max(dx, MathF.Max(dy, dz)), 0f));

                            if (distance < best) {
                                best = distance;
                            }
                        }
                    }

                    m_voxels[(((vz * dimY) + vy) * dimX) + vx] = (best * InverseLambda);
                }
            }
        }
    }
}
