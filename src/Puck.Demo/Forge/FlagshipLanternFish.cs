using System.Globalization;
using System.Numerics;
using Puck.Authoring;
using Puck.Demo.Creator;
using Puck.SdfVm;

namespace Puck.Demo.Forge;

/// <summary>
/// Authors Arc 3's lantern-fish flagship as a <see cref="CreationDocument"/> — committed content, not code: the recipe
/// drives a fresh, headless <see cref="CreatorScene"/> through EXACTLY the verbs a player has at the bench
/// (<see cref="CreatorScene.Place"/>, <see cref="CreatorScene.SetPaletteEntry"/>, <see cref="CreatorScene.LinkWithPrevious"/>,
/// <see cref="CreatorScene.DefineChain"/>, <see cref="CreatorScene.RecordFrame"/>), then lifts
/// <see cref="CreatorScene.ToDocument"/>. Nothing here reaches past the player-reachable vocabulary: the primitives
/// (including the newly-exposed <see cref="AvatarPrimitive.RoundCone"/>), the scale envelope
/// [<see cref="CreatorScene.MinScale"/>, <see cref="CreatorScene.MaxScale"/>], the <see cref="CreatorScene.Capacity"/>
/// shape ceiling, the <see cref="CreatorScene.PaletteSize"/> palette, and chains as the RIG page defines them. It is
/// deterministic by construction — no RNG, no wall-clock, every value a literal — so re-running the recipe reproduces
/// the committed <c>docs/examples/creations/lantern-fish.creation.json</c> byte-for-byte (a save→load→save round-trip
/// through <see cref="CreationStore"/> is stable).
///
/// The aesthetic is modelled on the old-project anglerfish: a chunky two-tone blob (dark navy hide above, soft
/// lavender belly below) SMOOTH-WELDED into one flowing mass, an enormous pair of shined eyes (bright sclera + inset
/// ink pupil + a tiny emission-1.0 catchlight — the highest-leverage cuteness device), a goofy open maw CARVED with
/// SmoothSubtraction and lined with chunky RoundCone teeth, cyan bioluminescent photophores and fin membranes, a run
/// of dorsal spikes, and the signature camera lure — a stalk arcing overhead tipped with a bulb that carries a hot
/// red "recording" LED. It swims by undulating a 6-link spine chain through a full sinusoid, played back smoothly on
/// the companion's interpolated timeline.
///
/// GROUPING NOTE: the engine's per-op material ownership assigns a welded surface the material of whichever shape is
/// NEAREST, so the two-tone reads crisply as long as the navy hide is the bulk and the lavender belly overlaps only
/// the underside. The engine's Mirror op self-folds a shape about its OWN center (it can't duplicate across the body),
/// so every symmetric feature — eyes, teeth, photophores, fins — is placed as an explicit left+right pair.
///
/// BEHAVIOR MANIFEST SEAM: the fact that this creature SWIMS (rather than walks) is a per-creation behavioral fact
/// that a forthcoming nullable manifest field on <c>puck.creation.v1</c> will carry; the recipe returns a plain
/// <see cref="CreationDocument"/> from <see cref="CreatorScene.ToDocument"/>, so declaring that manifest later is a
/// one-line addition here (a <c>with</c> on the returned document) — no restructuring required.
/// </summary>
internal static class FlagshipLanternFish {
    /// <summary>The synthetic authoring bench the recipe builds against — the same proportions
    /// <c>OverworldFrameSource</c> derives for the live room (4-unit horizontal half-extent, a floor-relative
    /// vertical band), just centred on the origin since a recipe has no room to sit inside.</summary>
    private static WorkbenchRegion Bench => new(Center: Vector3.Zero, HalfExtent: 4f, MinY: 0.35f, MaxY: 3.0f);

    // Palette slot indices — named so the recipe reads as a deep-sea color story, not a magic-number soup.
    private const int Hide = 0;
    private const int Belly = 1;
    private const int Fin = 10;
    private const int Glow = 6;
    private const int Maw = 7;
    private const int Pupil = 3;
    private const int Rec = 9;
    private const int Sclera = 4;
    private const int Shell = 8;
    private const int Shine = 5;
    private const int Teeth = 2;

