using System.Numerics;
using Puck.Demo.Creator;
using Puck.Demo.Forge;
using Puck.SdfVm;

namespace Puck.Demo.Town;

/// <summary>
/// Authors the town's four building creations as <see cref="CreationDocument"/>s — committed CONTENT, not code, in
/// exactly the <see cref="FlagshipCreations"/> discipline: each recipe drives a fresh, headless
/// <see cref="CreatorScene"/> through ONLY the verbs a player has at the bench (deselect → cycle the ghost to a
/// primitive → set the exact transform/material → <see cref="CreatorScene.Place"/> → select/rename), then lifts
/// <see cref="CreatorScene.ToDocument"/>. Nothing reaches past the player-reachable vocabulary: the seven primitives,
/// the scale envelope [<see cref="CreatorScene.MinScale"/>, <see cref="CreatorScene.MaxScale"/>], the
/// <see cref="CreatorScene.PaletteSize"/> palette, and PLAIN Union placement only — hard-edged architecture reads
/// great and stays deterministic (no smooth-union/groups; a blend would demand a group and buy nothing for a
/// building). Deterministic by construction — no RNG, no wall-clock, every value a literal — so a recipe reproduces
/// its creation byte-for-byte.
///
/// Every building is authored in the SAME synthetic bench <see cref="FlagshipCreations"/> uses (a 4-unit horizontal
/// half-extent, a floor-relative vertical band centred on the origin). Two clamps bound the geometry and the recipes
/// respect BOTH: a shape's centre clamps into the bench (X/Z within ±4, Y within [0.35, 3.0]), and a shape's per-axis
/// SCALE clamps to [0.2, 3.0] — so with the Box primitive's ~0.34u unit half-extent, a single box's half-extent lands
/// in roughly [0.068, 1.02]. The buildings are therefore authored COMPACT (widest box ~1.0u half): the arcade spans
/// ~2u, the cottages ~1.1u, and the world assembly scales the whole creation up to street size later. Each building's
/// base shapes are its lowest (centres near the bench floor) so it stands ON the ground when placed, and each carries
/// its OWN coherent 16-slot palette drawn from the dusk high-street family (warm brick-red, cream stucco, wood-brown,
/// sage/teal roof, slate gray, a warm window-glow, a neon accent).
/// </summary>
internal static class TownBuildings {
    /// <summary>The synthetic authoring bench every recipe builds against — the same proportions the live room derives
    /// (4-unit horizontal half-extent, a floor-relative vertical band), centred on the origin since a recipe has no
    /// room to sit inside.</summary>
    private static WorkbenchRegion Bench => new(Center: Vector3.Zero, HalfExtent: 4f, MinY: 0.35f, MaxY: 3.0f);

    // The Box primitive's canonical half-extent at unit scale — kept in lockstep with AvatarDefinition.BoxHalfExtents
    // so a requested half-extent maps to the scale that draws exactly that size (scale = half / this).
    private const float BoxUnitHalfExtent = 0.34f;

    // ---- the town's shared palette language (dusk high street) -----------------------------------------------------
    // Every slot is a linear-RGB albedo (+ optional emissive glow); each building picks the slots it needs from this
    // family so the whole block reads as one place. Emissive is a SCALAR strength (albedo * emissive adds glow — see
    // SdfMaterial), so a "lit" surface pairs a warm albedo with a positive emissive.

