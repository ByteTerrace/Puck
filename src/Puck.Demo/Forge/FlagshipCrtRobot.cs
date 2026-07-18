using System.Globalization;
using System.Numerics;
using Puck.Demo.Creator;
using Puck.SdfVm;

namespace Puck.Demo.Forge;

/// <summary>
/// Authors "Pixel" — Arc 3's CRT-faced robot flagship — as a <see cref="CreationDocument"/>, built the same way a
/// player would: a fresh headless <see cref="CreatorScene"/> driven through EXACTLY the bench verbs
/// (<see cref="CreatorScene.Place"/>, <see cref="CreatorScene.SetPaletteEntry"/>, <see cref="CreatorScene.DefineChain"/>,
/// <see cref="CreatorScene.RecordFrame"/>, goal sweeps), then lifted via <see cref="CreatorScene.ToDocument"/>. Nothing
/// here reaches past the player-reachable vocabulary: the seven primitives (Sphere/Box/Torus/Cylinder/Capsule/Ellipsoid/
/// RoundCone), the scale envelope [<see cref="CreatorScene.MinScale"/>, <see cref="CreatorScene.MaxScale"/>], the
/// <see cref="CreatorScene.Capacity"/> shape ceiling, the <see cref="CreatorScene.PaletteSize"/> palette, and chains as
/// the RIG page defines them. Deterministic by construction — no RNG, no wall-clock, every value a literal — so
/// re-running the recipe reproduces <c>docs/examples/creations/crt-robot.creation.json</c> byte-for-byte.
///
/// THE COLOR STORY — warm cream shell above, charcoal-slate below, one saturated ORANGE accent. Cream up top (where
/// the face lives) reads friendly and light; the charcoal lower body/legs ground the silhouette — the two-tone of a
/// warm vintage television set. Orange is the "recording light" identity color: the antenna's glowing tip, the chest
/// emblem, and the CRT's emissive bezel rim all carry it, echoing the anglerfish lure bulb the sibling flagship wears.
///
/// THE SILHOUETTE — boxy, chunky, stubby: a big rounded cream head-unit on a smaller charcoal torso, short thick
/// two-bone arms ending in orange mitten hands, short thick legs ending in chunky feet, a tiny antenna crowning it.
/// The FACE is a screen: a near-black slightly-inset slab (a small Onion shell suggests the scanline glass) framed by
/// a warm orange emissive bezel, wearing two oversized eyes (white sclera + dark pupil + a white SHINE dot) so it
/// reads as a friendly little CRT even before any live feed lights it — the live-feed wiring is the host's, this makes
/// the face READ on its own.
///
/// The structural contract the forge's flagship battery asserts is preserved: four named limb chains (armLeft/armRight/
/// legLeft/legRight, each a 3-shape/2-bone "limb"), and a shape named "face". Idle and greet-wave emotes land as
/// frames 1-2 (companion frame interpolation blends between them smoothly at replay).
/// </summary>
internal static class FlagshipCrtRobot {
    /// <summary>The synthetic authoring bench the recipe builds against — the same proportions
    /// <c>OverworldFrameSource</c> derives for the live room, centred on the origin.</summary>
    private static WorkbenchRegion Bench => new(Center: Vector3.Zero, HalfExtent: 4f, MinY: 0.35f, MaxY: 3.0f);

    // ---- the color story, as palette slots (authored, not the default hue sweep) ---------------------------------
    private const int MatShellCream = 0; // the head-unit + upper shell: warm cream
    private const int MatBodySlate = 1; // the torso + lower body: dusty slate-blue (lifted so it never reads as black)
    private const int MatAccentOrange = 2; // the identity accent: mitten hands, emblem core, antenna tip base
    private const int MatScreenDark = 3; // the CRT glass: near-black, faint cool tint
    private const int MatBezelGlow = 4; // the CRT bezel + emblem ring + antenna tip: emissive warm orange
    private const int MatEyeWhite = 5; // the eye sclera + the shine dot: bright warm white
    private const int MatEyeDark = 6; // the pupils: deep charcoal
    private const int MatFootSlate = 7; // the feet: a darker slate than the body, to plant the stance