    // The sphere primitive's canonical unit radius (AvatarDefinition.SphereRadius) — so an eye layer's desired WORLD
    // radius maps to the right per-axis scale (scale = desiredRadius / unitRadius).
    private const float SphereUnit = 0.38f;

    // The front of a companion faces +Z (its wander steering yaws toward its travel direction, where yaw 0 looks down
    // +Z) — so the face, maw, eyes, and lure all point at +Z.

    /// <summary>Builds the lantern-fish document (see the type remarks for the full aesthetic).</summary>
    /// <returns>The normalized document, ready for <see cref="CreationStore.ToJson"/> or to spawn as a companion.</returns>
    public static CreationDocument BuildLanternFish() {
        var scene = new CreatorScene(workbench: Bench);

        scene.SetActive(active: true);
        scene.SetName(name: "lantern-fish");
        scene.SetIntent(intent: CreatorIntent.Object);

        // Palette. The room dims itself so emissive glow dominates (see sdf-world.hlsli's environment entry), so every
        // material carries a modest emissive floor to read: the navy hide stays deliberately dark (deep-sea), the
        // belly/teeth/fins lift enough to show their color, and the bioluminescence / eyes / LED sit far brighter.
        SetPalette(scene, Hide, new Vector3(x: 0.11f, y: 0.15f, z: 0.28f), emissive: 0.12f, specular: 0.15f, shininess: 18f);
        SetPalette(scene, Belly, new Vector3(x: 0.64f, y: 0.57f, z: 0.76f), emissive: 0.22f, specular: 0.1f);
        SetPalette(scene, Teeth, new Vector3(x: 0.95f, y: 0.93f, z: 0.85f), emissive: 0.3f, specular: 0.2f);
        SetPalette(scene, Pupil, new Vector3(x: 0.03f, y: 0.03f, z: 0.05f));
        SetPalette(scene, Sclera, new Vector3(x: 0.96f, y: 0.97f, z: 1.0f), emissive: 0.4f);
        SetPalette(scene, Shine, new Vector3(x: 1.0f, y: 1.0f, z: 1.0f), emissive: 1.0f);
        SetPalette(scene, Glow, new Vector3(x: 0.45f, y: 0.92f, z: 1.0f), emissive: 1.3f);
        SetPalette(scene, Maw, new Vector3(x: 0.34f, y: 0.05f, z: 0.07f), emissive: 0.35f);
        SetPalette(scene, Shell, new Vector3(x: 0.16f, y: 0.17f, z: 0.2f), emissive: 0.1f, specular: 0.25f);
        SetPalette(scene, Rec, new Vector3(x: 1.0f, y: 0.1f, z: 0.06f), emissive: 2.4f);
        SetPalette(scene, Fin, new Vector3(x: 0.32f, y: 0.46f, z: 0.66f), emissive: 0.3f);

        // THE BODY MASS: a 6-ellipsoid spine, nose (+Z) → tail (−Z), each welded to the last with a fat smooth radius
        // so the whole thing reads as ONE tapering navy blob (the first is the plain Union base; the rest SmoothUnion
        // into it). Chunky and near-spherical at the head, tapering to the caudal peduncle at the tail.
        const int spineCount = 6;
        var spineIds = new int[spineCount];

        for (var index = 0; (index < spineCount); index++) {
            var t = (index / (float)(spineCount - 1));
            var z = (0.95f - (t * 1.9f));
            var girth = (1.35f - (t * 0.95f)); // slightly wider than tall (a fish's fusiform section)
            var scale = new Vector3(x: (girth * 1.02f), y: (girth * 0.9f), z: MathF.Max(x: 0.32f, y: girth));

            spineIds[index] = PlaceNamed(scene, $"spine{index}", AvatarPrimitive.Ellipsoid, new Vector3(x: 0f, y: 1.45f, z: z), scale, Hide,
                blend: ((index == 0) ? SdfBlendOp.Union : SdfBlendOp.SmoothUnion), smooth: 0.34f);
        }

        // The lavender belly: two ellipsoids slung under the head/mid, SmoothUnion-welded so the dark-above /
        // light-below two-tone reads as one continuous underside rather than a stuck-on patch.
        var bellyFrontId = PlaceNamed(scene, "bellyFront", AvatarPrimitive.Ellipsoid, new Vector3(x: 0f, y: 1.16f, z: 0.55f), new Vector3(x: 1.05f, y: 0.7f, z: 1.0f), Belly, blend: SdfBlendOp.SmoothUnion, smooth: 0.3f);
        var bellyBackId = PlaceNamed(scene, "bellyBack", AvatarPrimitive.Ellipsoid, new Vector3(x: 0f, y: 1.2f, z: -0.2f), new Vector3(x: 0.85f, y: 0.6f, z: 0.9f), Belly, blend: SdfBlendOp.SmoothUnion, smooth: 0.3f);

        // Weld the whole body — spine + belly — into ONE group so the smooth-min flows across all of it.
        WeldGroup(ids: [.. spineIds, bellyFrontId, bellyBackId], scene: scene);

        // THE MAW: a dark-red ellipsoid carved INTO the front of the head with SmoothSubtraction — a big goofy open
        // mouth glowing faintly from inside (its own group; subtraction auto-groups, and the carve reads against the
        // body it overlaps). Sits low and forward on the head.
        _ = PlaceNamed(scene, "maw", AvatarPrimitive.Ellipsoid, new Vector3(x: 0f, y: 1.24f, z: 1.28f), new Vector3(x: 0.72f, y: 0.42f, z: 0.5f), Maw, blend: SdfBlendOp.SmoothSubtraction, smooth: 0.14f);

        // Teeth: chunky white round-cones on the lower jaw line, points up, in symmetric pairs straddling the mouth,
        // plus a center tooth on the midline — the toothy-grin signature. A gentle back-tilt so they read as fangs.
        var toothTilt = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitX, angle: (-0.25f * MathF.PI));