    private static SdfMaterial BrickRed => new(Albedo: new Vector3(0.52f, 0.20f, 0.16f));
    private static SdfMaterial BrickDeep => new(Albedo: new Vector3(0.40f, 0.15f, 0.13f));
    private static SdfMaterial CreamStucco => new(Albedo: new Vector3(0.86f, 0.78f, 0.62f));
    private static SdfMaterial WoodBrown => new(Albedo: new Vector3(0.42f, 0.28f, 0.16f));
    private static SdfMaterial WoodDark => new(Albedo: new Vector3(0.26f, 0.17f, 0.10f));
    private static SdfMaterial SageRoof => new(Albedo: new Vector3(0.30f, 0.42f, 0.34f));
    private static SdfMaterial TealRoof => new(Albedo: new Vector3(0.16f, 0.42f, 0.44f));
    private static SdfMaterial SlateGray => new(Albedo: new Vector3(0.28f, 0.30f, 0.34f));
    private static SdfMaterial SlateLight => new(Albedo: new Vector3(0.44f, 0.46f, 0.50f));
    private static SdfMaterial DarkRecess => new(Albedo: new Vector3(0.06f, 0.06f, 0.08f));
    // The warm interior glow of a lit window / marquee — a yellow albedo that shines through the dusk ambient.
    private static SdfMaterial WindowGlow => new(Albedo: new Vector3(0.98f, 0.86f, 0.48f), Emissive: 1.5f);
    private static SdfMaterial MarqueeGlow => new(Albedo: new Vector3(1.0f, 0.72f, 0.30f), Emissive: 1.8f);
    // A cool neon accent stripe (the arcade's signature) — cyan reads as "arcade" against the warm block.
    private static SdfMaterial NeonAccent => new(Albedo: new Vector3(0.35f, 0.85f, 0.98f), Emissive: 1.7f);
    private static SdfMaterial AwningRed => new(Albedo: new Vector3(0.72f, 0.20f, 0.18f));
    private static SdfMaterial AwningCream => new(Albedo: new Vector3(0.90f, 0.86f, 0.74f));
    private static SdfMaterial CrateWood => new(Albedo: new Vector3(0.55f, 0.38f, 0.20f));

    /// <summary>The four town buildings, name-tagged for the world-assembly harness to enumerate and stamp.</summary>
    /// <returns>Each building's stable name paired with its freshly authored creation document.</returns>
    internal static (string Name, CreationDocument Document)[] All() => [
        ("town-arcade", BuildArcade()),
        ("town-grocery", BuildGrocery()),
        ("town-cottage-a", BuildCottageA()),
        ("town-cottage-b", BuildCottageB()),
    ];

    // ---- town-arcade: the hero the player emerges from -------------------------------------------------------------