    /// <summary>Builds the crt-robot document. See the type remarks for the color story and silhouette.</summary>
    /// <returns>The normalized document, ready for <see cref="CreationStore.ToJson"/> or the bake.</returns>
    public static CreationDocument BuildCrtRobot() {
        var scene = new CreatorScene(workbench: Bench);

        scene.SetActive(active: true);
        scene.SetName(name: "crt-robot");
        scene.SetIntent(intent: CreatorIntent.Object);

        PaintPalette(scene: scene);

        // ---- the body: a chunky charcoal torso, slightly tapered toward the hips via a RoundCone skirt ----------
        // The torso is a wide slate box — broad shoulders, stubby depth, reaching low enough that the hips close over
        // the leg roots (no gap between the legs). A small rounded CREAM chest plate sits flush high on its FRONT — a
        // vintage-appliance faceplate that echoes the cream head without ballooning into an apron; the orange emblem
        // badge rides on it.
        _ = PlaceNamed(scene: scene, name: "torso", type: AvatarPrimitive.Box, position: new Vector3(x: 0f, y: 1.2f, z: 0f), scale: new Vector3(x: 1.08f, y: 1.1f, z: 0.68f), material: MatBodySlate);
        _ = PlaceNamed(scene: scene, name: "chestPlate", type: AvatarPrimitive.Box, position: new Vector3(x: 0f, y: 1.4f, z: 0.32f), scale: new Vector3(x: 0.66f, y: 0.5f, z: 0.1f), material: MatShellCream, onion: 0f);
        // A squat slate hip block bridging the legs — closes the sightline through the crotch (otherwise the room's
        // teal-lit floor peeks between the legs) and gives the stance a solid, planted base.
        _ = PlaceNamed(scene: scene, name: "hips", type: AvatarPrimitive.Box, position: new Vector3(x: 0f, y: 0.72f, z: 0f), scale: new Vector3(x: 0.94f, y: 0.44f, z: 0.66f), material: MatBodySlate);

        // The chest emblem: a small emissive orange "power" badge high on the chest (the accent-carries-identity move).
        // A thin bezel-glow torus rim around a shallow orange dome — kept small and raised so it reads as a badge over
        // the heart, never a mouth or navel.
        _ = PlaceNamed(scene: scene, name: "emblemRing", type: AvatarPrimitive.Torus, position: new Vector3(x: 0f, y: 1.4f, z: 0.38f), rotation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitX, angle: (0.5f * MathF.PI)), scale: new Vector3(x: 0.28f, y: 0.28f, z: 0.28f), material: MatBezelGlow);
        _ = PlaceNamed(scene: scene, name: "emblemCore", type: AvatarPrimitive.Sphere, position: new Vector3(x: 0f, y: 1.4f, z: 0.4f), scale: new Vector3(x: 0.2f, y: 0.2f, z: 0.16f), material: MatAccentOrange);

        // ---- the head-unit: a big rounded cream box, the friendly warm-TV volume ---------------------------------
        // Deeper than before so the screen can inset INTO it (the cream shows as a generous faceplate border, not a
        // thin edge). Slightly rounded via a small Onion shell so it reads as a warm appliance, not a hard crate.
        _ = PlaceNamed(scene: scene, name: "head", type: AvatarPrimitive.Box, position: new Vector3(x: 0f, y: 2.14f, z: -0.06f), scale: new Vector3(x: 1.32f, y: 1.14f, z: 0.9f), material: MatShellCream, onion: 0f);

        // A thin cream "neck" collar so the head doesn't float — a short wide cylinder bridging torso→head.
        _ = PlaceNamed(scene: scene, name: "neck", type: AvatarPrimitive.Cylinder, position: new Vector3(x: 0f, y: 1.66f, z: 0f), scale: new Vector3(x: 0.6f, y: 0.44f, z: 0.6f), material: MatShellCream);