        _ = PlaceNamed(scene, "toothCenter", AvatarPrimitive.RoundCone, new Vector3(x: 0f, y: 1.02f, z: 1.36f), new Vector3(x: 0.42f, y: 0.6f, z: 0.42f), Teeth, rotation: toothTilt);
        PlaceMirroredPair(scene, "toothA", AvatarPrimitive.RoundCone, new Vector3(x: 0.3f, y: 1.06f, z: 1.28f), new Vector3(x: 0.38f, y: 0.52f, z: 0.38f), Teeth, rotation: toothTilt);
        PlaceMirroredPair(scene, "toothB", AvatarPrimitive.RoundCone, new Vector3(x: 0.56f, y: 1.12f, z: 1.08f), new Vector3(x: 0.32f, y: 0.44f, z: 0.32f), Teeth, rotation: toothTilt);

        // THE EYES: the charm engine. A bright sclera sphere, an inset ink pupil, and a tiny emission-1.0 white
        // catchlight offset up-and-forward — a symmetric pair straddling the face. Oversized (a third of head height)
        // and set high and forward, spaced so a band of navy hide reads down the middle of the face.
        PlaceEyePair(scene, "eye", new Vector3(x: 0.56f, y: 1.74f, z: 0.88f), scleraRadius: 0.36f);

        // Dorsal spikes: a little run of navy round-cones marching down the back, points up and slightly back — the
        // anglerfish's spiny ridge (on the centerline, no pairing needed).
        _ = PlaceNamed(scene, "dorsal0", AvatarPrimitive.RoundCone, new Vector3(x: 0f, y: 2.02f, z: 0.35f), new Vector3(x: 0.3f, y: 0.55f, z: 0.3f), Hide, rotation: Quaternion.CreateFromAxisAngle(angle: (-0.18f * MathF.PI), axis: Vector3.UnitX));
        _ = PlaceNamed(scene, "dorsal1", AvatarPrimitive.RoundCone, new Vector3(x: 0f, y: 2.0f, z: -0.05f), new Vector3(x: 0.28f, y: 0.5f, z: 0.28f), Hide, rotation: Quaternion.CreateFromAxisAngle(angle: (-0.05f * MathF.PI), axis: Vector3.UnitX));
        _ = PlaceNamed(scene, "dorsal2", AvatarPrimitive.RoundCone, new Vector3(x: 0f, y: 1.9f, z: -0.42f), new Vector3(x: 0.24f, y: 0.42f, z: 0.24f), Hide, rotation: Quaternion.CreateFromAxisAngle(angle: (0.08f * MathF.PI), axis: Vector3.UnitX));

