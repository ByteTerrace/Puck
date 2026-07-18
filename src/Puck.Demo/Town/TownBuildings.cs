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
///
/// Two landmarks additionally carry SIGNAGE — engraved/embossed façade lettering (the arcade's marquee reads "ARCADE",
/// the gateway arch reads "PUCKTON") — authored as a document-level <see cref="TextRunDocument"/> attached after the
/// box recipe lifts (text is not a bench primitive), which the world expands into real <see cref="SdfShapeType.Glyph"/>
/// geometry at emission via the shared font atlas. A run counts its letters against the per-stamp shape budget, so the
/// arcade traded two rooftop vents for its six-glyph sign (see <see cref="BuildArcade"/>).
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

    private static SdfMaterial BrickRed => new(Albedo: new Vector3(x: 0.52f, y: 0.20f, z: 0.16f));
    private static SdfMaterial BrickDeep => new(Albedo: new Vector3(x: 0.40f, y: 0.15f, z: 0.13f));
    private static SdfMaterial CreamStucco => new(Albedo: new Vector3(x: 0.86f, y: 0.78f, z: 0.62f));
    private static SdfMaterial DarkRecess => new(Albedo: new Vector3(x: 0.06f, y: 0.06f, z: 0.08f));
    private static SdfMaterial SageRoof => new(Albedo: new Vector3(x: 0.30f, y: 0.42f, z: 0.34f));
    private static SdfMaterial SlateGray => new(Albedo: new Vector3(x: 0.28f, y: 0.30f, z: 0.34f));
    private static SdfMaterial SlateLight => new(Albedo: new Vector3(x: 0.44f, y: 0.46f, z: 0.50f));
    private static SdfMaterial TealRoof => new(Albedo: new Vector3(x: 0.16f, y: 0.42f, z: 0.44f));
    private static SdfMaterial WoodBrown => new(Albedo: new Vector3(x: 0.42f, y: 0.28f, z: 0.16f));
    private static SdfMaterial WoodDark => new(Albedo: new Vector3(x: 0.26f, y: 0.17f, z: 0.10f));
    // The warm interior glow of a lit window / marquee — a yellow albedo that shines through the dusk ambient.
    private static SdfMaterial WindowGlow => new(Albedo: new Vector3(x: 0.98f, y: 0.86f, z: 0.48f), Emissive: 1.5f);
    private static SdfMaterial MarqueeGlow => new(Albedo: new Vector3(x: 1.0f, y: 0.72f, z: 0.30f), Emissive: 1.8f);
    // A cool neon accent stripe (the arcade's signature) — cyan reads as "arcade" against the warm block.
    private static SdfMaterial NeonAccent => new(Albedo: new Vector3(x: 0.35f, y: 0.85f, z: 0.98f), Emissive: 1.7f);
    private static SdfMaterial AwningCream => new(Albedo: new Vector3(x: 0.90f, y: 0.86f, z: 0.74f));
    private static SdfMaterial AwningRed => new(Albedo: new Vector3(x: 0.72f, y: 0.20f, z: 0.18f));
    private static SdfMaterial CrateWood => new(Albedo: new Vector3(x: 0.55f, y: 0.38f, z: 0.20f));
    // town-marquee's own stone + signature accent (distinct from the arcade's slate/cyan so the two bookend
    // landmarks never read as the same building).
    private static SdfMaterial ArchStone => new(Albedo: new Vector3(x: 0.62f, y: 0.58f, z: 0.50f));
    private static SdfMaterial ArchStoneDark => new(Albedo: new Vector3(x: 0.46f, y: 0.42f, z: 0.36f));
    private static SdfMaterial GoldAccent => new(Albedo: new Vector3(x: 0.95f, y: 0.78f, z: 0.35f), Emissive: 1.6f);

    /// <summary>The five town buildings, name-tagged for the world-assembly harness to enumerate and stamp.</summary>
    /// <returns>Each building's stable name paired with its freshly authored creation document.</returns>
    internal static (string Name, CreationDocument Document)[] All() => [
        ("town-arcade", BuildArcade()),
        ("town-grocery", BuildGrocery()),
        ("town-cottage-a", BuildCottageA()),
        ("town-cottage-b", BuildCottageB()),
        ("town-marquee", BuildMarquee()),
    ];

    // ---- town-arcade: the hero the player emerges from -------------------------------------------------------------

    /// <summary>Builds the town-arcade document: the widest façade on the block — a slate body under a glowing marquee
    /// sign band ENGRAVED with the word "ARCADE", a dark recessed double-door entrance, two lit windows, a flat parapet
    /// roof, and a cyan neon accent stripe. The building the reveal eases the player out of, so it reads biggest and
    /// brightest. The wide wall is two side-by-side boxes (a single box caps at ~1u half), which also gives the façade
    /// a centre seam under the door. SHAPE BUDGET: 18 boxes + the 6-glyph "ARCADE" run = 24, exactly
    /// <see cref="World.WorldScene.MaxShapesPerStamp"/> — the two rooftop vents this recipe carried before the sign
    /// were dropped to seat the lettering inside the budget (see the class remarks on signage).</summary>
    /// <returns>The creation document (18 plain-Union boxes + one engraved text run).</returns>
    public static CreationDocument BuildArcade() {
        var scene = NewScene(name: "town-arcade");

        // Palette: slate body/gray, a warm marquee + window glow, dark recess, cyan neon, wood trim.
        SetPalette(scene: scene, entries: [
            (0, SlateGray), (1, SlateLight), (2, DarkRecess), (3, MarqueeGlow),
            (4, WindowGlow), (5, NeonAccent), (6, WoodDark), (7, SlateGray),
        ]);

        // The main body: two side-by-side slate blocks spanning ~2u — the widest footprint on the block. Centres low
        // so the base sits on the floor and grows up to the parapet.
        _ = PlaceBox(scene: scene, name: "bodyL", center: new Vector3(x: -0.5f, y: 1.06f, z: 0f), half: new Vector3(x: 0.55f, y: 0.9f, z: 0.7f), material: 0);
        _ = PlaceBox(scene: scene, name: "bodyR", center: new Vector3(x: 0.5f, y: 1.06f, z: 0f), half: new Vector3(x: 0.55f, y: 0.9f, z: 0.7f), material: 0);

        // The flat/low roof slab + a raised parapet lip along its front edge (a thin box riding the roof line).
        _ = PlaceBox(scene: scene, name: "roof", center: new Vector3(x: 0f, y: 1.98f, z: 0f), half: new Vector3(x: 1.05f, y: 0.08f, z: 0.72f), material: 1);
        _ = PlaceBox(scene: scene, name: "parapet", center: new Vector3(x: 0f, y: 2.08f, z: 0.66f), half: new Vector3(x: 1.02f, y: 0.14f, z: 0.08f), material: 0);

        // The GLOWING MARQUEE band: a wide emissive box across the façade above the doors — the sign the eye lands on.
        _ = PlaceBox(scene: scene, name: "marquee", center: new Vector3(x: 0f, y: 1.62f, z: 0.72f), half: new Vector3(x: 0.9f, y: 0.22f, z: 0.1f), material: 3);
        // A slim wood frame under the marquee to seat it.
        _ = PlaceBox(scene: scene, name: "marqueeSill", center: new Vector3(x: 0f, y: 1.38f, z: 0.72f), half: new Vector3(x: 0.92f, y: 0.05f, z: 0.08f), material: 6);

        // The recessed double-door entrance: a dark recess box set into the façade, with a slim mullion splitting it
        // into a "double" door, and a wood threshold at the floor.
        _ = PlaceBox(scene: scene, name: "entryRecess", center: new Vector3(x: 0f, y: 0.85f, z: 0.72f), half: new Vector3(x: 0.4f, y: 0.5f, z: 0.1f), material: 2);
        _ = PlaceBox(scene: scene, name: "doorMullion", center: new Vector3(x: 0f, y: 0.85f, z: 0.78f), half: new Vector3(x: 0.03f, y: 0.46f, z: 0.05f), material: 6);
        _ = PlaceBox(scene: scene, name: "threshold", center: new Vector3(x: 0f, y: 0.42f, z: 0.76f), half: new Vector3(x: 0.44f, y: 0.05f, z: 0.1f), material: 6);

        // Two lit windows flanking the doors (glowing panes in slim wood frames).
        _ = PlaceBox(scene: scene, name: "winFrameL", center: new Vector3(x: -0.66f, y: 0.95f, z: 0.72f), half: new Vector3(x: 0.26f, y: 0.28f, z: 0.08f), material: 6);
        _ = PlaceBox(scene: scene, name: "winGlowL", center: new Vector3(x: -0.66f, y: 0.95f, z: 0.78f), half: new Vector3(x: 0.19f, y: 0.21f, z: 0.05f), material: 4);
        _ = PlaceBox(scene: scene, name: "winFrameR", center: new Vector3(x: 0.66f, y: 0.95f, z: 0.72f), half: new Vector3(x: 0.26f, y: 0.28f, z: 0.08f), material: 6);
        _ = PlaceBox(scene: scene, name: "winGlowR", center: new Vector3(x: 0.66f, y: 0.95f, z: 0.78f), half: new Vector3(x: 0.19f, y: 0.21f, z: 0.05f), material: 4);

        // The NEON accent stripe: a thin emissive cyan band running the façade just under the roof — the arcade tell.
        _ = PlaceBox(scene: scene, name: "neonStripe", center: new Vector3(x: 0f, y: 1.9f, z: 0.72f), half: new Vector3(x: 1.0f, y: 0.04f, z: 0.07f), material: 5);
        // Two neon uprights bracketing the door recess (vertical light rails).
        _ = PlaceBox(scene: scene, name: "neonRailL", center: new Vector3(x: -0.46f, y: 0.95f, z: 0.76f), half: new Vector3(x: 0.04f, y: 0.42f, z: 0.06f), material: 5);
        _ = PlaceBox(scene: scene, name: "neonRailR", center: new Vector3(x: 0.46f, y: 0.95f, z: 0.76f), half: new Vector3(x: 0.04f, y: 0.42f, z: 0.06f), material: 5);

        // Corner pilasters (lighter slate) to give the wide façade some structure and stop it reading as one slab.
        _ = PlaceBox(scene: scene, name: "pilasterL", center: new Vector3(x: -1.02f, y: 1.06f, z: 0.66f), half: new Vector3(x: 0.1f, y: 0.9f, z: 0.1f), material: 1);
        _ = PlaceBox(scene: scene, name: "pilasterR", center: new Vector3(x: 1.02f, y: 1.06f, z: 0.66f), half: new Vector3(x: 0.1f, y: 0.9f, z: 0.1f), material: 1);

        // The ENGRAVED façade sign: "ARCADE" carved into the glowing marquee band's front face (local +Z, y = 1.62,
        // face z = 0.72 + 0.1 = 0.82). Engrave = Subtraction — the glyph slab sits AT the face and recesses inward, so
        // the letters read as dark channels cut into the amber glow (never coplanar: the recess floor sits Depth below
        // the face). Dark-recess material (slot 2) reads as shadow, not glow. 6 glyphs → the budget note above.
        return WithSign(document: scene.ToDocument(), run: EngraveSign(text: "ARCADE", centre: new Vector3(x: 0f, y: 1.62f, z: 0.82f), emHeight: 0.26f, depth: 0.05f, material: 2));
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
        _ = PlaceBox(scene: scene, name: "body", center: new Vector3(x: 0f, y: 1.12f, z: 0f), half: new Vector3(x: 0.85f, y: 0.95f, z: 0.7f), material: 0);
        // A brick cornice course capping the wall.
        _ = PlaceBox(scene: scene, name: "cornice", center: new Vector3(x: 0f, y: 2.06f, z: 0f), half: new Vector3(x: 0.92f, y: 0.08f, z: 0.76f), material: 1);
        // A low parapet cap so the flat top has a lip.
        _ = PlaceBox(scene: scene, name: "parapet", center: new Vector3(x: 0f, y: 2.16f, z: 0.66f), half: new Vector3(x: 0.88f, y: 0.08f, z: 0.07f), material: 1);

        // The big glowing shop window — a wide emissive pane in a wood frame, the shop's warm invitation.
        _ = PlaceBox(scene: scene, name: "shopFrame", center: new Vector3(x: -0.28f, y: 0.95f, z: 0.7f), half: new Vector3(x: 0.44f, y: 0.42f, z: 0.08f), material: 2);
        _ = PlaceBox(scene: scene, name: "shopGlass", center: new Vector3(x: -0.28f, y: 0.95f, z: 0.76f), half: new Vector3(x: 0.36f, y: 0.34f, z: 0.05f), material: 4);
        // A mullion cross on the glass (the paned-window read).
        _ = PlaceBox(scene: scene, name: "shopMullionV", center: new Vector3(x: -0.28f, y: 0.95f, z: 0.8f), half: new Vector3(x: 0.03f, y: 0.34f, z: 0.03f), material: 2);
        _ = PlaceBox(scene: scene, name: "shopMullionH", center: new Vector3(x: -0.28f, y: 0.95f, z: 0.8f), half: new Vector3(x: 0.36f, y: 0.03f, z: 0.03f), material: 2);

        // The door, off to the right with a wood frame and a dark panel.
        _ = PlaceBox(scene: scene, name: "doorFrame", center: new Vector3(x: 0.52f, y: 0.82f, z: 0.7f), half: new Vector3(x: 0.24f, y: 0.5f, z: 0.08f), material: 3);
        _ = PlaceBox(scene: scene, name: "doorPanel", center: new Vector3(x: 0.52f, y: 0.78f, z: 0.76f), half: new Vector3(x: 0.17f, y: 0.42f, z: 0.05f), material: 2);

        // The STRIPED AWNING: two flattened boxes alternating red/cream across the shopfront, tilted down toward the
        // street (a roll pitch so it juts out and reads as fabric, not a shelf).
        _ = PlaceBox(scene: scene, name: "awningRed", center: new Vector3(x: -0.42f, y: 1.42f, z: 0.86f), half: new Vector3(x: 0.42f, y: 0.05f, z: 0.26f), material: 5, rollDeg: 18f);
        _ = PlaceBox(scene: scene, name: "awningCream", center: new Vector3(x: 0.4f, y: 1.42f, z: 0.86f), half: new Vector3(x: 0.4f, y: 0.05f, z: 0.26f), material: 6, rollDeg: 18f);
        // The awning valance — a short striped skirt hanging off the front lip.
        _ = PlaceBox(scene: scene, name: "valanceRed", center: new Vector3(x: -0.42f, y: 1.24f, z: 1.08f), half: new Vector3(x: 0.42f, y: 0.09f, z: 0.03f), material: 5);
        _ = PlaceBox(scene: scene, name: "valanceCream", center: new Vector3(x: 0.4f, y: 1.24f, z: 1.08f), half: new Vector3(x: 0.4f, y: 0.09f, z: 0.03f), material: 6);

        // A hanging sign on a bracket off the corner (a small wood plaque swinging on an arm).
        _ = PlaceBox(scene: scene, name: "signArm", center: new Vector3(x: 0.86f, y: 1.62f, z: 0.72f), half: new Vector3(x: 0.2f, y: 0.03f, z: 0.04f), material: 3);
        _ = PlaceBox(scene: scene, name: "signPlaque", center: new Vector3(x: 1.02f, y: 1.44f, z: 0.72f), half: new Vector3(x: 0.05f, y: 0.18f, z: 0.16f), material: 2);

        // Three produce crates out front (small boxes stacked/scattered by the door) — the shop's spill onto the street.
        _ = PlaceBox(scene: scene, name: "crate0", center: new Vector3(x: -0.7f, y: 0.55f, z: 0.95f), half: new Vector3(x: 0.2f, y: 0.19f, z: 0.19f), material: 7);
        _ = PlaceBox(scene: scene, name: "crate1", center: new Vector3(x: -0.34f, y: 0.53f, z: 1.0f), half: new Vector3(x: 0.18f, y: 0.17f, z: 0.17f), material: 7);
        _ = PlaceBox(scene: scene, name: "crate2", center: new Vector3(x: -0.6f, y: 0.86f, z: 0.92f), half: new Vector3(x: 0.15f, y: 0.14f, z: 0.14f), material: 7);

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
        _ = PlaceBox(scene: scene, name: "body", center: new Vector3(x: 0f, y: 0.95f, z: 0f), half: new Vector3(x: 0.7f, y: 0.7f, z: 0.65f), material: 0);

        // The PITCHED roof: a box rolled 45° so its square profile presents as a diamond — the top half above the
        // wall reads as a gable ridge running the depth of the house. A touch wider than the body so the eaves
        // overhang.
        _ = PlaceBox(scene: scene, name: "roofGable", center: new Vector3(x: 0f, y: 1.9f, z: 0f), half: new Vector3(x: 0.6f, y: 0.6f, z: 0.78f), material: 1, rollDeg: 45f);
        // A ridge cap along the very top so the gable peak reads crisp.
        _ = PlaceBox(scene: scene, name: "ridgeCap", center: new Vector3(x: 0f, y: 2.35f, z: 0f), half: new Vector3(x: 0.05f, y: 0.05f, z: 0.8f), material: 6);

        // The brick chimney rising off one side of the roof.
        _ = PlaceBox(scene: scene, name: "chimney", center: new Vector3(x: 0.42f, y: 2.2f, z: -0.3f), half: new Vector3(x: 0.12f, y: 0.34f, z: 0.12f), material: 5);
        _ = PlaceBox(scene: scene, name: "chimneyCap", center: new Vector3(x: 0.42f, y: 2.42f, z: -0.3f), half: new Vector3(x: 0.15f, y: 0.05f, z: 0.15f), material: 6);

        // The wood door, centred on the façade with a threshold.
        _ = PlaceBox(scene: scene, name: "doorFrame", center: new Vector3(x: 0f, y: 0.78f, z: 0.65f), half: new Vector3(x: 0.2f, y: 0.48f, z: 0.08f), material: 2);
        _ = PlaceBox(scene: scene, name: "doorPanel", center: new Vector3(x: 0f, y: 0.74f, z: 0.71f), half: new Vector3(x: 0.14f, y: 0.4f, z: 0.05f), material: 3);

        // Two warm lit windows flanking the door (glowing panes in wood frames).
        _ = PlaceBox(scene: scene, name: "winFrameL", center: new Vector3(x: -0.44f, y: 1.0f, z: 0.65f), half: new Vector3(x: 0.2f, y: 0.22f, z: 0.07f), material: 2);
        _ = PlaceBox(scene: scene, name: "winGlowL", center: new Vector3(x: -0.44f, y: 1.0f, z: 0.71f), half: new Vector3(x: 0.14f, y: 0.16f, z: 0.05f), material: 4);
        _ = PlaceBox(scene: scene, name: "winFrameR", center: new Vector3(x: 0.44f, y: 1.0f, z: 0.65f), half: new Vector3(x: 0.2f, y: 0.22f, z: 0.07f), material: 2);
        _ = PlaceBox(scene: scene, name: "winGlowR", center: new Vector3(x: 0.44f, y: 1.0f, z: 0.71f), half: new Vector3(x: 0.14f, y: 0.16f, z: 0.05f), material: 4);

        // A window box / planter ledge under one window (a little cottage detail).
        _ = PlaceBox(scene: scene, name: "sillL", center: new Vector3(x: -0.44f, y: 0.76f, z: 0.7f), half: new Vector3(x: 0.22f, y: 0.05f, z: 0.09f), material: 2);

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
        _ = PlaceBox(scene: scene, name: "body", center: new Vector3(x: -0.35f, y: 1.2f, z: 0f), half: new Vector3(x: 0.6f, y: 0.95f, z: 0.6f), material: 0);

        // The teal HIP roof over the main block: a wide flattened box (the hip cap) plus a rolled ridge box for the
        // pitch — reads as a pyramidal-ish roof rather than cottage-a's simple gable.
        _ = PlaceBox(scene: scene, name: "roofHip", center: new Vector3(x: -0.35f, y: 2.24f, z: 0f), half: new Vector3(x: 0.72f, y: 0.1f, z: 0.72f), material: 1);
        _ = PlaceBox(scene: scene, name: "roofRidge", center: new Vector3(x: -0.35f, y: 2.42f, z: 0f), half: new Vector3(x: 0.42f, y: 0.42f, z: 0.32f), material: 1, rollDeg: 45f);

        // The lean-to WING off the right side — a lower cream-stucco box making the L; its own shallow slate roof
        // sloping away (a rolled thin box).
        _ = PlaceBox(scene: scene, name: "wing", center: new Vector3(x: 0.6f, y: 0.75f, z: 0.12f), half: new Vector3(x: 0.55f, y: 0.55f, z: 0.5f), material: 2);
        _ = PlaceBox(scene: scene, name: "wingRoof", center: new Vector3(x: 0.6f, y: 1.42f, z: 0.12f), half: new Vector3(x: 0.6f, y: 0.06f, z: 0.55f), material: 3, rollDeg: 12f);

        // A brick chimney on the tall block.
        _ = PlaceBox(scene: scene, name: "chimney", center: new Vector3(x: -0.7f, y: 2.3f, z: -0.25f), half: new Vector3(x: 0.11f, y: 0.34f, z: 0.11f), material: 7);
        _ = PlaceBox(scene: scene, name: "chimneyCap", center: new Vector3(x: -0.7f, y: 2.5f, z: -0.25f), half: new Vector3(x: 0.14f, y: 0.05f, z: 0.14f), material: 6);

        // The door on the tall block.
        _ = PlaceBox(scene: scene, name: "doorFrame", center: new Vector3(x: -0.35f, y: 0.78f, z: 0.6f), half: new Vector3(x: 0.2f, y: 0.48f, z: 0.08f), material: 5);
        _ = PlaceBox(scene: scene, name: "doorPanel", center: new Vector3(x: -0.35f, y: 0.74f, z: 0.66f), half: new Vector3(x: 0.14f, y: 0.4f, z: 0.05f), material: 7);

        // A tall lit window on the main block (upper storey — the "taller" tell) and a lit window on the wing.
        _ = PlaceBox(scene: scene, name: "winFrameTall", center: new Vector3(x: -0.35f, y: 1.5f, z: 0.6f), half: new Vector3(x: 0.2f, y: 0.26f, z: 0.07f), material: 5);
        _ = PlaceBox(scene: scene, name: "winGlowTall", center: new Vector3(x: -0.35f, y: 1.5f, z: 0.66f), half: new Vector3(x: 0.14f, y: 0.2f, z: 0.05f), material: 4);
        _ = PlaceBox(scene: scene, name: "winFrameWing", center: new Vector3(x: 0.6f, y: 0.78f, z: 0.62f), half: new Vector3(x: 0.18f, y: 0.18f, z: 0.07f), material: 5);
        _ = PlaceBox(scene: scene, name: "winGlowWing", center: new Vector3(x: 0.6f, y: 0.78f, z: 0.68f), half: new Vector3(x: 0.13f, y: 0.13f, z: 0.05f), material: 4);

        return scene.ToDocument();
    }

    // ---- town-marquee: the "PUCKTON" gateway arch — the destination the reveal walks toward --------------------------

    /// <summary>Builds the town-marquee document: a free-standing stone gateway arch straddling the street — two
    /// pillars, capitals, a lintel with a gold accent trim, and a glowing amber marquee band lettered "PUCKTON"
    /// (EMBOSSED proud in gold) on the lintel's local +Z face. Placed at yaw 180 in <see cref="TownWorld.BuildPlacements"/>
    /// so that face turns to look back down the street toward the arcade/consoles — the destination the player sees the
    /// instant the fourth wall breaks. Its own signature (gold, not the arcade's cyan) keeps the two landmarks from
    /// reading as one building. SHAPE BUDGET: 11 boxes + the 7-glyph "PUCKTON" run = 18, well under
    /// <see cref="World.WorldScene.MaxShapesPerStamp"/>.</summary>
    /// <returns>The creation document (11 plain-Union boxes + one embossed text run).</returns>
    public static CreationDocument BuildMarquee() {
        var scene = NewScene(name: "town-marquee");

        SetPalette(scene: scene, entries: [
            (0, ArchStone), (1, ArchStoneDark), (2, MarqueeGlow), (3, GoldAccent),
        ]);

        // Two pillars flanking the street, wide apart so the player walks clean through the gap.
        _ = PlaceBox(scene: scene, name: "pillarL", center: new Vector3(x: -1.1f, y: 1.0f, z: 0f), half: new Vector3(x: 0.14f, y: 1.0f, z: 0.14f), material: 0);
        _ = PlaceBox(scene: scene, name: "pillarR", center: new Vector3(x: 1.1f, y: 1.0f, z: 0f), half: new Vector3(x: 0.14f, y: 1.0f, z: 0.14f), material: 0);
        // Capitals atop each pillar.
        _ = PlaceBox(scene: scene, name: "capL", center: new Vector3(x: -1.1f, y: 2.06f, z: 0f), half: new Vector3(x: 0.2f, y: 0.08f, z: 0.2f), material: 1);
        _ = PlaceBox(scene: scene, name: "capR", center: new Vector3(x: 1.1f, y: 2.06f, z: 0f), half: new Vector3(x: 0.2f, y: 0.08f, z: 0.2f), material: 1);

        // The lintel spanning the gap — the arch's crossbeam — with a cap course along its top.
        _ = PlaceBox(scene: scene, name: "lintel", center: new Vector3(x: 0f, y: 2.24f, z: 0f), half: new Vector3(x: 1.3f, y: 0.14f, z: 0.16f), material: 0);
        _ = PlaceBox(scene: scene, name: "lintelCap", center: new Vector3(x: 0f, y: 2.42f, z: 0f), half: new Vector3(x: 1.34f, y: 0.04f, z: 0.2f), material: 1);

        // The glowing marquee band — the "PUCKTON" sign — on the lintel's local +Z face (yaw 180 in the world
        // placement turns this face to look back down the street, toward the arcade the player emerges from).
        _ = PlaceBox(scene: scene, name: "marqueeGlow", center: new Vector3(x: 0f, y: 1.58f, z: 0.16f), half: new Vector3(x: 1.0f, y: 0.22f, z: 0.05f), material: 2);
        _ = PlaceBox(scene: scene, name: "marqueeSill", center: new Vector3(x: 0f, y: 1.32f, z: 0.16f), half: new Vector3(x: 1.02f, y: 0.04f, z: 0.06f), material: 1);

        // A gold accent trim along the lintel's underside — this arch's own signature — with two studs where it
        // meets the pillars.
        _ = PlaceBox(scene: scene, name: "accentTrim", center: new Vector3(x: 0f, y: 1.9f, z: 0.15f), half: new Vector3(x: 1.28f, y: 0.03f, z: 0.04f), material: 3);
        _ = PlaceBox(scene: scene, name: "accentStudL", center: new Vector3(x: -1.1f, y: 1.9f, z: 0.13f), half: new Vector3(x: 0.05f, y: 0.05f, z: 0.05f), material: 3);
        _ = PlaceBox(scene: scene, name: "accentStudR", center: new Vector3(x: 1.1f, y: 1.9f, z: 0.13f), half: new Vector3(x: 0.05f, y: 0.05f, z: 0.05f), material: 3);

        // The EMBOSSED marquee sign: "PUCKTON" raised proud of the amber band's front face (local +Z, y = 1.58,
        // face z = 0.16 + 0.05 = 0.21). Emboss = Union — the glyph slab sits AT the face and protrudes outward, so the
        // letters stand off the glow with real bevel lighting from the analytic normal (never coplanar: the raised top
        // sits Depth proud of the face). Gold-accent material (slot 3), this arch's own signature. 7 glyphs.
        return WithSign(document: scene.ToDocument(), run: EmbossSign(text: "PUCKTON", centre: new Vector3(x: 0f, y: 1.58f, z: 0.21f), emHeight: 0.26f, depth: 0.03f, material: 3));
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

        var steps = ((((int)type - (int)scene.GhostType) + CreatorScene.PrimitiveCount) % CreatorScene.PrimitiveCount);

        for (var step = 0; (step < steps); step++) {
            scene.CyclePrimitive(direction: 1);
        }

        _ = scene.SetTargetPosition(position: position);
        scene.SetTargetRotation(yawDegrees: 0f, pitchDegrees: 0f, rollDegrees: rollDeg);
        _ = scene.SetTargetScale(scale: scale);
        _ = scene.SetMaterialIndex(index: material);

        var placedId = scene.PlacedCount;

        scene.Place();

        _ = scene.Select(idOrName: placedId.ToString(provider: System.Globalization.CultureInfo.InvariantCulture));
        _ = scene.RenameSelected(name: name);
        scene.Deselect();

        return placedId;
    }

    // ---- signage: engraved / embossed façade lettering (a creation's TEXT-RUN authoring surface) --------------------
    // Signage is authored at the DOCUMENT level (a TextRunDocument the world expands into Glyph geometry at emission),
    // not through a CreatorScene primitive verb — text is not one of the seven bench primitives. It reaches no further
    // past the player-reachable model than the creator's own eventual text tool will: a string, a placement, a mode,
    // and a depth. The letters face the run's local +Z, so a sign authored on a building's local +Z front face turns
    // to the street with the building's placement yaw.

    // Attaches one text run to a freshly lifted creation document (the only signage each building needs today; the
    // schema carries a list, so this trivially grows to several).
    private static CreationDocument WithSign(CreationDocument document, TextRunDocument run) =>
        (document with { TextRuns = [run] });

    // An ENGRAVED run (Subtraction — a recess carved into the surface at `centre`, letters facing local +Z).
    private static TextRunDocument EngraveSign(string text, Vector3 centre, float emHeight, float depth, int material) =>
        new(Depth: depth, EmHeight: emHeight, Material: material, Mode: TextRunDocument.ModeEngrave, Position: centre, Rotation: Quaternion.Identity, Text: text);

    // An EMBOSSED run (Union — raised proud of the surface at `centre`, letters facing local +Z).
    private static TextRunDocument EmbossSign(string text, Vector3 centre, float emHeight, float depth, int material) =>
        new(Depth: depth, EmHeight: emHeight, Material: material, Mode: TextRunDocument.ModeEmboss, Position: centre, Rotation: Quaternion.Identity, Text: text);
}