    /// <summary>Builds the town-arcade document: the widest façade on the block — a slate body under a glowing marquee
    /// sign band, a dark recessed double-door entrance, two lit windows, a flat parapet roof, and a cyan neon accent
    /// stripe. The building the reveal eases the player out of, so it reads biggest and brightest. The wide wall is
    /// two side-by-side boxes (a single box caps at ~1u half), which also gives the façade a centre seam under the
    /// door.</summary>
    /// <returns>The creation document (a plain-Union stack of ~22 boxes).</returns>
    public static CreationDocument BuildArcade() {
        var scene = NewScene(name: "town-arcade");

        // Palette: slate body/gray, a warm marquee + window glow, dark recess, cyan neon, wood trim.
        SetPalette(scene: scene, entries: [
            (0, SlateGray), (1, SlateLight), (2, DarkRecess), (3, MarqueeGlow),
            (4, WindowGlow), (5, NeonAccent), (6, WoodDark), (7, SlateGray),
        ]);

        // The main body: two side-by-side slate blocks spanning ~2u — the widest footprint on the block. Centres low
        // so the base sits on the floor and grows up to the parapet.
        _ = PlaceBox(scene: scene, name: "bodyL", center: new Vector3(-0.5f, 1.06f, 0f), half: new Vector3(0.55f, 0.9f, 0.7f), material: 0);
        _ = PlaceBox(scene: scene, name: "bodyR", center: new Vector3(0.5f, 1.06f, 0f), half: new Vector3(0.55f, 0.9f, 0.7f), material: 0);

        // The flat/low roof slab + a raised parapet lip along its front edge (a thin box riding the roof line).
        _ = PlaceBox(scene: scene, name: "roof", center: new Vector3(0f, 1.98f, 0f), half: new Vector3(1.05f, 0.08f, 0.72f), material: 1);
        _ = PlaceBox(scene: scene, name: "parapet", center: new Vector3(0f, 2.08f, 0.66f), half: new Vector3(1.02f, 0.14f, 0.08f), material: 0);

        // The GLOWING MARQUEE band: a wide emissive box across the façade above the doors — the sign the eye lands on.
        _ = PlaceBox(scene: scene, name: "marquee", center: new Vector3(0f, 1.62f, 0.72f), half: new Vector3(0.9f, 0.22f, 0.1f), material: 3);
        // A slim wood frame under the marquee to seat it.
        _ = PlaceBox(scene: scene, name: "marqueeSill", center: new Vector3(0f, 1.38f, 0.72f), half: new Vector3(0.92f, 0.05f, 0.08f), material: 6);

        // The recessed double-door entrance: a dark recess box set into the façade, with a slim mullion splitting it
        // into a "double" door, and a wood threshold at the floor.
        _ = PlaceBox(scene: scene, name: "entryRecess", center: new Vector3(0f, 0.85f, 0.72f), half: new Vector3(0.4f, 0.5f, 0.1f), material: 2);
        _ = PlaceBox(scene: scene, name: "doorMullion", center: new Vector3(0f, 0.85f, 0.78f), half: new Vector3(0.03f, 0.46f, 0.05f), material: 6);
        _ = PlaceBox(scene: scene, name: "threshold", center: new Vector3(0f, 0.42f, 0.76f), half: new Vector3(0.44f, 0.05f, 0.1f), material: 6);

        // Two lit windows flanking the doors (glowing panes in slim wood frames).
        _ = PlaceBox(scene: scene, name: "winFrameL", center: new Vector3(-0.66f, 0.95f, 0.72f), half: new Vector3(0.26f, 0.28f, 0.08f), material: 6);
        _ = PlaceBox(scene: scene, name: "winGlowL", center: new Vector3(-0.66f, 0.95f, 0.78f), half: new Vector3(0.19f, 0.21f, 0.05f), material: 4);
        _ = PlaceBox(scene: scene, name: "winFrameR", center: new Vector3(0.66f, 0.95f, 0.72f), half: new Vector3(0.26f, 0.28f, 0.08f), material: 6);
        _ = PlaceBox(scene: scene, name: "winGlowR", center: new Vector3(0.66f, 0.95f, 0.78f), half: new Vector3(0.19f, 0.21f, 0.05f), material: 4);

        // The NEON accent stripe: a thin emissive cyan band running the façade just under the roof — the arcade tell.
        _ = PlaceBox(scene: scene, name: "neonStripe", center: new Vector3(0f, 1.9f, 0.72f), half: new Vector3(1.0f, 0.04f, 0.07f), material: 5);
        // Two neon uprights bracketing the door recess (vertical light rails).
        _ = PlaceBox(scene: scene, name: "neonRailL", center: new Vector3(-0.46f, 0.95f, 0.76f), half: new Vector3(0.04f, 0.42f, 0.06f), material: 5);
        _ = PlaceBox(scene: scene, name: "neonRailR", center: new Vector3(0.46f, 0.95f, 0.76f), half: new Vector3(0.04f, 0.42f, 0.06f), material: 5);

        // Corner pilasters (lighter slate) to give the wide façade some structure and stop it reading as one slab.
        _ = PlaceBox(scene: scene, name: "pilasterL", center: new Vector3(-1.02f, 1.06f, 0.66f), half: new Vector3(0.1f, 0.9f, 0.1f), material: 1);
        _ = PlaceBox(scene: scene, name: "pilasterR", center: new Vector3(1.02f, 1.06f, 0.66f), half: new Vector3(0.1f, 0.9f, 0.1f), material: 1);

        // A pair of rooftop vent boxes (small massing on the flat roof, so it doesn't read dead-flat from above).
        _ = PlaceBox(scene: scene, name: "roofVentL", center: new Vector3(-0.5f, 2.14f, -0.3f), half: new Vector3(0.18f, 0.16f, 0.18f), material: 0);
        _ = PlaceBox(scene: scene, name: "roofVentR", center: new Vector3(0.5f, 2.14f, -0.3f), half: new Vector3(0.18f, 0.16f, 0.18f), material: 0);

        return scene.ToDocument();
    }

