using System.Numerics;

namespace Puck.Demo.Overworld;

/// <summary>
/// The static collision world for the overworld: a flat floor, an axis-aligned rectangular wall boundary, the player
/// avatar's box half-extents (so the body resolves against the surfaces with its own size), and the console stands the
/// player walks up to and boots. Pure data — the visual SDF version of these same surfaces is built once by the frame
/// source, and the fixed-point collision surfaces are derived once by <c>FixedRoom.From</c>.
/// </summary>
public sealed record OverworldRoom {
    /// <summary>The floor plane height (world Y); the player rests with its lower face on it.</summary>
    public float FloorY { get; init; } = 0f;
    /// <summary>The minimum XZ corner of the inner wall boundary (the player's box stops flush against it).</summary>
    public Vector2 BoundsMin { get; init; } = new(-8f, -8f);
    /// <summary>The maximum XZ corner of the inner wall boundary.</summary>
    public Vector2 BoundsMax { get; init; } = new(8f, 8f);
    /// <summary>The player avatar box half-extents (x = half width, y = half height, z = half depth).</summary>
    public Vector3 PlayerHalfExtents { get; init; } = new(0.35f, 0.5f, 0.35f);
    /// <summary>The half-thickness of the perimeter walls, whose boxes are CENTERED on <see cref="BoundsMin"/>/
    /// <see cref="BoundsMax"/>. The single source of truth shared by the collision planes (<see cref="OverworldRoom"/>
    /// → <c>FixedRoom</c>) and the visual walls (the frame source), so a body rests flush against a wall's INNER face
    /// (<c>bound ∓ WallThickness</c>) rather than burying its edge to the wall centerline.</summary>
    public float WallThickness { get; init; } = 0.3f;
    /// <summary>The console stands, in console-index order (the same order as the run document's console list). Each is
    /// a solid obstacle the body resolves against and a boot target the interact action fires at.</summary>
    public IReadOnlyList<ConsoleStand> Consoles { get; init; } = [];
    /// <summary>How close (per XZ axis, world units) a player's center must be to a stand's center for the interact
    /// action to boot it.</summary>
    public float ConsoleInteractRange { get; init; } = 1.8f;
    /// <summary>The cartridge shelf slots, in library-index order (the same order as the run document's library list).
    /// Each is a solid obstacle and a pick-up target, the shelf's mirror of <see cref="Consoles"/>.</summary>
    public IReadOnlyList<ShelfSlot> Shelf { get; init; } = [];
    /// <summary>How close (per XZ axis, world units) a player's center must be to a shelf slot's center for the
    /// interact action to pick up its cartridge — the shelf's mirror of <see cref="ConsoleInteractRange"/>.</summary>
    public float ShelfInteractRange { get; init; } = 1.8f;

    /// <summary>The default 16×16 walled room with a 0.7×1.0×0.7 player box and three console stands along the far wall.</summary>
    public static OverworldRoom Default { get; } = WithConsoles(count: 3);

    /// <summary>A room with <paramref name="count"/> console stands spaced evenly along the far (−Z) wall — the shape a
    /// run document's console list resolves to. Zero consoles is the bare room.</summary>
    /// <param name="count">The console-stand count (the run document validates 0..<see cref="Puck.Scene.OverworldNode.MaxConsoles"/>).</param>
    /// <returns>The authored room.</returns>
    public static OverworldRoom WithConsoles(int count) {
        return WithConsolesAndShelf(consoleCount: count, shelfCount: 0);
    }

    /// <summary>A room with <paramref name="consoleCount"/> console stands along the far (−Z) wall and
    /// <paramref name="shelfCount"/> shelf slots evenly spaced along the west (−X) wall — a side wall, so the shelf
    /// stays inside the room camera's stand-facing frustum — the shape a run document's console list + library
    /// resolves to. Zero of either is that side left bare.</summary>
    /// <param name="consoleCount">The console-stand count (the run document validates 0..<see cref="Puck.Scene.OverworldNode.MaxConsoles"/>).</param>
    /// <param name="shelfCount">The shelf-slot count (the run document validates 0..<see cref="Puck.Scene.CartridgeSource.MaxEntries"/>).</param>
    /// <returns>The authored room.</returns>
    public static OverworldRoom WithConsolesAndShelf(int consoleCount, int shelfCount) {
        var room = new OverworldRoom();

        if (consoleCount > 0) {
            // Spaced evenly along the far wall, snug enough to leave the room's center open; a stand sits just off the
            // wall's inner face so the visual pedestal never intersects the wall boxes. Up to three stands keep the
            // historical 5-unit spacing BYTE-FOR-BYTE (the determinism gate's scripted room and the overworld state hash
            // both derive collision from these centers); a FOURTH stand cannot fit that spacing inside the ±8 bounds
            // (the outermost pedestals would bury themselves in the side walls), so a full house tightens to 4.4 —
            // outer centers at ±6.6, pedestal edges at ±7.3, clear of the side walls' inner faces at ±7.7.
            var spacing = ((consoleCount <= 3) ? 5f : 4.4f);
            var stands = new ConsoleStand[consoleCount];
            var z = (room.BoundsMin.Y + 1.4f);
            var firstX = (-0.5f * spacing * (consoleCount - 1));

            for (var index = 0; (index < consoleCount); index++) {
                stands[index] = new ConsoleStand(
                    Center: new Vector2(x: (firstX + (index * spacing)), y: z),
                    HalfExtents: new Vector3(0.7f, 0.55f, 0.45f)
                );
            }

            room = (room with { Consoles = stands });
        }

        if (shelfCount > 0) {
            const float ShelfSpacing = 2.2f; // shelf slots sit closer together than console stands (smaller footprint)

            var slots = new ShelfSlot[shelfCount];
            var x = (room.BoundsMin.X + 1.1f);
            var firstZ = (-0.5f * ShelfSpacing * (shelfCount - 1));

            for (var index = 0; (index < shelfCount); index++) {
                slots[index] = new ShelfSlot(
                    Center: new Vector2(x: x, y: (firstZ + (index * ShelfSpacing))),
                    HalfExtents: new Vector3(0.3f, 0.35f, 0.3f)
                );
            }

            room = (room with { Shelf = slots });
        }

        return room;
    }
}

/// <summary>One console stand: an axis-aligned box obstacle (its base rests on the floor) that is also the boot target
/// for the console at the same index in the run document's console list.</summary>
/// <param name="Center">The stand's center on the floor plane (world XZ).</param>
/// <param name="HalfExtents">The stand's box half-extents (x = half width, y = half height, z = half depth).</param>
public sealed record ConsoleStand(Vector2 Center, Vector3 HalfExtents);

/// <summary>One shelf slot: an axis-aligned box obstacle (its base rests on the floor) that is also the pick-up target
/// for the cartridge at the same index in the run document's library list — the shelf's mirror of
/// <see cref="ConsoleStand"/>.</summary>
/// <param name="Center">The slot's center on the floor plane (world XZ).</param>
/// <param name="HalfExtents">The slot's box half-extents (x = half width, y = half height, z = half depth).</param>
public sealed record ShelfSlot(Vector2 Center, Vector3 HalfExtents);
