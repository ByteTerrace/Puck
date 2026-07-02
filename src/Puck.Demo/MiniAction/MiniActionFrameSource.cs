using System.Numerics;
using Puck.Maths;
using Puck.SdfVm;

namespace Puck.Demo.MiniAction;

/// <summary>
/// Bridges the deterministic <see cref="MiniActionWorld"/> to the SDF renderer. It builds the static room program ONCE
/// (the floor, four walls, and one box per FIXED player slot at a dynamic-transform slot) and, each frame, emits the
/// slots' current transforms (which the renderer uploads into the dynamic-transform buffer) plus the camera director's
/// view list. The program is never rebuilt — a player moves, joins, or leaves purely by changing the per-frame
/// transform buffer (a free slot's box rides a hidden position) and the per-frame view regions.
/// </summary>
public sealed class MiniActionFrameSource : ISdfFrameSource {
    // The coarse power-of-two grid (in world units) the render origin snaps to: 2^8 = 256. Snapping keeps the rebased
    // floats stable between snaps and the static-program rebuilds rare.
    private const int RenderOriginGridLog2 = 8;

    private readonly MiniActionWorld m_world;
    private readonly MiniActionRoom m_room;
    private readonly CameraDirector m_director;
    // Reused render-relative position buffer for the camera director — Cleared+refilled each frame (no per-frame alloc).
    private readonly List<Vector3> m_activePositions = new(capacity: MiniActionWorld.MaxPlayers);
    private SdfProgram? m_program;
    // The world anchor the current static program is authored relative to; the program is rebuilt only when the
    // freshly-computed origin snaps away from this.
    private WorldCoord3 m_renderOrigin;
    private float m_time;

    /// <summary>Initializes the frame source over a world, its room, and the camera director that lays out the views.</summary>
    public MiniActionFrameSource(MiniActionWorld world, MiniActionRoom room, CameraDirector director) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(director);