    // ---- town-grocery: the inviting corner shop -------------------------------------------------------------------

    /// <summary>Builds the town-grocery document: a warm brick shop body under a STRIPED AWNING (two flattened boxes
    /// alternating red/cream), a big glowing shop window, a door, a hanging sign on a bracket, and three produce
    /// crates out front — cozy and inviting, a mid-width footprint.</summary>
    /// <returns>The creation document (~21 plain-Union boxes).</returns>
    public static CreationDocument BuildGrocery() {
        var scene = NewScene(name: "town-grocery");

        SetPalette(scene: scene, entries: [
            (0, BrickRed), (1, BrickDeep), (2, WoodBrown), (3, WoodDark),
            (4, WindowGlow), (5, AwningRed), (6, AwningCream), (7, CrateWood),
        ]);

        // The brick body — narrower and a touch taller than the arcade, the corner-shop silhouette.
        _ = PlaceBox(scene: scene, name: "body", center: new Vector3(0f, 1.12f, 0f), half: new Vector3(0.85f, 0.95f, 0.7f), material: 0);
        // A brick cornice course capping the wall.
        _ = PlaceBox(scene: scene, name: "cornice", center: new Vector3(0f, 2.06f, 0f), half: new Vector3(0.92f, 0.08f, 0.76f), material: 1);
        // A low parapet cap so the flat top has a lip.
        _ = PlaceBox(scene: scene, name: "parapet", center: new Vector3(0f, 2.16f, 0.66f), half: new Vector3(0.88f, 0.08f, 0.07f), material: 1);

        // The big glowing shop window — a wide emissive pane in a wood frame, the shop's warm invitation.
        _ = PlaceBox(scene: scene, name: "shopFrame", center: new Vector3(-0.28f, 0.95f, 0.7f), half: new Vector3(0.44f, 0.42f, 0.08f), material: 2);
        _ = PlaceBox(scene: scene, name: "shopGlass", center: new Vector3(-0.28f, 0.95f, 0.76f), half: new Vector3(0.36f, 0.34f, 0.05f), material: 4);
        // A mullion cross on the glass (the paned-window read).
        _ = PlaceBox(scene: scene, name: "shopMullionV", center: new Vector3(-0.28f, 0.95f, 0.8f), half: new Vector3(0.03f, 0.34f, 0.03f), material: 2);
        _ = PlaceBox(scene: scene, name: "shopMullionH", center: new Vector3(-0.28f, 0.95f, 0.8f), half: new Vector3(0.36f, 0.03f, 0.03f), material: 2);

        // The door, off to the right with a wood frame and a dark panel.
        _ = PlaceBox(scene: scene, name: "doorFrame", center: new Vector3(0.52f, 0.82f, 0.7f), half: new Vector3(0.24f, 0.5f, 0.08f), material: 3);
        _ = PlaceBox(scene: scene, name: "doorPanel", center: new Vector3(0.52f, 0.78f, 0.76f), half: new Vector3(0.17f, 0.42f, 0.05f), material: 2);

        // The STRIPED AWNING: two flattened boxes alternating red/cream across the shopfront, tilted down toward the
        // street (a roll pitch so it juts out and reads as fabric, not a shelf).
        _ = PlaceBox(scene: scene, name: "awningRed", center: new Vector3(-0.42f, 1.42f, 0.86f), half: new Vector3(0.42f, 0.05f, 0.26f), material: 5, rollDeg: 18f);
        _ = PlaceBox(scene: scene, name: "awningCream", center: new Vector3(0.4f, 1.42f, 0.86f), half: new Vector3(0.4f, 0.05f, 0.26f), material: 6, rollDeg: 18f);
        // The awning valance — a short striped skirt hanging off the front lip.
        _ = PlaceBox(scene: scene, name: "valanceRed", center: new Vector3(-0.42f, 1.24f, 1.08f), half: new Vector3(0.42f, 0.09f, 0.03f), material: 5);
        _ = PlaceBox(scene: scene, name: "valanceCream", center: new Vector3(0.4f, 1.24f, 1.08f), half: new Vector3(0.4f, 0.09f, 0.03f), material: 6);

        // A hanging sign on a bracket off the corner (a small wood plaque swinging on an arm).
        _ = PlaceBox(scene: scene, name: "signArm", center: new Vector3(0.86f, 1.62f, 0.72f), half: new Vector3(0.2f, 0.03f, 0.04f), material: 3);
        _ = PlaceBox(scene: scene, name: "signPlaque", center: new Vector3(1.02f, 1.44f, 0.72f), half: new Vector3(0.05f, 0.18f, 0.16f), material: 2);

        // Three produce crates out front (small boxes stacked/scattered by the door) — the shop's spill onto the street.
        _ = PlaceBox(scene: scene, name: "crate0", center: new Vector3(-0.7f, 0.55f, 0.95f), half: new Vector3(0.2f, 0.19f, 0.19f), material: 7);
        _ = PlaceBox(scene: scene, name: "crate1", center: new Vector3(-0.34f, 0.53f, 1.0f), half: new Vector3(0.18f, 0.17f, 0.17f), material: 7);
        _ = PlaceBox(scene: scene, name: "crate2", center: new Vector3(-0.6f, 0.86f, 0.92f), half: new Vector3(0.15f, 0.14f, 0.14f), material: 7);

        return scene.ToDocument();
    }