        // Photophores: three glowing cyan pinpoints along each flank (symmetric pairs), tucked ONTO the body surface
        // (not floating beside it) — the deep-sea bioluminescence that, with the eye shine and the lure LED, does the
        // "it lives in the dark" read.
        PlaceMirroredPair(scene, "photoA", AvatarPrimitive.Sphere, new Vector3(x: 0.5f, y: 1.34f, z: 0.35f), new Vector3(value: 0.17f), Glow);
        PlaceMirroredPair(scene, "photoB", AvatarPrimitive.Sphere, new Vector3(x: 0.48f, y: 1.26f, z: -0.05f), new Vector3(value: 0.15f), Glow);
        PlaceMirroredPair(scene, "photoC", AvatarPrimitive.Sphere, new Vector3(x: 0.4f, y: 1.22f, z: -0.4f), new Vector3(value: 0.13f), Glow);

        // Pectoral fins: small flattened, twisted ellipsoids hugging each flank (a symmetric pair), yawed out and
        // twisted so the membrane reads as a swept-back fin rather than a floating paddle — kept close to the body so
        // it grows OUT of the flank instead of hovering beside it.
        PlaceMirroredPair(scene, "fin", AvatarPrimitive.Ellipsoid, new Vector3(x: 0.62f, y: 1.12f, z: 0.05f), new Vector3(x: 0.44f, y: 0.16f, z: 0.32f), Fin, rotation: Quaternion.CreateFromAxisAngle(angle: (0.42f * MathF.PI), axis: Vector3.UnitY), twist: 0.5f);

        // Tail fin: a tall, thin caudal fan at the tail end, twisted so it reads as a swimming fin.
        _ = PlaceNamed(scene, "tailFin", AvatarPrimitive.Ellipsoid, new Vector3(x: 0f, y: 1.5f, z: -1.35f), new Vector3(x: 0.16f, y: 0.9f, z: 0.46f), Fin, twist: 0.5f);

        // THE LURE (the signature gag, and our upgrade over the reference — ours carries the live camera): a 2-shape
        // "spine" chain arcing up and forward off the head. The dark stalk drags/lags behind the head the same gentle
        // way the body does; the bulb at its tip carries the hot red recording LED.
        var stalkId = PlaceNamed(scene, "lureStalk", AvatarPrimitive.Capsule, new Vector3(x: 0f, y: 2.15f, z: 0.95f), new Vector3(value: 0.28f), Shell, rotation: Quaternion.CreateFromAxisAngle(angle: (0.35f * MathF.PI), axis: Vector3.UnitX));
        var bulbId = PlaceNamed(scene, "lureBulb", AvatarPrimitive.Sphere, new Vector3(x: 0f, y: 2.78f, z: 1.5f), new Vector3(value: 0.34f), Shell);

        _ = scene.DefineChain(name: "lure", shapeIdsOrNames: [Id(value: stalkId), Id(value: bulbId)], kind: CreatorChainState.KindSpine);

        // The recording LED on the bulb tip — the hottest point on the whole creature.
        _ = PlaceNamed(scene, "recLed", AvatarPrimitive.Sphere, new Vector3(x: 0f, y: 2.86f, z: 1.72f), new Vector3(value: 0.14f), Rec);

        // The spine chain, captured LAST so its rest geometry reads against the finished body positions.
        _ = scene.DefineChain(name: "spine", shapeIdsOrNames: [.. spineIds.Select(selector: static id => Id(value: id))], kind: CreatorChainState.KindSpine);

