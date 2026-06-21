using System.Numerics;
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
    private readonly MiniActionWorld m_world;
    private readonly MiniActionRoom m_room;
    private readonly CameraDirector m_director;
    private SdfProgram? m_program;
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
    public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds) {
        m_time += deltaSeconds;

        var programChanged = (m_program is null);

        m_program ??= BuildProgram();

        var activePositions = new List<Vector3>(capacity: MiniActionWorld.MaxPlayers);

        foreach (var slot in m_world.Slots) {
            if (slot is not null) {
                activePositions.Add(item: slot.Body.Position);
            }
        }

        return new SdfFrame(
            Program: m_program,
            ProgramChanged: programChanged,
            Views: m_director.Compose(activePositions: activePositions, imageWidth: width, imageHeight: height, deltaSeconds: deltaSeconds),
            Time: m_time,
            WarpAmount: 0f
        ) {
            DynamicTransforms = m_world.DynamicTransforms(),
        };
    }

    private SdfProgram BuildProgram() {
        var builder = new SdfProgramBuilder();
        var floorMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.34f, 0.36f, 0.42f)));
        var wallMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.50f, 0.46f, 0.58f)));
        var playerMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.93f, 0.52f, 0.18f)));

        // Floor: a plane whose surface sits at y = FloorY (sdfPlane = dot(p, n) + offset, so offset = -FloorY).
        _ = builder.ResetPoint().Plane(normal: Vector3.UnitY, offset: -m_room.FloorY, material: floorMaterial);

        // Four perimeter walls as thin tall boxes.
        var wallHeight = 1.5f;
        var wallThickness = 0.3f;
        var minX = m_room.BoundsMin.X;
        var maxX = m_room.BoundsMax.X;
        var minZ = m_room.BoundsMin.Y;
        var maxZ = m_room.BoundsMax.Y;
        var midX = (0.5f * (minX + maxX));
        var midZ = (0.5f * (minZ + maxZ));
        var halfSpanX = (0.5f * (maxX - minX));
        var halfSpanZ = (0.5f * (maxZ - minZ));
        var wallCenterY = (m_room.FloorY + wallHeight);

        AddWall(builder: builder, center: new Vector3(maxX, wallCenterY, midZ), halfExtents: new Vector3(wallThickness, wallHeight, halfSpanZ), material: wallMaterial);
        AddWall(builder: builder, center: new Vector3(minX, wallCenterY, midZ), halfExtents: new Vector3(wallThickness, wallHeight, halfSpanZ), material: wallMaterial);
        AddWall(builder: builder, center: new Vector3(midX, wallCenterY, maxZ), halfExtents: new Vector3(halfSpanX, wallHeight, wallThickness), material: wallMaterial);
        AddWall(builder: builder, center: new Vector3(midX, wallCenterY, minZ), halfExtents: new Vector3(halfSpanX, wallHeight, wallThickness), material: wallMaterial);

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
