using System.Numerics;
using System.Text;
using Puck.Assets;
using Puck.Demo.Creator;
using Puck.Demo.Overworld;
using Puck.Demo.World;

namespace Puck.Demo.Town;

/// <summary>
/// Assembles "Puckton" — the flagship town: a cozy dusk-lit high-street block, authored as a
/// <c>puck.world.v1</c> world document out of the <see cref="TownBuildings"/> and <see cref="TownProps"/> creations.
/// Committed CONTENT, not code (the <c>--forge-town</c> tool regenerates it byte-identically and materializes it into
/// the runtime CAS + <c>./worlds/</c> so a run document naming <c>"world": "puckton"</c> — or the live
/// <c>world.load puckton</c> verb — can load it) — the sibling of
/// <see cref="Forge.FlagshipCreations"/>'s recipes. Deterministic by construction: every creation is byte-reproducible,
/// so its content hash is stable, so this document's bytes (placements-by-hash + the baked walk grid) are stable.
/// <para>
/// The layout is built AROUND the immersed run's four console stands, which sit fixed on the far wall
/// (<see cref="OverworldRoom.WithConsolesAndShelf"/> with 4 consoles: Z ≈ -6.6, X ∈ {-6.6, -2.2, +2.2, +6.6}). The
/// arcade façade rises behind them; the street opens toward +Z with three storefronts across it, a fountain in the
/// little plaza, and lamp rows down each side. Nothing re-homes a cabinet (no <c>cabinet:</c> role), so the walk grid
/// this builder bakes — against that same 4-console room — is byte-identical to what a live <c>world.save</c> would
/// write, and the <c>world.verify</c> save→reload byte-compare matches.
/// </para>
/// </summary>
public static class TownWorld {
    /// <summary>The town's save/load handle — a run document's <c>OverworldNode.World</c> (<c>"world": "puckton"</c>)
    /// and the <c>world.load puckton</c> console verb resolve it against <c>./worlds/</c> and the CAS store.</summary>
    public const string Handle = "puckton";

    /// <summary>The town's own creations (buildings + street props). The flagship trio (lantern-fish/crt-robot/
    /// adventurer) inhabit the town as ROAMING COMPANIONS, loaded by their committed <c>docs/examples/creations/</c>
    /// paths at scenario time — not as static placements here.</summary>
    /// <returns>Each creation as (ref name, document), in a stable order.</returns>
    public static IReadOnlyList<(string Name, CreationDocument Document)> Creations() {
        var list = new List<(string Name, CreationDocument Document)>();

        list.AddRange(collection: TownBuildings.All());
        list.AddRange(collection: TownProps.All());

        return list;
    }

    /// <summary>Builds the town world document: stores every creation into <paramref name="store"/> (so the placements
    /// resolve by content hash), assembles the placements + lights + bounds, then bakes the walk grid so the buildings
    /// block. Pure of wall-clock/RNG — the same store yields the same hashes yields the same bytes.</summary>
    /// <param name="store">The content-addressed store to land the creations in and resolve footprints against.</param>
    /// <returns>The baked, walk-gridded world document (unsaved — the caller saves/commits it).</returns>
    public static WorldDocument Build(ContentAddressedStore store) {
        ArgumentNullException.ThrowIfNull(store);

        var hashes = StoreCreations(store: store);
        var placements = BuildPlacements(hashes: hashes);
        var lights = BuildLights();
        // A tight, dense BLOCK — everything within a moderate camera distance so the SDF VM renders it whole (a
        // sprawling lot pushes far geometry past the reveal overview's reliable range). Encloses the four console
        // stands (Z ≈ -6.6, X ∈ ±6.6) with margin, so the tick-0 load clamps no seated player.
        var bounds = new WorldBoundsDocument(FloorY: 0f, MaxX: 9f, MaxZ: 7f, MinX: -9f, MinZ: -9f);

        // The optional collections we don't author are EMPTY lists, not null — matching the shape WorldDocumentStore's
        // Normalize produces on load, so the saved bytes round-trip byte-identically (a null would reload as [] and
        // break the bit-for-bit proof). MovementLock stays null (free) and passes through Normalize unchanged.
        var raw = new WorldDocument(
            Bounds: bounds,
            Cameras: [],
            Lights: lights,
            MovementLock: null,
            Name: Handle,
            Placements: placements,
            Schema: WorldDocument.CurrentSchema,
            Terrain: [],
            WalkGrid: null,
            WalkOverrides: [],
            Wiring: []
        );

        // Bake the walk grid against the SAME room the immersed run resolves to (4 console stands, no shelf) so the
        // committed grid equals a live save's — buildings become blockers, the open street and plaza stay walkable,
        // and the world.verify save→reload byte-compare matches. The stands double as blockers too (belt-and-suspenders with the sim's own
        // FixedConsole boxes), exactly as BakeWorldForSave does at runtime.
        var room = OverworldRoom.WithConsolesAndShelf(consoleCount: 4, shelfCount: 0);

        return WorldWalkGridBake.Bake(document: raw, room: room, store: store, walkGridKind: "square");
    }