        // Two little orange "ear" tuning-knobs on the head sides — chunky charm, and they read at small size.
        _ = PlaceNamed(scene: scene, name: "earLeft", type: AvatarPrimitive.Cylinder, position: new Vector3(x: -0.9f, y: 2.16f, z: -0.04f), rotation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitZ, angle: (0.5f * MathF.PI)), scale: new Vector3(x: 0.34f, y: 0.42f, z: 0.34f), material: MatAccentOrange);
        _ = PlaceNamed(scene: scene, name: "earRight", type: AvatarPrimitive.Cylinder, position: new Vector3(x: 0.9f, y: 2.16f, z: -0.04f), rotation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitZ, angle: (0.5f * MathF.PI)), scale: new Vector3(x: 0.34f, y: 0.42f, z: 0.34f), material: MatAccentOrange);

        // ---- the CRT face: an orange emissive bezel framing a dark inset screen slab, bordered by cream -----------
        // The screen is deliberately SMALLER than the head front so a fat cream faceplate frames it all around — the
        // vintage-TV read. The bezel is a thin warm-orange emissive frame just proud of the cream, just larger than
        // the screen: the glowing rim that says "little CRT" even unlit. The screen slab is near-black with a small
        // Onion shell (the rounded glass / scanline-inset suggestion) sitting inside the bezel. Named "face" per the
        // flagship contract (the host's screen-slab ledger finds it by name; here it also just READS as a screen).
        _ = PlaceNamed(scene: scene, name: "bezel", type: AvatarPrimitive.Box, position: new Vector3(x: 0f, y: 2.16f, z: 0.4f), scale: new Vector3(x: 0.72f, y: 0.56f, z: 0.12f), material: MatBezelGlow, onion: 0f);
        var faceShapeId = PlaceNamed(scene: scene, name: "face", type: AvatarPrimitive.Box, position: new Vector3(x: 0f, y: 2.16f, z: 0.44f), scale: new Vector3(x: 0.62f, y: 0.46f, z: 0.12f), material: MatScreenDark, onion: 0.035f);

        // ---- the eyes: oversized, ON the screen — white sclera + big dark pupil + a white SHINE dot ---------------
        // The reference rule: wherever there's a face, big eyes with a bright shine dot. Sit just proud of the dark
        // screen so they read as glowing pixels on the CRT, pupils pushed forward + down so the face looks at the
        // viewer. Left/right placed by hand (explicit shapes keep them nameable and posable).
        PlaceEye(scene: scene, side: "Left", x: -0.2f);
        PlaceEye(scene: scene, side: "Right", x: 0.2f);

        // A small orange "smile" bar low on the screen — a friendly readout line, the accent tying the face together.
        _ = PlaceNamed(scene: scene, name: "mouth", type: AvatarPrimitive.Capsule, position: new Vector3(x: 0f, y: 1.94f, z: 0.5f), rotation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitZ, angle: (0.5f * MathF.PI)), scale: new Vector3(x: 0.2f, y: 0.34f, z: 0.2f), material: MatBezelGlow);

        // ---- the antenna: a thin cream stalk with a glowing orange tip (the "recording light") ------------------
        // A RoundCone stalk (base at the head crown, tapering up) topped by a small high-emissive orange bulb — the
        // lure-bulb language, the signature accessory carrying identity. Kept near the crown centre-back so it reads
        // against the room rather than lost off to the side.
        _ = PlaceNamed(scene: scene, name: "antennaStalk", type: AvatarPrimitive.RoundCone, position: new Vector3(x: 0.22f, y: 2.74f, z: -0.08f), rotation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitZ, angle: (-0.16f)), scale: new Vector3(x: 0.36f, y: 0.86f, z: 0.36f), material: MatShellCream);
        _ = PlaceNamed(scene: scene, name: "antennaTip", type: AvatarPrimitive.Sphere, position: new Vector3(x: 0.34f, y: 3.08f, z: -0.08f), scale: new Vector3(x: 0.3f, y: 0.3f, z: 0.3f), material: MatBezelGlow);

        // ---- four 2-link LIMB chains: thick arms with orange mitten hands, thick legs with chunky feet ----------
        // Arms hang from the shoulders, mid at the elbow, tip a big orange mitten (a RoundCone-capped chunky hand, not
        // a spindly stick). Legs are short and thick, tips are chunky slate feet planted on the floor.
        DefineArm(scene: scene, name: "armLeft", root: new Vector3(x: -1.02f, y: 1.5f, z: 0f), mid: new Vector3(x: -1.28f, y: 1.12f, z: 0.02f), tip: new Vector3(x: -1.34f, y: 0.74f, z: 0.05f));
        DefineArm(scene: scene, name: "armRight", root: new Vector3(x: 1.02f, y: 1.5f, z: 0f), mid: new Vector3(x: 1.28f, y: 1.12f, z: 0.02f), tip: new Vector3(x: 1.34f, y: 0.74f, z: 0.05f));
        DefineLeg(scene: scene, name: "legLeft", root: new Vector3(x: -0.36f, y: 0.66f, z: 0.02f), mid: new Vector3(x: -0.37f, y: 0.42f, z: 0.04f), tip: new Vector3(x: -0.38f, y: 0.12f, z: 0.1f));
        DefineLeg(scene: scene, name: "legRight", root: new Vector3(x: 0.36f, y: 0.66f, z: 0.02f), mid: new Vector3(x: 0.37f, y: 0.42f, z: 0.04f), tip: new Vector3(x: 0.38f, y: 0.12f, z: 0.1f));

        // ---- the emotes: idle (frame 1) and a friendly greet-wave (frame 2) --------------------------------------
        // Frame 1 (idle): every limb goal at rest, but the head tilts a touch and the arms settle — the neutral,
        // alive-at-rest pose. Recorded straight from the rest scene.
        _ = scene.RecordFrame();
        ResetTimelineCursor(scene: scene);

        // Frame 2 (greet-wave): armRight lifts up and out (the wave), armLeft swings gently forward, and the whole
        // creature reads as saying hello. The rest hold rest. Companion frame interpolation slerps between idle and
        // this, so the wave arcs smoothly rather than snapping.
        var armRightRest = FindChain(scene: scene, name: "armRight").Goal;
        var armLeftRest = FindChain(scene: scene, name: "armLeft").Goal;

        SetChainGoal(scene: scene, chainName: "armRight", goal: new Vector3(x: 1.5f, y: 2.28f, z: 0.35f));
        SetChainGoal(scene: scene, chainName: "armLeft", goal: new Vector3(x: -1.2f, y: 1.02f, z: 0.5f));
        _ = scene.RecordFrame();
        ResetTimelineCursor(scene: scene);
        RestoreChainGoal(scene: scene, chainName: "armRight", goal: armRightRest);
        RestoreChainGoal(scene: scene, chainName: "armLeft", goal: armLeftRest);

        // THE BEHAVIOR MANIFEST (WS-12): the robot WALKS (the default locomotion) and declares its CRT as a screen
        // FACE — a screen surface (the "face" shape) showing the DEFAULT (procedural) face feed unless a host wires it
        // otherwise. Authored as document DATA on the returned document (ToDocument does not carry the manifest), the
        // one-line `with` this recipe's siblings anticipated. The default source is the named emote face feed — pure
        // content string; the architecture names no robot-specific channel.
        return scene.ToDocument() with {
            Behavior = new CreationBehaviorDocument(
                Faces: [
                    new CreationFaceDocument(
                        DefaultSource: $"named:{Creator.CompanionState.DefaultFaceFeed}",
                        Name: "face",
                        ShapeId: faceShapeId
                    ),
                ],
                Locomotion: "walk"
            ),
        };
    }

    // Paints the deliberate color story into the palette slots the recipe uses (every other slot keeps the default
    // hue-sweep entry — untouched slots never render, so they cost nothing but keep the palette a stable 16 wide).
    private static void PaintPalette(CreatorScene scene) {
        // Cream + slate + orange. The body is a deliberately LIFTED, gently-saturated dusty slate-blue — not a neutral
        // charcoal, which collapses to black in shadow and picks up the room's cool fill unpredictably. The lifted
        // value keeps the two-tone reading (cream head vs. slate body) under the dim room light, and the touch of blue
        // makes the warm cream + orange accent pop as the complementary story.
        scene.SetPaletteEntry(index: MatShellCream, material: new SdfMaterial(Albedo: new Vector3(x: 0.96f, y: 0.9f, z: 0.76f), Emissive: 0.06f, Specular: 0.16f, Shininess: 26f));
        scene.SetPaletteEntry(index: MatBodySlate, material: new SdfMaterial(Albedo: new Vector3(x: 0.42f, y: 0.4f, z: 0.45f), Emissive: 0.04f, Specular: 0.14f, Shininess: 22f));
        scene.SetPaletteEntry(index: MatAccentOrange, material: new SdfMaterial(Albedo: new Vector3(x: 0.98f, y: 0.48f, z: 0.14f), Emissive: 0.22f, Specular: 0.24f, Shininess: 32f));
        scene.SetPaletteEntry(index: MatScreenDark, material: new SdfMaterial(Albedo: new Vector3(x: 0.06f, y: 0.09f, z: 0.12f), Emissive: 0f, Specular: 0.4f, Shininess: 72f));
        scene.SetPaletteEntry(index: MatBezelGlow, material: new SdfMaterial(Albedo: new Vector3(x: 1.0f, y: 0.56f, z: 0.2f), Emissive: 1.15f, Specular: 0.15f, Shininess: 28f));
        scene.SetPaletteEntry(index: MatEyeWhite, material: new SdfMaterial(Albedo: new Vector3(x: 0.99f, y: 0.98f, z: 0.94f), Emissive: 0.62f, Specular: 0.22f, Shininess: 44f));
        scene.SetPaletteEntry(index: MatEyeDark, material: new SdfMaterial(Albedo: new Vector3(x: 0.05f, y: 0.06f, z: 0.09f), Emissive: 0f, Specular: 0.45f, Shininess: 64f));
        scene.SetPaletteEntry(index: MatFootSlate, material: new SdfMaterial(Albedo: new Vector3(x: 0.28f, y: 0.27f, z: 0.3f), Emissive: 0f, Specular: 0.1f, Shininess: 18f));
    }

    // One oversized eye: a white sclera sphere, a dark pupil just proud of it, and a tiny white SHINE dot up-and-out
    // (the reference's signature life-giving glint). All three sit proud of the dark screen so they read as glowing.
    private static void PlaceEye(CreatorScene scene, string side, float x) {
        // The sclera: a bright white dome proud of the screen. The pupil: a BIG dark sphere pushed further forward and
        // a touch down, so it reads as a round pupil looking at the viewer (not a blank ping-pong ball). The shine: a
        // tiny bright dot up-and-out on the pupil — the signature life-giving glint.
        // The pupil sits at the eye's own x (not pulled toward centre) and just below the sclera centre, so the gaze
        // reads as looking forward-and-slightly-down at the viewer rather than cross-eyed.
        _ = PlaceNamed(scene: scene, name: $"eye{side}Sclera", type: AvatarPrimitive.Sphere, position: new Vector3(x: x, y: 2.25f, z: 0.48f), scale: new Vector3(x: 0.38f, y: 0.42f, z: 0.3f), material: MatEyeWhite);
        _ = PlaceNamed(scene: scene, name: $"eye{side}Pupil", type: AvatarPrimitive.Sphere, position: new Vector3(x: x, y: 2.21f, z: 0.58f), scale: new Vector3(x: 0.24f, y: 0.28f, z: 0.22f), material: MatEyeDark);
        _ = PlaceNamed(scene: scene, name: $"eye{side}Shine", type: AvatarPrimitive.Sphere, position: new Vector3(x: (x - 0.06f), y: 2.29f, z: 0.64f), scale: new Vector3(x: 0.1f, y: 0.1f, z: 0.09f), material: MatEyeWhite);
    }

    // ---- shared authoring helpers (drive the SAME verbs the pad/console would) -------------------------------------

    // Places one named shape at an exact transform via the player-reachable verbs (deselect → cycle the ghost to the
    // primitive → set exact transform/material/onion → place → select+rename). Mirrors the pattern the flagship
    // recipes use so an avatar authored in the creator round-trips through this identically.
    private static int PlaceNamed(CreatorScene scene, string name, AvatarPrimitive type, Vector3 position, Vector3 scale, int material, Quaternion? rotation = null, float onion = 0f) {
        scene.Deselect();

        var steps = ((((int)type - (int)scene.GhostType) + CreatorScene.PrimitiveCount) % CreatorScene.PrimitiveCount);

        for (var step = 0; (step < steps); step++) {
            scene.CyclePrimitive(direction: 1);
        }

        _ = scene.SetTargetPosition(position: position);
        scene.SetTargetRotation(yawDegrees: 0f, pitchDegrees: 0f, rollDegrees: 0f);
        _ = scene.SetTargetScale(scale: scale);
        _ = scene.SetMaterialIndex(index: material);

        if (rotation is { } explicitRotation) {
            ApplyGhostRotation(scene: scene, rotation: explicitRotation);
        }

        if (onion > 0f) {
            _ = scene.SetOnion(value: onion);
        }

        var placedId = NextShapeId(scene: scene);

        scene.Place();

        _ = scene.Select(idOrName: placedId.ToString(provider: CultureInfo.InvariantCulture));
        _ = scene.RenameSelected(name: name);
        scene.Deselect();

        return placedId;
    }

    // SetTargetRotation only takes Tait-Bryan degrees (the console-assist precision path). Every rotation this recipe
    // passes is a pure single-axis CreateFromAxisAngle; decompose it back to the matching Euler component so the exact
    // authored orientation lands without a raw-quaternion setter (which no player has).
    private static void ApplyGhostRotation(CreatorScene scene, Quaternion rotation) {
        // Recover the axis-angle, then route the angle to whichever axis carries it (X = pitch, Y = yaw, Z = roll).
        var angle = (2f * MathF.Acos(x: Math.Clamp(value: rotation.W, max: 1f, min: -1f)));
        var sin = MathF.Sqrt(x: MathF.Max(x: 0f, y: (1f - (rotation.W * rotation.W))));

        var (ax, ay, az) = ((sin < 1e-6f) ? (0f, 0f, 0f) : ((rotation.X / sin), (rotation.Y / sin), (rotation.Z / sin)));
        var degrees = (angle * (180f / MathF.PI));

        scene.SetTargetRotation(
            pitchDegrees: (degrees * ax),
            rollDegrees: (degrees * az),
            yawDegrees: (degrees * ay)
        );
    }

    // The id CreatorScene hands the NEXT placed shape — for a freshly authored recipe (no deletes) always equals
    // PlacedCount (ids assign 0, 1, 2, ... in place order).
    private static int NextShapeId(CreatorScene scene) => scene.PlacedCount;

    // An arm limb: a thick cream upper (shoulder) + charcoal forearm + a big ORANGE MITTEN hand (a fat RoundCone-tip,
    // not a spindly sphere) — the "mitten hands, no spindly sticks" directive. Three shapes = a valid 2-bone limb.
    private static void DefineArm(CreatorScene scene, string name, Vector3 root, Vector3 mid, Vector3 tip) {
        var rootId = PlaceNamed(scene: scene, name: $"{name}Root", type: AvatarPrimitive.Capsule, position: root, scale: new Vector3(x: 0.5f, y: 0.42f, z: 0.5f), material: MatShellCream);
        var midId = PlaceNamed(scene: scene, name: $"{name}Mid", type: AvatarPrimitive.Capsule, position: mid, scale: new Vector3(x: 0.42f, y: 0.4f, z: 0.42f), material: MatBodySlate);
        var tipId = PlaceNamed(scene: scene, name: $"{name}Tip", type: AvatarPrimitive.Sphere, position: tip, scale: new Vector3(x: 0.44f, y: 0.44f, z: 0.44f), material: MatAccentOrange);

        _ = scene.DefineChain(name: name, shapeIdsOrNames: [Str(value: rootId), Str(value: midId), Str(value: tipId)], kind: CreatorChainState.KindLimb);
    }

    // A leg limb: a thick charcoal thigh + shin + a chunky slate FOOT (a fat box, planted). Three shapes = a valid
    // 2-bone limb.
    private static void DefineLeg(CreatorScene scene, string name, Vector3 root, Vector3 mid, Vector3 tip) {
        var rootId = PlaceNamed(scene: scene, name: $"{name}Root", type: AvatarPrimitive.Capsule, position: root, scale: new Vector3(x: 0.5f, y: 0.42f, z: 0.5f), material: MatBodySlate);
        var midId = PlaceNamed(scene: scene, name: $"{name}Mid", type: AvatarPrimitive.Capsule, position: mid, scale: new Vector3(x: 0.46f, y: 0.4f, z: 0.46f), material: MatBodySlate);
        var tipId = PlaceNamed(scene: scene, name: $"{name}Tip", type: AvatarPrimitive.Box, position: tip, scale: new Vector3(x: 0.52f, y: 0.34f, z: 0.72f), material: MatFootSlate);

        _ = scene.DefineChain(name: name, shapeIdsOrNames: [Str(value: rootId), Str(value: midId), Str(value: tipId)], kind: CreatorChainState.KindLimb);
    }
    private static string Str(int value) => value.ToString(provider: CultureInfo.InvariantCulture);

    // Finds a defined chain by name (recipes always name theirs, so this never returns null in practice here).
    private static CreatorChainState FindChain(CreatorScene scene, string name) {
        foreach (var chain in scene.Chains) {
            if (string.Equals(a: chain.Name, b: name, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                return chain;
            }
        }

        throw new InvalidOperationException(message: $"No chain named '{name}' is defined.");
    }

    // Moves a named chain's goal to an EXACT position by selecting it as the target and driving SetTargetPosition —
    // the same console-assist precision path the RIG page's goal nudge stands in for on the pad.
    private static void SetChainGoal(CreatorScene scene, string chainName, Vector3 goal) {
        SelectGoal(scene: scene, chainName: chainName);
        _ = scene.SetTargetPosition(position: goal);
    }
    private static void RestoreChainGoal(CreatorScene scene, string chainName, Vector3 goal) {
        SelectGoal(scene: scene, chainName: chainName);
        _ = scene.SetTargetPosition(position: goal);
        scene.Deselect();
    }

    // Walks CycleSelection into goal-space until the named chain's goal is the target (goals sit after every placed
    // shape, in chain-definition order): from a freshly deselected (ghost) target, cycling forward PlacedCount times
    // lands on goal index 0, one further step per chain position after that.
    private static void SelectGoal(CreatorScene scene, string chainName) {
        scene.Deselect();

        var chainIndex = -1;

        for (var index = 0; (index < scene.Chains.Count); index++) {
            if (string.Equals(a: scene.Chains[index].Name, b: chainName, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                chainIndex = index;

                break;
            }
        }

        if (chainIndex < 0) {
            throw new InvalidOperationException(message: $"No chain named '{chainName}' is defined.");
        }

        for (var step = 0; (step <= (scene.PlacedCount + chainIndex)); step++) {
            scene.CycleSelection(direction: 1);
        }
    }

    // Walks the timeline cursor back to REST (index 0) via the public StepFrame path — which restores the rest pose's
    // transforms. Harmless for every caller here: each sweep re-sets the chain goal immediately after, before the next
    // RecordFrame, so the momentarily-restored rest pose never gets recorded.
    private static void ResetTimelineCursor(CreatorScene scene) {
        while (scene.CurrentFrame != 0) {
            _ = scene.StepFrame(direction: -1);
        }
    }
}