        m_world = world;
        m_room = room;
        m_director = director;
    }

    /// <inheritdoc/>
    public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) {
        m_time += deltaSeconds;

        // The per-frame world anchor: the active players' centroid snapped to a coarse grid. Everything the GPU sees
        // (the static room, the player boxes, the cameras) is expressed relative to it — subtracted in FIXED POINT
        // before the float cast — so the renderer stays precise no matter how far the room sits from the world origin.
        var renderOrigin = ComputeRenderOrigin();

        // The static room is authored relative to the anchor; rebuild ONLY when the anchor snaps to a new grid cell
        // (rare). The instruction count is unchanged, so the renderer's once-sized program buffer stays valid.
        var programChanged = ((m_program is null) || (renderOrigin != m_renderOrigin));

        if (programChanged) {
            m_program = BuildProgram(renderOrigin: renderOrigin);
            m_renderOrigin = renderOrigin;
        }

        // Render-relative, alpha-interpolated player positions for the camera director (reused list — no per-frame alloc).
        m_activePositions.Clear();

        foreach (var slot in m_world.Slots) {
            if (slot is not null) {
                m_activePositions.Add(item: slot.Body.RenderRelativePositionAt(renderOrigin: renderOrigin, alpha: interpolationAlpha));
            }
        }

        return new SdfFrame(
            Program: m_program!, // non-null: programChanged is true whenever m_program was null, so it was just built
            ProgramChanged: programChanged,
            Views: m_director.Compose(activePositions: m_activePositions, imageWidth: width, imageHeight: height, deltaSeconds: deltaSeconds),
            Time: m_time,
            WarpAmount: 0f
        ) {
            DynamicTransforms = m_world.DynamicTransforms(renderOrigin: renderOrigin, alpha: interpolationAlpha),
        };
    }

    // The render origin: the active players' centroid (a raw-integer average — presentation only, so the averaging
    // rounding is immaterial and is washed out by the snap) floored to the coarse grid, carrying the cell. The origin
    // cell when the room is empty.
    private WorldCoord3 ComputeRenderOrigin() {
        var anchor = WorldCoord3.Zero;
        var hasAnchor = false;
        long sumX = 0L, sumY = 0L, sumZ = 0L;
        var count = 0;

        foreach (var slot in m_world.Slots) {
            if (slot is null) {
                continue;
            }

            var position = slot.Body.Position;

            if (!hasAnchor) {
                anchor = position;
                hasAnchor = true;
            }

            // Sum each body's offset from the common anchor (cell-aware), so the centroid is well-defined even if bodies
            // span cells; for the demo all bodies share one cell, so this averages their local offsets.
            var offset = position.Delta(origin: anchor);

            sumX += offset.X.Value;
            sumY += offset.Y.Value;
            sumZ += offset.Z.Value;
            ++count;
        }

        if (count == 0) {
            // The room is empty (overview camera): anchor at the SPAWN cell, not the origin cell, so the static room and
            // hidden boxes rebase with a zero cell difference. A cell-0 anchor against a far spawn cell would overflow
            // WorldCoord3.Delta's (cellDiff << 36) term.
            return m_world.SpawnAnchor;
        }

        var centroidOffset = new FixedVector3(
            X: FixedQ4816.FromRawBits(value: (sumX / count)),
            Y: FixedQ4816.FromRawBits(value: (sumY / count)),
            Z: FixedQ4816.FromRawBits(value: (sumZ / count))
        );

        return SnapToGrid(value: (anchor + centroidOffset));
    }

    // Floor each LOCAL component to a multiple of the coarse grid (2^RenderOriginGridLog2 world units) by masking off the
    // low raw bits — a deterministic, sign-correct (toward −∞) snap; the cell index is left untouched.
    private static WorldCoord3 SnapToGrid(WorldCoord3 value) =>
        value with {
            Local = new FixedVector3(X: SnapToGrid(value: value.Local.X), Y: SnapToGrid(value: value.Local.Y), Z: SnapToGrid(value: value.Local.Z)),
        };
    private static FixedQ4816 SnapToGrid(FixedQ4816 value) {
        var mask = ~((1L << (FixedQ4816.FractionBitCount + RenderOriginGridLog2)) - 1L);

        return FixedQ4816.FromRawBits(value: (value.Value & mask));
    }

    private SdfProgram BuildProgram(WorldCoord3 renderOrigin) {
        // Author the static room RELATIVE to the render anchor (differenced in fixed point, then cast), so its float
        // coordinates stay small and precise; the per-slot player boxes ride the dynamic-transform buffer, which the
        // frame source already feeds render-relative. The room is authored in the spawn cell's local frame, so express
        // the render origin in that same frame (cell-aware delta) before subtracting.
        var origin = renderOrigin.Delta(origin: m_world.SpawnAnchor).ToVector3();
        var builder = new SdfProgramBuilder();
        var floorMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.34f, 0.36f, 0.42f)));
        var wallMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.50f, 0.46f, 0.58f)));
        var playerMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.93f, 0.52f, 0.18f)));

        // Floor: a plane whose surface sits at world y = FloorY. In render-relative space (p = world − origin) the plane
        // equation dot(p, n) + offset must still vanish there, so offset = origin.Y − FloorY (= −FloorY when origin = 0).
        _ = builder.ResetPoint().Plane(normal: Vector3.UnitY, offset: (origin.Y - m_room.FloorY), material: floorMaterial);

        // Four perimeter walls as thin tall boxes. The half-thickness is the room's single source of truth, so these
        // visual walls and the collision clamp planes (FixedRoom.From) stay locked — a body rests flush against the
        // inner face drawn here.
        var wallHeight = 1.5f;
        var wallThickness = m_room.WallThickness;
        var minX = m_room.BoundsMin.X;
        var maxX = m_room.BoundsMax.X;
        var minZ = m_room.BoundsMin.Y;
        var maxZ = m_room.BoundsMax.Y;
        var midX = (0.5f * (minX + maxX));
        var midZ = (0.5f * (minZ + maxZ));
        var halfSpanX = (0.5f * (maxX - minX));
        var halfSpanZ = (0.5f * (maxZ - minZ));
        var wallCenterY = (m_room.FloorY + wallHeight);

        // Wall centers are rebased into render-relative space (− origin); the half-extents are differences, hence anchor-invariant.
        AddWall(builder: builder, center: (new Vector3(maxX, wallCenterY, midZ) - origin), halfExtents: new Vector3(wallThickness, wallHeight, halfSpanZ), material: wallMaterial);
        AddWall(builder: builder, center: (new Vector3(minX, wallCenterY, midZ) - origin), halfExtents: new Vector3(wallThickness, wallHeight, halfSpanZ), material: wallMaterial);
        AddWall(builder: builder, center: (new Vector3(midX, wallCenterY, maxZ) - origin), halfExtents: new Vector3(halfSpanX, wallHeight, wallThickness), material: wallMaterial);
        AddWall(builder: builder, center: (new Vector3(midX, wallCenterY, minZ) - origin), halfExtents: new Vector3(halfSpanX, wallHeight, wallThickness), material: wallMaterial);

        // One player box per FIXED slot, placed by its per-frame dynamic transform (active slots at the player, free
        // slots hidden below the floor). Built once — never rebuilt as players join or leave.
        for (var slot = 0; (slot < MiniActionWorld.MaxPlayers); slot++) {
            _ = builder.ResetPoint().TransformDynamic(slot: slot).Box(halfExtents: m_room.PlayerHalfExtents, round: 0.06f, material: playerMaterial);
        }

        return builder.Build();
    }
    private static void AddWall(SdfProgramBuilder builder, Vector3 center, Vector3 halfExtents, int material) {
        _ = builder.ResetPoint().Translate(offset: center).Box(halfExtents: halfExtents, round: 0f, material: material);
    }
}
