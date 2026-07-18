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
/// <c>world.load puckton</c> verb — can load it) — the sibling of <see cref="Forge.FlagshipLanternFish"/>/
/// <see cref="Forge.FlagshipCrtRobot"/>/<see cref="Forge.FlagshipAdventurer"/>'s recipes. Deterministic by
/// construction: every creation is byte-reproducible, so its content hash is stable, so this document's bytes
/// (placements-by-hash + the baked walk grid) are stable.
/// <para>
/// The layout is built AROUND the immersed run's four console stands, which sit fixed on the far wall
/// (<see cref="OverworldRoom.WithConsolesAndShelf"/> with 4 consoles: Z ≈ -6.6, X ∈ {-6.6, -2.2, +2.2, +6.6}). The
/// arcade façade rises behind them; the street opens toward +Z with three storefronts across it, a fountain in the
/// little plaza, lamp rows down each side, and a paved road/plaza/sidewalk terrain pass underfoot (see
/// <see cref="BuildTerrain"/>). Near the lot's open +Z end the PUCKTON gateway arch (<c>town-marquee</c>) straddles
/// the street as the destination beat — the focal point the moment the fourth wall breaks. Nothing re-homes a cabinet
/// (no <c>cabinet:</c> role), so the walk grid this builder bakes — against that same 4-console room — is
/// byte-identical to what a live <c>world.save</c> would write, and the <c>world.verify</c> save→reload byte-compare
/// matches.
/// </para>
/// </summary>
public static class TownWorld {
    /// <summary>The town's save/load handle — a run document's <c>OverworldNode.World</c> (<c>"world": "puckton"</c>)
    /// and the <c>world.load puckton</c> console verb resolve it against <c>./worlds/</c> and the CAS store.</summary>
    public const string Handle = "puckton";