    // ---- town-cottage-a: the cozy cream house ---------------------------------------------------------------------

    /// <summary>Builds the town-cottage-a document: a cream-stucco cubic body under a PITCHED roof (a box rolled 45°
    /// into a gable ridge), a brick chimney, a wood door, and two warm lit windows — the cozy house, warm palette,
    /// the smallest footprint.</summary>
    /// <returns>The creation document (~15 plain-Union shapes).</returns>
    public static CreationDocument BuildCottageA() {
        var scene = NewScene(name: "town-cottage-a");

        SetPalette(scene: scene, entries: [
            (0, CreamStucco), (1, SageRoof), (2, WoodBrown), (3, WoodDark),
            (4, WindowGlow), (5, BrickRed), (6, SlateGray), (7, CreamStucco),
        ]);

        // The cubic cream body — compact, a little taller than wide.
        _ = PlaceBox(scene: scene, name: "body", center: new Vector3(0f, 0.95f, 0f), half: new Vector3(0.7f, 0.7f, 0.65f), material: 0);

        // The PITCHED roof: a box rolled 45° so its square profile presents as a diamond — the top half above the
        // wall reads as a gable ridge running the depth of the house. A touch wider than the body so the eaves
        // overhang.
        _ = PlaceBox(scene: scene, name: "roofGable", center: new Vector3(0f, 1.9f, 0f), half: new Vector3(0.6f, 0.6f, 0.78f), material: 1, rollDeg: 45f);
        // A ridge cap along the very top so the gable peak reads crisp.
        _ = PlaceBox(scene: scene, name: "ridgeCap", center: new Vector3(0f, 2.35f, 0f), half: new Vector3(0.05f, 0.05f, 0.8f), material: 6);

        // The brick chimney rising off one side of the roof.
        _ = PlaceBox(scene: scene, name: "chimney", center: new Vector3(0.42f, 2.2f, -0.3f), half: new Vector3(0.12f, 0.34f, 0.12f), material: 5);
        _ = PlaceBox(scene: scene, name: "chimneyCap", center: new Vector3(0.42f, 2.42f, -0.3f), half: new Vector3(0.15f, 0.05f, 0.15f), material: 6);

        // The wood door, centred on the façade with a threshold.
        _ = PlaceBox(scene: scene, name: "doorFrame", center: new Vector3(0f, 0.78f, 0.65f), half: new Vector3(0.2f, 0.48f, 0.08f), material: 2);
        _ = PlaceBox(scene: scene, name: "doorPanel", center: new Vector3(0f, 0.74f, 0.71f), half: new Vector3(0.14f, 0.4f, 0.05f), material: 3);

        // Two warm lit windows flanking the door (glowing panes in wood frames).
        _ = PlaceBox(scene: scene, name: "winFrameL", center: new Vector3(-0.44f, 1.0f, 0.65f), half: new Vector3(0.2f, 0.22f, 0.07f), material: 2);
        _ = PlaceBox(scene: scene, name: "winGlowL", center: new Vector3(-0.44f, 1.0f, 0.71f), half: new Vector3(0.14f, 0.16f, 0.05f), material: 4);
        _ = PlaceBox(scene: scene, name: "winFrameR", center: new Vector3(0.44f, 1.0f, 0.65f), half: new Vector3(0.2f, 0.22f, 0.07f), material: 2);
        _ = PlaceBox(scene: scene, name: "winGlowR", center: new Vector3(0.44f, 1.0f, 0.71f), half: new Vector3(0.14f, 0.16f, 0.05f), material: 4);

        // A window box / planter ledge under one window (a little cottage detail).
        _ = PlaceBox(scene: scene, name: "sillL", center: new Vector3(-0.44f, 0.76f, 0.7f), half: new Vector3(0.22f, 0.05f, 0.09f), material: 2);

        return scene.ToDocument();
    }