    // Lands every creation's canonical bytes in the store (Put + a 'creations' ref, exactly as CreationStore.Save's CAS
    // half does) and returns each ref name's content hash — the placement identity.
    private static Dictionary<string, string> StoreCreations(ContentAddressedStore store) {
        var hashes = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var (name, document) in Creations()) {
            var hash = store.Put(content: Encoding.UTF8.GetBytes(s: CreationStore.ToJson(document: document)));

            store.SetRef(category: "creations", name: name, hash: hash);
            hashes[name] = hash;
        }

        return hashes;
    }

    // The authored block: arcade behind the cabinets, three storefronts across the street, a fountain plaza, lamp rows
    // down each side, and scattered street furniture. Positions are floor-plane XZ (Y sinks each stamp so its base
    // meets the floor); scales lift the compact bench-authored creations to street size.
    private static List<PlacementDocument> BuildPlacements(IReadOnlyDictionary<string, string> hashes) {
        var placements = new List<PlacementDocument>();
        var nextId = 0;

        void Place(string name, float x, float y, float z, float scale, float yaw, PlacementRepeatDocument? repeat = null) {
            placements.Add(item: new PlacementDocument(
                Id: nextId++,
                Mirror: null,
                Name: name,
                Pattern: null,
                Position: new Vector3(x, y, z),
                Repeat: repeat,
                Role: null,
                Scale: scale,
                Source: hashes[name],
                YawDegrees: yaw
            ));
        }

        // The arcade — the hero, behind the four cabinets (Z ≈ -6.6), its glowing marquee facing the street (+Z).
        // Base sits at local Y≈0.16; at S=3.0 it floats ≈0.48, so y≈-0.48 seats it.
        Place(name: "town-arcade", scale: 3.0f, x: 0f, y: -0.48f, yaw: 0f, z: -8f);

        // Three storefronts lining the SIDES of the street, spread with gaps, façades (authored toward local +Z, which
        // yaw 0 turns toward the camera) angled INWARD toward the open center channel so the eye runs clean down the
        // middle (fountain → arcade) yet still reads each shopfront. cottage-a + cottage-b down the left, grocery on the
        // right. Base local Y≈0.20–0.25; the small negative y seats them on the floor at these scales.
        Place(name: "town-cottage-a", scale: 1.95f, x: -7f, y: -0.42f, yaw: 55f, z: 2.8f);
        Place(name: "town-cottage-b", scale: 2.1f, x: -6.5f, y: -0.38f, yaw: 25f, z: -4f);
        // yaw 320 == -40 (NormalizeYaw wraps negatives on load, so author the positive form or the saved bytes won't
        // round-trip byte-identically — the forge's bit-for-bit doctrine catches an authored negative).
        Place(name: "town-grocery", scale: 2.3f, x: 7f, y: -0.4f, yaw: 320f, z: 0.5f);

        // The fountain — the little plaza's centerpiece, mid-street, sitting in the open center channel.
        // Base local Y≈0.35; at S=1.7 it floats ≈0.6, so y≈-0.6 seats the basin on the floor.
        Place(name: "town-fountain", scale: 1.7f, x: 0f, y: -0.6f, yaw: 0f, z: 0f);

        // Two lamp rows down each sidewalk — the stamp-many hero: one placement, four copies, one segment each.
        // Base local Y≈0.26; at S=1.5 it floats ≈0.39, so y≈-0.39 seats each post. Rows hug the street edges (X≈±5)
        // so they frame the open center without crowding it.
        Place(name: "town-lamp", scale: 1.5f, x: -5f, y: -0.39f, yaw: 0f, z: -5.5f, repeat: new PlacementRepeatDocument(CountX: 1, CountZ: 4, SpacingX: 0f, SpacingZ: 3.5f));
        Place(name: "town-lamp", scale: 1.5f, x: 5f, y: -0.39f, yaw: 0f, z: -5.5f, repeat: new PlacementRepeatDocument(CountX: 1, CountZ: 4, SpacingX: 0f, SpacingZ: 3.5f));

        // Corner trees softening the four corners of the lot (base local Y≈0.15; at S=2.0 float ≈0.3).
        Place(name: "town-tree", scale: 2.0f, x: -8f, y: -0.3f, yaw: 0f, z: -8f);
        Place(name: "town-tree", scale: 2.0f, x: 8f, y: -0.3f, yaw: 25f, z: -8f);
        Place(name: "town-tree", scale: 2.0f, x: -8f, y: -0.3f, yaw: 200f, z: 6f);
        Place(name: "town-tree", scale: 2.0f, x: 8f, y: -0.3f, yaw: 110f, z: 6f);

        // Benches flanking the fountain (base local Y≈0.18; at S=1.4 float ≈0.25), plus planters and a mailbox for
        // lived-in detail (planter base ≈0.35, mailbox base ≈0.17).
        Place(name: "town-bench", scale: 1.4f, x: -2.6f, y: -0.25f, yaw: 90f, z: 0f);
        Place(name: "town-bench", scale: 1.4f, x: 2.6f, y: -0.25f, yaw: 270f, z: 0f);
        Place(name: "town-planter", scale: 1.4f, x: -3f, y: -0.49f, yaw: 0f, z: 3.5f);
        Place(name: "town-planter", scale: 1.4f, x: 3f, y: -0.49f, yaw: 0f, z: 3.5f);
        Place(name: "town-mailbox", scale: 1.4f, x: 4.5f, y: -0.24f, yaw: 210f, z: 4f);

        return placements;
    }

    // A warm amber emitter at each lamp head (mirrors the two BuildPlacements lamp rows), so dusk reads as a lit
    // street. Presentation-only — lights never touch the sim.
    private static List<WorldLightDocument> BuildLights() {
        var lights = new List<WorldLightDocument>();
        var glow = new Vector3(1.0f, 0.72f, 0.38f);

        // A warm amber emitter at each lamp head (mirrors the two BuildPlacements lamp rows at X≈±5, four down Z),
        // so dusk reads as a lit street. Lamp head sits at ≈2.7×1.5 - 0.39 ≈ 3.6 world; put the light a touch below.
        foreach (var rowX in new[] { -5f, 5f }) {
            for (var index = 0; (index < 4); index++) {
                var z = (-5.5f + (index * 3.5f));

                lights.Add(item: new WorldLightDocument(Color: glow, Intensity: 1.8f, Position: new Vector3(rowX, 3.4f, z)));
            }
        }

        // A warm wash on the arcade façade so the hero and the cabinets in front of it read clearly.
        lights.Add(item: new WorldLightDocument(Color: new Vector3(1.0f, 0.66f, 0.34f), Intensity: 1.9f, Position: new Vector3(0f, 3.2f, -5.5f)));

        // A soft, high warm fill over the plaza so the street floor and the shopfronts read at dusk (not black),
        // without flattening the mood — kept gentle and warm-tinted so lamp pools still pop.
        lights.Add(item: new WorldLightDocument(Color: new Vector3(0.92f, 0.64f, 0.42f), Intensity: 1.3f, Position: new Vector3(0f, 5.5f, 1.5f)));

        // A second low warm fill toward the back so the shopfronts flanking the arcade and the far corner don't sink
        // into black — dusk, but a LIT dusk street.
        lights.Add(item: new WorldLightDocument(Color: new Vector3(0.88f, 0.6f, 0.4f), Intensity: 0.9f, Position: new Vector3(0f, 4.5f, -4f)));

        // A cool fill at the fountain so the plaza centerpiece and open channel never fall fully dark between lamps.
        lights.Add(item: new WorldLightDocument(Color: new Vector3(0.55f, 0.72f, 0.92f), Intensity: 1.2f, Position: new Vector3(0f, 2.0f, 0f)));

        return lights;
    }
}