    /// <summary>The town's own creations (buildings + street props + the flagship companion trio). The trio
    /// (lantern-fish/crt-robot/adventurer, built from the SAME canonical recipes <c>--forge-flagships</c> verifies —
    /// see <see cref="Forge.FlagshipLanternFish"/>/<see cref="Forge.FlagshipCrtRobot"/>/<see cref="Forge.FlagshipAdventurer"/>)
    /// is stored here alongside the buildings so <see cref="BuildPlacements"/> can reference their hashes from
    /// <c>companion</c>-role placements — "inhabitants as data": the reveal lands in a town already populated, with
    /// no scenario-time <c>companion.add</c> needed (though the committed <c>docs/examples/creations/</c> copies
    /// remain the review-scenario path for iterating on a single creation).</summary>
    /// <returns>Each creation as (ref name, document), in a stable order.</returns>
    public static IReadOnlyList<(string Name, CreationDocument Document)> Creations() {
        var list = new List<(string Name, CreationDocument Document)>();

        list.AddRange(collection: TownBuildings.All());
        list.AddRange(collection: TownProps.All());
        list.Add(item: ("lantern-fish", Forge.FlagshipLanternFish.BuildLanternFish()));
        list.Add(item: ("crt-robot", Forge.FlagshipCrtRobot.BuildCrtRobot()));
        list.Add(item: ("adventurer", Forge.FlagshipAdventurer.BuildAdventurer()));

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
        var terrain = BuildTerrain();
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
            Terrain: terrain,
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

        void Place(string name, float x, float y, float z, float scale, float yaw, PlacementRepeatDocument? repeat = null, string? role = null) {
            placements.Add(item: new PlacementDocument(
                Id: nextId++,
                Mirror: null,
                Name: name,
                Pattern: null,
                Position: new Vector3(x: x, y: y, z: z),
                Repeat: repeat,
                Role: role,
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
        // lived-in detail (planter base ≈0.35, mailbox base ≈0.17). Pulled to x≈±3.1 (not ±2.6) so the walk grid's
        // player-half-extent dilation (WalkGridBaker's Step 3) leaves genuine clearance between the fountain's
        // blocked footprint and the benches' — a ±2.6 seat pinched the plaza's through-channel to a fraction of a
        // cell once dilation folded in, sealing the street shut right where a player needs to pass.
        Place(name: "town-bench", scale: 1.4f, x: -3.1f, y: -0.25f, yaw: 90f, z: 0f);
        Place(name: "town-bench", scale: 1.4f, x: 3.1f, y: -0.25f, yaw: 270f, z: 0f);
        Place(name: "town-planter", scale: 1.4f, x: -3f, y: -0.49f, yaw: 0f, z: 3.5f);
        Place(name: "town-planter", scale: 1.4f, x: 3f, y: -0.49f, yaw: 0f, z: 3.5f);
        Place(name: "town-mailbox", scale: 1.4f, x: 4.5f, y: -0.24f, yaw: 210f, z: 4f);

        // The flagship trio, declared as `companion`-role placements so CompanionRoster.SpawnFromWorld dispatches
        // them into the live roster at world load — the reveal lands in an already-populated town. Scale/yaw are
        // unused by a companion spawn (CompanionState reads only Source + Position) but stay uniform (1/0) rather
        // than null so the placement round-trips exactly like every other one. Spread around the plaza, clear of the
        // fountain basin and the walk-grid's blocked footprints.
        Place(name: "lantern-fish", scale: 1f, x: 1.6f, y: 0f, yaw: 0f, z: 1.4f, role: "companion");
        Place(name: "crt-robot", scale: 1f, x: -1.8f, y: 0f, yaw: 0f, z: -1.6f, role: "companion");
        Place(name: "adventurer", scale: 1f, x: 0f, y: 0f, yaw: 0f, z: 2.6f, role: "companion");

        // The PUCKTON gateway arch — the destination beat: near the lot's +Z open end (bounds MaxZ = 7f), straddling
        // the street channel so the player walks THROUGH it. Its base sits at local Y=0 (the pillars' own feet), so
        // no floor-seating offset is needed (y: 0f). Authored at yaw 180 (positive, wrapped — NormalizeYaw's
        // round-trip demands it) so its marquee face, authored toward local +Z, turns to look back down the street
        // at the arcade/consoles: the instant the fourth wall breaks, this is what the player sees ahead of them.
        Place(name: "town-marquee", scale: 3.0f, x: 0f, y: 0f, yaw: 180f, z: 6.2f);

        // Mid-street infill so the far reach toward the arch doesn't read bare: two planters dividing the open
        // channel partway down, then a small welcome cluster (two lamps + two benches) framing the arch itself —
        // echoing the fountain plaza's lamp/bench pairing at the OTHER end of the street. x = ±2.8 (not ±2) keeps
        // the through-channel clear once WalkGridBaker's player-half-extent dilation folds in — any tighter and
        // the planters wall off the plaza from the rest of the street.
        Place(name: "town-planter", scale: 1.4f, x: -2.8f, y: -0.49f, yaw: 0f, z: 2.6f);
        Place(name: "town-planter", scale: 1.4f, x: 2.8f, y: -0.49f, yaw: 0f, z: 2.6f);
        Place(name: "town-lamp", scale: 1.5f, x: -3.9f, y: -0.39f, yaw: 0f, z: 6.3f);
        Place(name: "town-lamp", scale: 1.5f, x: 3.9f, y: -0.39f, yaw: 0f, z: 6.3f);
        Place(name: "town-bench", scale: 1.4f, x: -2.2f, y: -0.25f, yaw: 90f, z: 5.6f);
        Place(name: "town-bench", scale: 1.4f, x: 2.2f, y: -0.25f, yaw: 270f, z: 5.6f);

        return placements;
    }

    // Road/plaza/sidewalk terrain patches: flat slabs whose top sits at Center.Y = FloorY (0) with a small
    // HalfExtents.Y (0.05, mirroring WorldSculptController.AddTerrainWithHistory's own live-authoring default) —
    // just enough rise to clear the floor plane's surface and render (a patch buried at y < FloorY is provably
    // invisible under this engine's sphere-tracing: the floor is an infinite solid half-space below y = FloorY, so
    // nothing beneath it is ever the nearest surface). Walkable BY CONSTRUCTION, no WalkOverride: WorldWalkGridBake
    // deliberately never feeds terrain into the blocking pass (terrain is dressing, not an obstacle — see its
    // remark). Road and plaza share WorldPalette slot 0 ("road/plaza gray" — the palette's own naming), so the two
    // freely overlap at the plaza transition with no seam; sidewalks use slot 2 ("dirt/path tan") to read distinct
    // from the road underfoot.
    private static List<TerrainPatchDocument> BuildTerrain() {
        var terrain = new List<TerrainPatchDocument>();
        const float FloorY = 0f;
        const float RiseHalf = 0.05f;

        void Slab(string kind, float centerX, float centerZ, float halfX, float halfZ, int material) {
            terrain.Add(item: new TerrainPatchDocument(
                Center: new Vector3(x: centerX, y: FloorY, z: centerZ),
                HalfExtents: new Vector3(x: halfX, y: RiseHalf, z: halfZ),
                Kind: kind,
                Material: material
            ));
        }

        // The paved road down the center channel — two segments (the plaza slab below fills the gap between them,
        // sharing the same material so the seam is invisible) so no single patch spans the whole lot.
        Slab(kind: "slab", centerX: 0f, centerZ: -3.5f, halfX: 1.7f, halfZ: 3.5f, material: 0);
        Slab(kind: "slab", centerX: 0f, centerZ: 3.4f, halfX: 1.7f, halfZ: 3.2f, material: 0);

        // The plaza ring under the fountain — wider than the road, so from above the street visibly WIDENS into a
        // little square exactly where the fountain and its flanking benches sit.
        Slab(kind: "plaza", centerX: 0f, centerZ: 0.3f, halfX: 3.0f, halfZ: 3.3f, material: 0);

        // Sidewalk strips beneath each building's front (dirt/path tan), so the storefronts read as sitting on a
        // walkway rather than bare lot.
        Slab(kind: "slab", centerX: 0f, centerZ: -6.8f, halfX: 2.2f, halfZ: 1.0f, material: 2);   // in front of the arcade
        Slab(kind: "slab", centerX: -6.8f, centerZ: 2.8f, halfX: 1.4f, halfZ: 1.8f, material: 2);  // cottage-a's frontage
        Slab(kind: "slab", centerX: -6.5f, centerZ: -4.0f, halfX: 1.6f, halfZ: 1.6f, material: 2); // cottage-b's frontage
        Slab(kind: "slab", centerX: 6.8f, centerZ: 0.6f, halfX: 1.8f, halfZ: 1.8f, material: 2);   // grocery's frontage

        // The landing pad under the PUCKTON arch — the destination gets its own little plaza too.
        Slab(kind: "plaza", centerX: 0f, centerZ: 6.1f, halfX: 2.4f, halfZ: 0.7f, material: 0);

        return terrain;
    }

    // A warm amber emitter at each lamp head (mirrors the two BuildPlacements lamp rows), so dusk reads as a lit
    // street. Presentation-only — lights never touch the sim.
    private static List<WorldLightDocument> BuildLights() {
        var lights = new List<WorldLightDocument>();
        var glow = new Vector3(x: 1.0f, y: 0.72f, z: 0.38f);

        // A warm amber emitter at each lamp head (mirrors the two BuildPlacements lamp rows at X≈±5, four down Z),
        // so dusk reads as a lit street. Lamp head sits at ≈2.7×1.5 - 0.39 ≈ 3.6 world; put the light a touch below.
        foreach (var rowX in new[] { -5f, 5f }) {
            for (var index = 0; (index < 4); index++) {
                var z = (-5.5f + (index * 3.5f));

                lights.Add(item: new WorldLightDocument(Color: glow, Intensity: 1.8f, Position: new Vector3(x: rowX, y: 3.4f, z: z)));
            }
        }

        // A warm wash on the arcade façade so the hero and the cabinets in front of it read clearly.
        lights.Add(item: new WorldLightDocument(Color: new Vector3(x: 1.0f, y: 0.66f, z: 0.34f), Intensity: 1.9f, Position: new Vector3(x: 0f, y: 3.2f, z: -5.5f)));

        // A soft, high warm fill over the plaza so the street floor and the shopfronts read at dusk (not black),
        // without flattening the mood — kept gentle and warm-tinted so lamp pools still pop.
        lights.Add(item: new WorldLightDocument(Color: new Vector3(x: 0.92f, y: 0.64f, z: 0.42f), Intensity: 1.3f, Position: new Vector3(x: 0f, y: 5.5f, z: 1.5f)));

        // A second low warm fill toward the back so the shopfronts flanking the arcade and the far corner don't sink
        // into black — dusk, but a LIT dusk street.
        lights.Add(item: new WorldLightDocument(Color: new Vector3(x: 0.88f, y: 0.6f, z: 0.4f), Intensity: 0.9f, Position: new Vector3(x: 0f, y: 4.5f, z: -4f)));

        // A cool fill at the fountain so the plaza centerpiece and open channel never fall fully dark between lamps.
        lights.Add(item: new WorldLightDocument(Color: new Vector3(x: 0.55f, y: 0.72f, z: 0.92f), Intensity: 1.2f, Position: new Vector3(x: 0f, y: 2.0f, z: 0f)));

        return lights;
    }
}