    // ---- town-cottage-b: the taller brick L-house -----------------------------------------------------------------

    /// <summary>Builds the town-cottage-b document: DIFFERENT from cottage-a in both palette and massing — a taller
    /// brick-red main block with a lower cream-stucco lean-to wing off one side (an L-shape), a teal HIP roof over the
    /// main block, a slate lean-to roof, a chimney, a door, and two lit windows. It never reads as a copy of A.</summary>
    /// <returns>The creation document (~17 plain-Union shapes).</returns>
    public static CreationDocument BuildCottageB() {
        var scene = NewScene(name: "town-cottage-b");

        SetPalette(scene: scene, entries: [
            (0, BrickRed), (1, TealRoof), (2, CreamStucco), (3, SlateGray),
            (4, WindowGlow), (5, WoodDark), (6, SlateLight), (7, BrickDeep),
        ]);

        // The main block: TALL and narrow brick — a clearly different silhouette from cottage-a's low cube.
        _ = PlaceBox(scene: scene, name: "body", center: new Vector3(-0.35f, 1.2f, 0f), half: new Vector3(0.6f, 0.95f, 0.6f), material: 0);

        // The teal HIP roof over the main block: a wide flattened box (the hip cap) plus a rolled ridge box for the
        // pitch — reads as a pyramidal-ish roof rather than cottage-a's simple gable.
        _ = PlaceBox(scene: scene, name: "roofHip", center: new Vector3(-0.35f, 2.24f, 0f), half: new Vector3(0.72f, 0.1f, 0.72f), material: 1);
        _ = PlaceBox(scene: scene, name: "roofRidge", center: new Vector3(-0.35f, 2.42f, 0f), half: new Vector3(0.42f, 0.42f, 0.32f), material: 1, rollDeg: 45f);

        // The lean-to WING off the right side — a lower cream-stucco box making the L; its own shallow slate roof
        // sloping away (a rolled thin box).
        _ = PlaceBox(scene: scene, name: "wing", center: new Vector3(0.6f, 0.75f, 0.12f), half: new Vector3(0.55f, 0.55f, 0.5f), material: 2);
        _ = PlaceBox(scene: scene, name: "wingRoof", center: new Vector3(0.6f, 1.42f, 0.12f), half: new Vector3(0.6f, 0.06f, 0.55f), material: 3, rollDeg: 12f);

        // A brick chimney on the tall block.
        _ = PlaceBox(scene: scene, name: "chimney", center: new Vector3(-0.7f, 2.3f, -0.25f), half: new Vector3(0.11f, 0.34f, 0.11f), material: 7);
        _ = PlaceBox(scene: scene, name: "chimneyCap", center: new Vector3(-0.7f, 2.5f, -0.25f), half: new Vector3(0.14f, 0.05f, 0.14f), material: 6);

        // The door on the tall block.
        _ = PlaceBox(scene: scene, name: "doorFrame", center: new Vector3(-0.35f, 0.78f, 0.6f), half: new Vector3(0.2f, 0.48f, 0.08f), material: 5);
        _ = PlaceBox(scene: scene, name: "doorPanel", center: new Vector3(-0.35f, 0.74f, 0.66f), half: new Vector3(0.14f, 0.4f, 0.05f), material: 7);

        // A tall lit window on the main block (upper storey — the "taller" tell) and a lit window on the wing.
        _ = PlaceBox(scene: scene, name: "winFrameTall", center: new Vector3(-0.35f, 1.5f, 0.6f), half: new Vector3(0.2f, 0.26f, 0.07f), material: 5);
        _ = PlaceBox(scene: scene, name: "winGlowTall", center: new Vector3(-0.35f, 1.5f, 0.66f), half: new Vector3(0.14f, 0.2f, 0.05f), material: 4);
        _ = PlaceBox(scene: scene, name: "winFrameWing", center: new Vector3(0.6f, 0.78f, 0.62f), half: new Vector3(0.18f, 0.18f, 0.07f), material: 5);
        _ = PlaceBox(scene: scene, name: "winGlowWing", center: new Vector3(0.6f, 0.78f, 0.68f), half: new Vector3(0.13f, 0.13f, 0.05f), material: 4);

        return scene.ToDocument();
    }