        // THE SWIM CYCLE: sweep the spine's goal through one full sinusoid, 8 frames — the undulation wants the extra
        // smoothness, and frames are cheap; the companion's interpolated timeline slerps between them so 8 keys read
        // as a continuous porpoise. Each frame appends (cursor reset to rest first, then the goal move re-poses before
        // recording), so the loop hands frame 8 back to frame 1 with no jump-cut. A vertical lift term (at twice the
        // lateral frequency) rides alongside the sway so the body porpoises gently rather than just wagging.
        var restGoal = FindChain(name: "spine", scene: scene).Goal;
        const int swimFrames = 8;

        for (var frame = 0; (frame < swimFrames); frame++) {
            var phase = ((frame / (float)swimFrames) * MathF.Tau);
            var sway = new Vector3(x: (MathF.Sin(x: phase) * 0.7f), y: (MathF.Sin(x: (phase * 2f)) * 0.16f), z: 0f);

            ResetTimelineCursor(scene: scene);
            SetChainGoal(chainName: "spine", goal: (restGoal + sway), scene: scene);
            _ = scene.RecordFrame();
        }

        ResetTimelineCursor(scene: scene);
        RestoreChainGoal(chainName: "spine", goal: restGoal, scene: scene);

        // THE LURE CAMERA + BEHAVIOR MANIFEST (WS-12): the signature upgrade over the reference — the bulb carries a
        // live camera EYE. It is authored as DATA on the returned document (the recipe's own vocabulary stops at the
        // scene verbs; a camera/behavior manifest is document data ToDocument does not carry, so it is a `with` here,
        // exactly as this recipe's remarks anticipated). The eye rides the lure BULB shape (so it swings with the lure
        // through the swim cycle), tilted down and forward so its feed frames the room ahead of and below the lure —
        // "the room as seen from the lure". Its feed is NAMED "lure" (pure content string), wirable onto any screen.
        // The behavior manifest records the fact this creature SWIMS (so a companion loads it hover-bobbing without the
        // console swim token). No face is declared — the fish is the camera operator, not a screen-faced creature.
        return scene.ToDocument() with {
            Behavior = new CreationBehaviorDocument(Faces: null, Locomotion: "swim"),
            Cameras = [
                new CreationCameraDocument(
                    // The lure arcs FORWARD over the head (its bulb sits ahead of and above the face); the recording
                    // eye looks BACK and DOWN from the bulb, so its feed frames the anglerfish's own glowing face —
                    // the big shined eyes, the toothy maw, the photophores — against the room beyond. "The creature as
                    // seen from its own lure": the signature diegetic-camera gag.
                    Feed: "lure",
                    Focus: 1.4f,
                    Fov: 74f,
                    Id: 0,
                    Pitch: -50f,
                    Position: new Vector3(x: 0f, y: 0.15f, z: 0.05f),
                    ShapeId: bulbId,
                    Yaw: 180f
                ),
            ],
        };
    }

    // ---- authoring helpers (drive the SAME verbs the pad/console would) --------------------------------------------

    // Sets one palette slot directly (the console palette verb's precision path), so the fish's colors are a
    // deliberate story rather than the default golden-ratio sweep.
    private static void SetPalette(CreatorScene scene, int slot, Vector3 albedo, float emissive = 0f, float specular = 0f, float shininess = 32f) {
        scene.SetPaletteEntry(index: slot, material: new SdfMaterial(Albedo: albedo, Emissive: emissive, Shininess: shininess, Specular: specular));
    }

    // Places one named shape at an exact transform, with an optional blend/smooth/twist: deselect (targets the ghost),
    // cycle the ghost to the desired primitive from its current type, set the exact transform/material/style, place,
    // then select and rename the just-placed shape (RenameSelected needs a real selection).
    private static int PlaceNamed(CreatorScene scene, string name, AvatarPrimitive type, Vector3 position, Vector3 scale, int material, Quaternion? rotation = null, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f, float twist = 0f) {
        scene.Deselect();

        var steps = ((((int)type - (int)scene.GhostType) + CreatorScene.PrimitiveCount) % CreatorScene.PrimitiveCount);

        for (var step = 0; (step < steps); step++) {
            scene.CyclePrimitive(direction: 1);
        }

        _ = scene.SetTargetPosition(position: position);
        scene.SetTargetRotation(yawDegrees: 0f, pitchDegrees: 0f, rollDegrees: 0f);
        _ = scene.SetTargetScale(scale: scale);
        _ = scene.SetMaterialIndex(index: material);
        scene.SetBlend(blend: blend);
        _ = scene.SetSmooth(value: smooth);

        if (twist != 0f) {
            _ = scene.SetTwist(value: twist);
        }

        if (rotation is { } explicitRotation) {
            ApplyGhostRotation(rotation: explicitRotation, scene: scene);
        }

        var placedId = scene.PlacedCount; // no deletes in a recipe, so the next id equals the current count

        scene.Place();

        _ = scene.Select(idOrName: Id(value: placedId));
        _ = scene.RenameSelected(name: name);
        scene.Deselect();

        return placedId;
    }

    // Places a symmetric PAIR of eyes straddling the face centerline at ±|center.X| (the engine's Mirror op self-folds
    // a shape about its OWN center, so a cross-body pair is two explicit shapes). Each eye is a bright sclera sphere,
    // an inset ink pupil, and a tiny emission-1.0 white catchlight offset up-and-toward the front. Ungrouped Union
    // spheres, so the nearest (frontmost) surface's material wins where they overlap: pupil over sclera, shine over
    // pupil. The shine sits in each eye's upper-OUTER quadrant (mirrored left/right) the way a real catchlight would.
    private static void PlaceEyePair(CreatorScene scene, string name, Vector3 center, float scleraRadius) {
        var pupilRadius = (scleraRadius * 0.6f);
        var shineRadius = (scleraRadius * 0.26f);
        var offsetX = MathF.Abs(x: center.X);

        foreach (var side in new[] { 1f, -1f }) {
            var eyeCenter = new Vector3(x: (offsetX * side), y: center.Y, z: center.Z);
            var pupilCenter = new Vector3(x: eyeCenter.X, y: eyeCenter.Y, z: (eyeCenter.Z + (scleraRadius * 0.5f)));
            var shineCenter = new Vector3(x: (eyeCenter.X + ((scleraRadius * 0.24f) * side)), y: (eyeCenter.Y + (scleraRadius * 0.3f)), z: (eyeCenter.Z + (scleraRadius * 0.82f)));
            var suffix = ((side > 0f) ? "R" : "L");

            _ = PlaceNamed(scene, $"{name}Sclera{suffix}", AvatarPrimitive.Sphere, eyeCenter, new Vector3(value: (scleraRadius / SphereUnit)), Sclera);
            _ = PlaceNamed(scene, $"{name}Pupil{suffix}", AvatarPrimitive.Sphere, pupilCenter, new Vector3(value: (pupilRadius / SphereUnit)), Pupil);
            _ = PlaceNamed(scene, $"{name}Shine{suffix}", AvatarPrimitive.Sphere, shineCenter, new Vector3(value: (shineRadius / SphereUnit)), Shine);
        }
    }

    // Places a symmetric PAIR of the same shape at ±|position.X| (the manual cross-body mirror — see PlaceEyePair's
    // note on why the engine's Mirror op can't do this). A single-axis tilt is mirrored about the X=0 plane so the
    // left copy leans the mirror-image of the right; a twist likewise flips sign on the far side.
    private static void PlaceMirroredPair(CreatorScene scene, string name, AvatarPrimitive type, Vector3 position, Vector3 scale, int material, Quaternion? rotation = null, float twist = 0f) {
        var offsetX = MathF.Abs(x: position.X);

        foreach (var side in new[] { 1f, -1f }) {
            var suffix = ((side > 0f) ? "R" : "L");
            var sideRotation = rotation;

            if ((rotation is { } r) && (side < 0f)) {
                // Mirror a single-axis tilt about the X=0 plane: a Y-axis (yaw) or Z-axis (roll) rotation negates on
                // the far side so the pair splays symmetrically; an X-axis (pitch) tilt is the same on both sides.
                sideRotation = new Quaternion(w: r.W, x: r.X, y: -r.Y, z: -r.Z);
            }

            _ = PlaceNamed(scene, $"{name}{suffix}", type, new Vector3(x: (offsetX * side), y: position.Y, z: position.Z), scale, material, rotation: sideRotation, twist: (twist * side));
        }
    }

    // SetTargetRotation only takes Tait-Bryan degrees (the console-assist precision path a player has, no raw
    // quaternion setter exists). Every caller passes a pure Y-axis or a pure X-axis rotation, so this decomposes that
    // back to exact yaw/pitch degrees.
    private static void ApplyGhostRotation(CreatorScene scene, Quaternion rotation) {
        var angle = (2f * MathF.Atan2(new Vector3(x: rotation.X, y: rotation.Y, z: rotation.Z).Length(), rotation.W));
        var toDegrees = (180f / MathF.PI);

        if (MathF.Abs(x: rotation.X) >= MathF.Abs(x: rotation.Y)) {
            var sign = ((rotation.X < 0f) ? -1f : 1f);

            scene.SetTargetRotation(yawDegrees: 0f, pitchDegrees: ((angle * sign) * toDegrees), rollDegrees: 0f);
        } else {
            var sign = ((rotation.Y < 0f) ? -1f : 1f);

            scene.SetTargetRotation(yawDegrees: ((angle * sign) * toDegrees), pitchDegrees: 0f, rollDegrees: 0f);
        }
    }

    // Links a set of already-placed shapes into ONE composition group (via the SELECT page's chain-link verb: select
    // A, select B, link — repeated so every shape joins the first shape's group) so a SmoothUnion body flows as one
    // welded mass. The first id's group anchors the rest.
    private static void WeldGroup(CreatorScene scene, IReadOnlyList<int> ids) {
        if (ids.Count < 2) {
            return;
        }

        _ = scene.Select(idOrName: Id(value: ids[0]));

        for (var index = 1; (index < ids.Count); index++) {
            _ = scene.Select(idOrName: Id(value: ids[index]));
            _ = scene.LinkWithPrevious();
        }

        scene.Deselect();
    }

    // Finds a defined chain by name (the recipe always names its chains, so this never returns null in practice here).
    private static CreatorChainState FindChain(CreatorScene scene, string name) {
        foreach (var chain in scene.Chains) {
            if (string.Equals(a: chain.Name, b: name, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                return chain;
            }
        }

        throw new InvalidOperationException(message: $"No chain named '{name}' is defined.");
    }

    // Moves a named chain's goal to an EXACT position by selecting it as the target (CycleSelection walks the combined
    // shape+goal cursor space) and driving SetTargetPosition — the console-assist precision path the RIG page's goal
    // nudge stands in for on the pad.
    private static void SetChainGoal(CreatorScene scene, string chainName, Vector3 goal) {
        SelectGoal(chainName: chainName, scene: scene);
        _ = scene.SetTargetPosition(position: goal);
    }
    private static void RestoreChainGoal(CreatorScene scene, string chainName, Vector3 goal) {
        SelectGoal(chainName: chainName, scene: scene);
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

    // Walks the timeline cursor back to REST (index 0) via the public StepFrame/SetFrame path (which restores the rest
    // pose's transforms). Harmless for every caller: each sweep iteration re-sets the chain goal (which re-solves)
    // immediately after this call and before the next RecordFrame, so the momentarily-restored rest pose is never
    // recorded.
    private static void ResetTimelineCursor(CreatorScene scene) {
        while (scene.CurrentFrame != 0) {
            _ = scene.StepFrame(direction: -1);
        }
    }

    // The invariant-culture decimal id string the scene's id/name resolvers expect.
    private static string Id(int value) => value.ToString(provider: CultureInfo.InvariantCulture);
}
