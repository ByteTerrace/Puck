using Puck.Maths;
using Puck.SdfVm.Queries;

namespace Puck.Demo.Rts;

/// <summary>
/// Defines the minimal RTS scenario used to exercise the
/// engine's emitter/view/query abstractions host something other than the overworld's platformer. Authored content
/// only — the actual per-tick unit sim state lives on <c>Puck.Demo.Overworld.OverworldWorld</c> (mirroring how the
/// deterministic garden's tree structure is authored here while its plant pool lives there); this type owns the
/// arena's terrain/blocker geometry and bakes the <see cref="IWorldQuery"/> provider the sim binds. No combat, no
/// economy, no pathfinding — straight-line move-to-target, ground-snapped via <see cref="IWorldQuery.TryGroundHeight"/>,
/// and a blocked-geometry spawn check via <see cref="IWorldQuery.Overlap"/> are the WHOLE gameplay surface.
/// </summary>
public static class RtsScenario {
    /// <summary>The arena's XZ bounds (room-local, inside the default room's ±8 walls with margin).</summary>
    public const float ArenaMinX = -6f;
    /// <summary>See <see cref="ArenaMinX"/>.</summary>
    public const float ArenaMinZ = -6f;
    /// <summary>See <see cref="ArenaMinX"/>.</summary>
    public const float ArenaMaxX = 6f;
    /// <summary>See <see cref="ArenaMinX"/>.</summary>
    public const float ArenaMaxZ = 6f;

    /// <summary>The flat arena floor's height.</summary>
    public const float GroundY = 0f;

    /// <summary>A raised dais in the arena's NE quadrant — gives <see cref="IWorldQuery.TryGroundHeight"/> a real
    /// height DIFFERENCE to prove ground-snap actually samples the query rather than a constant.</summary>
    public const float DaisMinX = 1.5f;
    /// <summary>See <see cref="DaisMinX"/>.</summary>
    public const float DaisMinZ = 1.5f;
    /// <summary>See <see cref="DaisMinX"/>.</summary>
    public const float DaisMaxX = 4.0f;
    /// <summary>See <see cref="DaisMinX"/>.</summary>
    public const float DaisMaxZ = 4.0f;
    /// <summary>See <see cref="DaisMinX"/>.</summary>
    public const float DaisTopY = 0.6f;

    /// <summary>A boulder blocker in the arena's W quadrant — gives <see cref="IWorldQuery.Overlap"/> something to
    /// actually reject a spawn against.</summary>
    public const float BoulderMinX = -4.5f;
    /// <summary>See <see cref="BoulderMinX"/>.</summary>
    public const float BoulderMinZ = -1.0f;
    /// <summary>See <see cref="BoulderMinX"/>.</summary>
    public const float BoulderMaxX = -3.3f;
    /// <summary>See <see cref="BoulderMinX"/>.</summary>
    public const float BoulderMaxZ = 1.0f;

    /// <summary>The authored terrain patches — the ground-height layer's ONLY input (also what
    /// <see cref="Rts.RtsTerrainEmitter"/> draws).</summary>
    public static readonly IReadOnlyList<WorldQueryTerrainInput> TerrainPatches = [
        new WorldQueryTerrainInput(MaxX: ArenaMaxX, MaxZ: ArenaMaxZ, MinX: ArenaMinX, MinZ: ArenaMinZ, TopY: GroundY),
        new WorldQueryTerrainInput(MaxX: DaisMaxX, MaxZ: DaisMaxZ, MinX: DaisMinX, MinZ: DaisMinZ, TopY: DaisTopY),
    ];

    /// <summary>The authored blocker rectangles — the blocked-cell layer's ONLY input.</summary>
    public static readonly IReadOnlyList<WorldQueryBlockerInput> Blockers = [
        new WorldQueryBlockerInput(MaxX: BoulderMaxX, MaxZ: BoulderMaxZ, MinX: BoulderMinX, MinZ: BoulderMinZ),
    ];

    /// <summary>Bakes the arena's <see cref="WorldQueryArtifact"/> and wraps it as a provider — called ONCE at
    /// overworld construction (see <c>OverworldFrameSource</c>) and bound onto the sim via
    /// <c>OverworldWorld.ConfigureRtsQuery</c>. In-memory only this wave (see <see cref="WorldQueryArtifact"/>'s
    /// remarks) — no document, no CAS.</summary>
    /// <returns>The baked query provider.</returns>
    public static IWorldQuery BuildQuery() {
        var artifact = WorldQueryBaker.Bake(blockers: Blockers, maxX: ArenaMaxX, maxZ: ArenaMaxZ, minX: ArenaMinX, minZ: ArenaMinZ, terrain: TerrainPatches);

        return WorldQueryProviders.ForWorld(artifact: artifact);
    }

    /// <summary>A deterministic default spawn position for <c>rts.spawn</c> called with no explicit coordinates: a
    /// 4x3 grid in the arena's S quadrant, clear of both the dais and the boulder, so every unit spawns unblocked.</summary>
    /// <param name="index">The spawn call's ordinal (not the resulting slot — a full pool still yields a stable grid).</param>
    /// <returns>The room-local XZ to spawn at.</returns>
    public static (FixedQ4816 X, FixedQ4816 Z) DefaultSpawnPosition(int index) {
        var column = (index % 4);
        var row = (index / 4);
        var x = FixedQ4816.FromDouble(value: (-1.8 + (column * 1.2)));
        var z = FixedQ4816.FromDouble(value: (-3.6 + (row * 1.2)));

        return (x, z);
    }
}