    // ---- shared authoring helpers (drive the SAME verbs the pad/console would) -------------------------------------

    // Opens a fresh headless scene, enters creator mode, names the creation, and sets the 3D-object intent — the
    // common preamble every building recipe shares.
    private static CreatorScene NewScene(string name) {
        var scene = new CreatorScene(workbench: Bench);

        scene.SetActive(active: true);
        scene.SetName(name: name);
        scene.SetIntent(intent: CreatorIntent.Object);

        return scene;
    }

    // Writes a set of palette slots via the player-reachable SetPaletteEntry verb.
    private static void SetPalette(CreatorScene scene, (int Index, SdfMaterial Material)[] entries) {
        foreach (var (index, material) in entries) {
            scene.SetPaletteEntry(index: index, material: material);
        }
    }

    // Places one named BOX given as a center + HALF-EXTENTS (the building author's natural unit), converting the
    // half-extents into the creator scale that reproduces them: scale = half / BoxUnitHalfExtent. An optional roll
    // (degrees about +Z) makes a gable/awning pitch. Plain Union, hard-edged — no blend, no group. Note: each axis
    // scale still clamps to [MinScale, MaxScale], so a requested half outside ~[0.068, 1.02]u lands on the clamp.
    private static int PlaceBox(CreatorScene scene, string name, Vector3 center, Vector3 half, int material, float rollDeg = 0f) =>
        PlaceNamed(scene: scene, name: name, type: AvatarPrimitive.Box, position: center, scale: (half / BoxUnitHalfExtent), material: material, rollDeg: rollDeg);

    // Places one named shape at an exact transform, mirroring FlagshipCreations.PlaceNamed: deselect (so the ghost is
    // the target), cycle the ghost from its always-Sphere-at-construction start to the desired primitive, set the
    // exact transform/material, place, then select-and-rename the just-placed shape (RenameSelected needs a real
    // selection, never the ghost). Returns the placed shape's id.
    private static int PlaceNamed(CreatorScene scene, string name, AvatarPrimitive type, Vector3 position, Vector3 scale, int material, float rollDeg = 0f) {
        scene.Deselect();

        var steps = (((int)type - (int)scene.GhostType) + CreatorScene.PrimitiveCount) % CreatorScene.PrimitiveCount;

        for (var step = 0; (step < steps); step++) {
            scene.CyclePrimitive(direction: 1);
        }

        _ = scene.SetTargetPosition(position: position);
        scene.SetTargetRotation(yawDegrees: 0f, pitchDegrees: 0f, rollDegrees: rollDeg);
        _ = scene.SetTargetScale(scale: scale);
        _ = scene.SetMaterialIndex(index: material);

        var placedId = scene.PlacedCount;

        scene.Place();

        _ = scene.Select(idOrName: placedId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _ = scene.RenameSelected(name: name);
        scene.Deselect();

        return placedId;
    }
}
