using System.Numerics;
using Puck.Authoring;
using Puck.Demo.Creator;
using Puck.SdfVm;

namespace Puck.Demo.Forge;

/// <summary>
/// Authors Arc 3's ADVENTURER flagship — the classic top-down RPG hero, cartoonified — as a
/// <see cref="CreationDocument"/>. Like its siblings in <see cref="FlagshipCreations"/> this recipe drives a fresh,
/// headless <see cref="CreatorScene"/> through EXACTLY the verbs a player has at the bench (place, paint a palette
/// slot, style with smooth/onion/mirror, rig 2-bone limb chains, and a <see cref="CreatorScene.Gait"/> walk), then
/// lifts <see cref="CreatorScene.ToDocument"/> — nothing here reaches past the player-reachable vocabulary (the seven
/// primitives, the scale envelope, the <see cref="CreatorScene.Capacity"/> shape ceiling, the 16-slot palette, chains
/// as the RIG page defines them). Deterministic by construction — every value a literal, no RNG, no wall-clock — so
/// re-running reproduces <c>docs/examples/creations/adventurer.creation.json</c> byte-for-byte.
///
/// The look is a deliberate escalation over the plain torso/head/limbs the adventurer started as, tuned against the
/// old menagerie's lessons: a CHUNKY silhouette (an oversized head under a pointed cap, a stubby belted body, short
/// thick limbs, planted boots), a THREE-COLOR story (tunic green above, cream face/trim below, brown boots+belt
/// grounding it), ONE signature accessory (a red backpack with a gold, faintly-glowing emblem patch — the identity
/// piece, exactly the "tiny red saddle-pack with a yellow emblem" role from the reference deer-llama), and a real
/// FACE (big mirrored eyes, each a dark sclera with a bright emissive shine dot, plus a friendly carved smile). The
/// stride keys land at frames 1-2 (the bake's walk-pair convention) with two extra personality frames past them (a
/// bob/lean the room's frame interpolation blends through — the bake ignores frames past 2, so the convention
/// survives).
/// </summary>
internal static class FlagshipAdventurer {
    /// <summary>The synthetic authoring bench the recipe builds against — the same proportions the sibling flagships
    /// use (a 4-unit horizontal half-extent, a floor-relative vertical band), centred on the origin.</summary>
    private static WorkbenchRegion Bench => new(Center: Vector3.Zero, HalfExtent: 4f, MinY: 0.05f, MaxY: 3.0f);

    // ---- the palette: the three-color story + the eye/accent slots, painted into named slots -----------------------
    // A shape's material index selects one of these. Slots 8+ stay on the default hue sweep (unused by this recipe).
    private const int TunicGreen = 0;   // the body/tunic — the "dark above" two-tone half
    private const int CapGreen = 1;     // a deeper green for the pointed cap (reads as its own piece over the head)
    private const int SkinCream = 2;    // the face + hands + collar trim — the "light below" two-tone half
    private const int BootBrown = 3;    // boots + belt — the grounding dark, bottom-weighted
    private const int PackRed = 4;      // the signature backpack — the one loud identity color
    private const int EmblemGold = 5;   // the emblem patch on the pack — the emissive accent (the deer-llama's letter)
    private const int EyeDark = 6;      // the eye sclera — near-black, so the shine reads
    private const int EyeShine = 7;     // the white shine dot — emissive, the "alive" spark every reference eye has

    /// <summary>Builds the adventurer document: a hatted, big-headed, belted little hero with a red-and-gold backpack
    /// and a real face, rigged with four 2-link limb chains and a planted-foot march. Frames 1-2 are the walk-pair;
    /// frames 3-4 add a confident bob the room blends through.</summary>
    /// <returns>The normalized document, ready for <see cref="CreationStore.ToJson"/> or the bake.</returns>
    public static CreationDocument BuildAdventurer() {
        var scene = new CreatorScene(workbench: Bench);

        scene.SetActive(active: true);
        scene.SetName(name: "adventurer");
        scene.SetIntent(intent: CreatorIntent.Object);

        PaintPalette(scene: scene);

        // A chibi hero — a COMPACT, heavily-overlapping stack (nothing lanky): boots on the floor, a rounded body,
        // a BIG head sitting almost directly on it, capped by a pointed hat. Overlaps are deliberate and smooth-unioned
        // so the torso/belt/head read as ONE fused little body rather than a tower of separate balls. Floor is y≈0.

        // ---- the body: a rounded egg torso, wider than tall, sitting low so the head can dominate ----------------
        // An ellipsoid reads as a plump little belly; smooth blends it into the belt below and the head above.
        _ = PlaceNamed(scene: scene, name: "torso", type: AvatarPrimitive.Ellipsoid, position: new Vector3(x: 0f, y: 0.66f, z: 0f), scale: new Vector3(x: 0.92f, y: 1.0f, z: 0.86f), material: TunicGreen, smooth: 0.16f);
        // The belt: a flattened brown band at the waist, overlapping the torso bottom — the two-tone split (green
        // tunic above, brown boots below), fused in so it reads as a belt, not a stacked disc.
        _ = PlaceNamed(scene: scene, name: "belt", type: AvatarPrimitive.Cylinder, position: new Vector3(x: 0f, y: 0.46f, z: 0f), scale: new Vector3(x: 0.86f, y: 0.22f, z: 0.82f), material: BootBrown, smooth: 0.08f);
        // The collar: a slim cream band at the neck line, overlapping BOTH torso-top and head-bottom, so the face's
        // cream reaches down into the tunic (the reference's belly-cream reaching up) and the head-body seam vanishes.
        _ = PlaceNamed(scene: scene, name: "collar", type: AvatarPrimitive.Cylinder, position: new Vector3(x: 0f, y: 0.92f, z: 0.02f), scale: new Vector3(x: 0.62f, y: 0.12f, z: 0.58f), material: SkinCream, smooth: 0.06f);

        // ---- the head: OVERSIZED (the chibi proportion — ~40% of the height), cream skin, sitting RIGHT on the body
        // Its bottom (centre 1.12 − radius ~0.44) overlaps the collar/torso top: no thin neck gap, so the two fuse.
        _ = PlaceNamed(scene: scene, name: "head", type: AvatarPrimitive.Sphere, position: new Vector3(x: 0f, y: 1.12f, z: 0.02f), scale: new Vector3(value: 1.14f), material: SkinCream, smooth: 0.12f);

        // ---- the face: two big white-eyeball eyes (dark pupil + emissive shine), a carved friendly smile ----------
        BuildFace(scene: scene);

        // ---- the cap: a solid pointed hat sitting ON the head crown (dome + peak), the top-of-silhouette identity --
        BuildCap(scene: scene);

        // ---- the signature accessory: a red backpack on the back, with a gold emissive emblem patch ---------------
        BuildBackpack(scene: scene);

        // ---- the limbs: four 2-link chains (arms, legs), short and THICK, cream hands + brown boots ---------------
        // The arm ROOTS start INSIDE the body (the shoulder overlaps the torso, ~±0.44 vs the body's ~±0.39 surface)
        // so the arms read as attached, never floating sticks; they hang down-and-slightly-out to plump little hands.
        // Legs are stubby, boots planted just below the belt.
        DefineLimb(scene: scene, name: "armLeft", root: new Vector3(x: -0.44f, y: 0.78f, z: 0.06f), mid: new Vector3(x: -0.56f, y: 0.58f, z: 0.08f), tip: new Vector3(x: -0.62f, y: 0.42f, z: 0.1f), material: TunicGreen, handMaterial: SkinCream);
        DefineLimb(scene: scene, name: "armRight", root: new Vector3(x: 0.44f, y: 0.78f, z: 0.06f), mid: new Vector3(x: 0.56f, y: 0.58f, z: 0.08f), tip: new Vector3(x: 0.62f, y: 0.42f, z: 0.1f), material: TunicGreen, handMaterial: SkinCream);
        DefineLimb(scene: scene, name: "legLeft", root: new Vector3(x: -0.26f, y: 0.34f, z: 0f), mid: new Vector3(x: -0.27f, y: 0.22f, z: 0.02f), tip: new Vector3(x: -0.28f, y: 0.1f, z: 0.08f), material: TunicGreen, handMaterial: BootBrown, foot: true);
        DefineLimb(scene: scene, name: "legRight", root: new Vector3(x: 0.26f, y: 0.34f, z: 0f), mid: new Vector3(x: 0.27f, y: 0.22f, z: 0.02f), tip: new Vector3(x: 0.28f, y: 0.1f, z: 0.08f), material: TunicGreen, handMaterial: BootBrown, foot: true);

        // ---- the march: the planted-foot walk-pair (frames 1-2), then two personality frames (a bob) --------------
        // Gait sweeps the "leg" chains through an ellipse, legLeft/legRight half a cycle apart — a confident stride.
        // Two frames is the bake's walk-pair convention (frames 1-2); AssertAdventurer requires exactly that they
        // exist and differ.
        _ = scene.Gait(prefix: "leg", frameCount: 2, stride: 0.42f);

        // Frames 3-4: a whole-body bob — the arms swing counter to the legs and the whole figure lifts a hair, so the
        // room's frame interpolation reads a lively march rather than only shuffling feet. The bake ignores frames
        // past 2, so this never touches the walk-pair convention.
        RecordBob(scene: scene);

        var document = scene.ToDocument();

        AssertStridePair(document: document);

        return document;
    }

    // The settled walk-pair convention (also enforced by RomForge.AssertAdventurer at bake time): frames 1-2 exist and
    // DIFFER (a real stride, not a frozen pose). Asserted here so the recipe fails loud if a future edit ever breaks
    // the gait, rather than producing a document that only fails much later at the bake.
    private static void AssertStridePair(CreationDocument document) {
        var frames = (document.Frames ?? throw new InvalidOperationException(message: "adventurer recipe recorded no frames."));

        if (frames.Count < 2) {
            throw new InvalidOperationException(message: $"adventurer recipe needs the walk-pair (frames 1-2); recorded {frames.Count}.");
        }

        var frame1 = frames[0];
        var frame2 = frames[1];
        var strideMoved = false;

        foreach (var transform in frame1.Transforms) {
            var counterpart = frame2.Transforms.FirstOrDefault(predicate: entry => (entry.Id == transform.Id));

            if ((counterpart is not null) && (Vector3.DistanceSquared(value1: transform.Position, value2: counterpart.Position) > 1e-8f)) {
                strideMoved = true;

                break;
            }
        }

        if (!strideMoved) {
            throw new InvalidOperationException(message: "adventurer recipe's walk-pair frames 1-2 are identical (no stride).");
        }
    }

    // Paints the recipe's named palette slots (the three-color story + eye/accent slots). Every value a literal.
    private static void PaintPalette(CreatorScene scene) {
        scene.SetPaletteEntry(index: TunicGreen, material: new SdfMaterial(Albedo: new Vector3(x: 0.28f, y: 0.60f, z: 0.38f)));
        scene.SetPaletteEntry(index: CapGreen, material: new SdfMaterial(Albedo: new Vector3(x: 0.17f, y: 0.44f, z: 0.28f)));
        scene.SetPaletteEntry(index: SkinCream, material: new SdfMaterial(Albedo: new Vector3(x: 0.96f, y: 0.86f, z: 0.70f)));
        scene.SetPaletteEntry(index: BootBrown, material: new SdfMaterial(Albedo: new Vector3(x: 0.40f, y: 0.26f, z: 0.16f)));
        scene.SetPaletteEntry(index: PackRed, material: new SdfMaterial(Albedo: new Vector3(x: 0.82f, y: 0.22f, z: 0.18f)));
        scene.SetPaletteEntry(index: EmblemGold, material: new SdfMaterial(Albedo: new Vector3(x: 0.96f, y: 0.79f, z: 0.34f), Emissive: 0.35f, Specular: 0.4f, Shininess: 48f));
        scene.SetPaletteEntry(index: EyeDark, material: new SdfMaterial(Albedo: new Vector3(x: 0.08f, y: 0.08f, z: 0.11f), Specular: 0.5f, Shininess: 64f));
        scene.SetPaletteEntry(index: EyeShine, material: new SdfMaterial(Albedo: new Vector3(x: 1.0f, y: 1.0f, z: 1.0f), Emissive: 0.6f));
    }

    // The face: a pair of big eyes on the head front (+Z), each a WHITE eyeball with a dark pupil and a bright
    // emissive shine on top — the reference's white-sclera / dark-pupil / white-shine stack, which is what makes the
    // eye read against a head in shadow (a dark-only eye vanishes). Each is placed ONCE and mirrored across the head's
    // X=0 plane, so one placement makes the symmetric pair (the reference's eyes-sym op — our Mirror flag). A carved
    // smile finishes the face. The head centre is (0, 1.12, 0.02), radius ~0.43; the eyes sit high-and-forward on it.
    // NOTE ON EYE SIZES: the scale envelope floors every axis at CreatorScene.MinScale (0.2), so a sphere's smallest
    // world radius is 0.2 × its 0.38 base ≈ 0.076. The eye stack works WITHIN that floor: a big eyeball (well above
    // the floor) with a smaller pupil (near it) reads as a classic wide cute eye; a third tinier "shine" sphere can't
    // go below the pupil, so we skip it — the bright emissive white eyeball already gives the eye its living spark.
    private static void BuildFace(CreatorScene scene) {
        // The white eyeball — a bright emissive white, placed once at +X and mirrored to −X for the symmetric pair.
        // Emissive so it glows out of the cap's shadow (a dark-only eye vanishes under the brim). Sized big-but-not-
        // dominating, set a touch wider apart so both read as a clear pair from the front.
        _ = PlaceNamed(scene: scene, name: "eyeWhite", type: AvatarPrimitive.Sphere, position: new Vector3(x: 0.26f, y: 1.16f, z: 0.32f), scale: new Vector3(value: 0.36f), material: EyeShine, mirror: true);
        // The pupil — a smaller dark sphere on the eyeball's front, sitting slightly inner so the two look a touch
        // toward each other (a warm, friendly gaze). Near the scale floor, so it reads as a neat dot in the white.
        _ = PlaceNamed(scene: scene, name: "pupil", type: AvatarPrimitive.Sphere, position: new Vector3(x: 0.24f, y: 1.14f, z: 0.46f), scale: new Vector3(value: 0.22f), material: EyeDark, mirror: true);
        // The smile: a small dark shape PLACED on the face (not carved) — a reliable, gentle read. A slim, gently-wide
        // ellipsoid laid across the lower face (modest X, thin Y) reads as a soft happy mouth; a subtract carve on a
        // small chibi head tends to gouge a gash, so a placed shape is the surer cute read. Dark brown, so it sits like
        // ink on the cream skin without a harsh black — kept small so it never becomes a muzzle/beak.
        _ = PlaceNamed(scene: scene, name: "smile", type: AvatarPrimitive.Ellipsoid, position: new Vector3(x: 0f, y: 0.98f, z: 0.44f), scale: new Vector3(x: 0.34f, y: 0.2f, z: 0.2f), material: BootBrown);
    }

    // The cap: a soft pointed adventurer's hat that SITS DOWN OVER the head — a wide dome band pulled low enough to
    // cover the whole crown (no exposed dark head-top ring, the "hollow bowl" the earlier pass suffered), a small
    // upturned brim ringing where hat meets head, then a RoundCone peak rising and flopping forward, finished by a
    // cream pom. Dome + brim + peak all group and smooth-union into ONE hat volume. Head centre (0,1.12,0.02), radius
    // ~0.43 (crown ~1.55). The dome sits LOW (centre 1.38) and WIDE so its lower edge wraps down past the crown.
    private static void BuildCap(CreatorScene scene) {
        // The dome: a broad squashed sphere pulled low over the crown — wide enough (X/Z 1.0) that its skirt hangs
        // below the crown line all around, so no dark head-top shows between face and hat. The group base for the hat.
        var domeId = PlaceNamed(scene: scene, name: "capDome", type: AvatarPrimitive.Sphere, position: new Vector3(x: 0f, y: 1.4f, z: 0.0f), scale: new Vector3(x: 1.0f, y: 0.5f, z: 1.0f), material: CapGreen, smooth: 0.12f);
        // The brim: a slim flattened ring at the hat's base, a hair wider than the dome — the little turned-up edge
        // that reads unmistakably as "hat brim." Smooth-unioned into the dome group.
        var brimId = PlaceNamed(scene: scene, name: "capBrim", type: AvatarPrimitive.Cylinder, position: new Vector3(x: 0f, y: 1.32f, z: 0.0f), scale: new Vector3(x: 1.06f, y: 0.1f, z: 1.04f), material: CapGreen, blend: SdfBlendOp.SmoothUnion, smooth: 0.1f);

        LinkInto(scene: scene, name: "capDome", partnerId: brimId);
        // The peak: a SHORT, stout round-cone rising from the dome and flopping forward — a jaunty little point, not a
        // tall gnome cone. Its base overlaps well down into the dome so they fuse; smooth-unioned into the group.
        var peakId = PlaceNamed(scene: scene, name: "capPeak", type: AvatarPrimitive.RoundCone, position: new Vector3(x: 0f, y: 1.5f, z: 0.12f), rotation: Tilt(pitchDegrees: 40f), scale: new Vector3(x: 0.6f, y: 0.52f, z: 0.6f), material: CapGreen, blend: SdfBlendOp.SmoothUnion, smooth: 0.18f);

        LinkInto(scene: scene, name: "capDome", partnerId: peakId);
        // The pom: a small cream bobble at the flopped-forward peak's tip — the terminal detail that finishes a hat.
        _ = PlaceNamed(scene: scene, name: "capPom", type: AvatarPrimitive.Sphere, position: new Vector3(x: 0f, y: 1.68f, z: 0.46f), scale: new Vector3(value: 0.22f), material: SkinCream);
    }

    // The backpack: a rounded red box hugging the body's back (−Z), a darker lid, a gold emissive emblem patch (the
    // identity accent — the deer-llama's yellow emblem), and two brown straps over the shoulders grounding it to the
    // body. Sits at the body's height so it reads as WORN, not floating. The pack + lid + emblem group so they fuse.
    private static void BuildBackpack(CreatorScene scene) {
        var packId = PlaceNamed(scene: scene, name: "pack", type: AvatarPrimitive.Box, position: new Vector3(x: 0f, y: 0.72f, z: -0.62f), scale: new Vector3(x: 0.66f, y: 0.72f, z: 0.4f), material: PackRed, smooth: 0.16f);
        // A rounded lid flap over the top of the pack, smooth-unioned into the pack's group.
        var lidId = PlaceNamed(scene: scene, name: "packLid", type: AvatarPrimitive.Box, position: new Vector3(x: 0f, y: 1.0f, z: -0.6f), scale: new Vector3(x: 0.7f, y: 0.24f, z: 0.44f), material: PackRed, blend: SdfBlendOp.SmoothUnion, smooth: 0.12f);

        LinkInto(scene: scene, name: "pack", partnerId: lidId);
        // The emblem: a small gold, faintly-glowing ring on the back of the pack — the one story detail cartoonified to
        // a badge. A torus laid flat against the pack's back reads as a stitched patch ring.
        var emblemId = PlaceNamed(scene: scene, name: "emblem", type: AvatarPrimitive.Torus, position: new Vector3(x: 0f, y: 0.72f, z: -0.84f), rotation: Tilt(pitchDegrees: 90f), scale: new Vector3(x: 0.5f, y: 0.5f, z: 0.5f), material: EmblemGold);

        LinkInto(scene: scene, name: "pack", partnerId: emblemId);
        // A gold stud in the emblem's centre (the "letter" dot) — a tiny sphere finishing the badge.
        _ = PlaceNamed(scene: scene, name: "emblemStud", type: AvatarPrimitive.Sphere, position: new Vector3(x: 0f, y: 0.72f, z: -0.86f), scale: new Vector3(value: 0.18f), material: EmblemGold);
        // The straps: one brown capsule strap over the shoulder, mirrored to the other side — grounds the pack to the
        // body so it does not read as floating.
        _ = PlaceNamed(scene: scene, name: "strap", type: AvatarPrimitive.Capsule, position: new Vector3(x: 0.36f, y: 0.86f, z: 0.16f), rotation: Tilt(pitchDegrees: 28f), scale: new Vector3(x: 0.13f, y: 0.46f, z: 0.13f), material: BootBrown, mirror: true);
    }

    // ---- authoring helpers (drive the SAME verbs the pad/console would) --------------------------------------------

    // Places one named shape at an exact transform: deselect (targets the ghost), cycle the ghost to the desired
    // primitive, set the exact transform/material/style, place, then select-and-rename the just-placed shape.
    private static int PlaceNamed(CreatorScene scene, string name, AvatarPrimitive type, Vector3 position, Vector3 scale, int material, Quaternion? rotation = null, float smooth = 0f, float onion = 0f, bool mirror = false, SdfBlendOp blend = SdfBlendOp.Union) {
        scene.Deselect();

        var steps = ((((int)type - (int)scene.GhostType) + CreatorScene.PrimitiveCount) % CreatorScene.PrimitiveCount);

        for (var step = 0; (step < steps); step++) {
            scene.CyclePrimitive(direction: 1);
        }

        _ = scene.SetTargetPosition(position: position);

        if (rotation is { } explicitRotation) {
            ApplyGhostRotation(scene: scene, rotation: explicitRotation);
        } else {
            scene.SetTargetRotation(yawDegrees: 0f, pitchDegrees: 0f, rollDegrees: 0f);
        }

        _ = scene.SetTargetScale(scale: scale);
        _ = scene.SetMaterialIndex(index: material);

        // Every style field is set UNCONDITIONALLY to its exact desired value — the ghost is sticky (it carries the
        // previous placement's blend/smooth/onion/mirror forward, exactly as it would for a player), so a recipe that
        // uses these on some shapes MUST reset them to neutral on the others or they bleed. Blend first, since it
        // coerces a group; then the continuous style; then mirror to its exact flag.
        scene.SetBlend(blend: blend);
        _ = scene.SetSmooth(value: smooth);
        _ = scene.SetOnion(value: onion);

        if (scene.GhostMirror != mirror) {
            _ = scene.ToggleMirror();
        }

        var placedId = NextShapeId(scene: scene);

        scene.Place();

        _ = scene.Select(idOrName: placedId.ToString(provider: System.Globalization.CultureInfo.InvariantCulture));
        _ = scene.RenameSelected(name: name);
        scene.Deselect();

        return placedId;
    }

    // Links a just-placed (blended) shape into the same composition group as a named base shape, so the base+blend
    // evaluate as one instance (select the base, then the partner, then link — the chain-link grouping verb).
    private static void LinkInto(CreatorScene scene, string name, int partnerId) {
        _ = scene.Select(idOrName: name);
        _ = scene.Select(idOrName: partnerId.ToString(provider: System.Globalization.CultureInfo.InvariantCulture));
        _ = scene.LinkWithPrevious();
        scene.Deselect();
    }

    // Decomposes a pure Y-axis (yaw) quaternion back to the yaw-degrees the console-assist rotation setter takes, and
    // for the tilt/pitch helpers reads the axis-angle directly — the SetTargetRotation path is the only rotation verb
    // a player has, so every placement rotates through it.
    private static void ApplyGhostRotation(CreatorScene scene, Quaternion rotation) {
        // Convert the quaternion to Tait-Bryan yaw/pitch/roll (Y*X*Z order — matching SetTargetRotation's compose).
        var q = Quaternion.Normalize(value: rotation);
        var sinPitch = (2f * ((q.W * q.X) - (q.Y * q.Z)));
        float yaw, pitch, roll;

        if (MathF.Abs(x: sinPitch) >= 0.9999f) {
            // Gimbal pole: fold roll into yaw.
            pitch = (MathF.CopySign(x: (MathF.PI / 2f), y: sinPitch));
            yaw = MathF.Atan2(y: (2f * ((q.W * q.Y) + (q.X * q.Z))), x: (1f - (2f * ((q.X * q.X) + (q.Y * q.Y)))));
            roll = 0f;
        } else {
            pitch = MathF.Asin(x: sinPitch);
            yaw = MathF.Atan2(y: (2f * ((q.W * q.Y) + (q.X * q.Z))), x: (1f - (2f * ((q.X * q.X) + (q.Y * q.Y)))));
            roll = MathF.Atan2(y: (2f * ((q.W * q.Z) + (q.X * q.Y))), x: (1f - (2f * ((q.X * q.X) + (q.Z * q.Z)))));
        }

        const float toDegrees = (180f / MathF.PI);

        scene.SetTargetRotation(yawDegrees: (yaw * toDegrees), pitchDegrees: (pitch * toDegrees), rollDegrees: (roll * toDegrees));
    }

    // A pure yaw (about +Y) as a quaternion — for shapes turned to face sideways.
    private static Quaternion TipForward(float degrees) =>
        Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: (degrees * (MathF.PI / 180f)));

    // A pure pitch (about +X) as a quaternion — for tilting the cap peak, laying the emblem/boot flat, etc.
    private static Quaternion Tilt(float pitchDegrees) =>
        Quaternion.CreateFromAxisAngle(axis: Vector3.UnitX, angle: (pitchDegrees * (MathF.PI / 180f)));

    // The id CreatorScene will hand the NEXT placed shape (mirrors the scene's private counter for a delete-free
    // recipe: ids assign 0, 1, 2, ... in place order, so the next id == PlacedCount).
    private static int NextShapeId(CreatorScene scene) => scene.PlacedCount;

    // Defines a "limb" (2-bone) chain from three FRESH shapes at root/mid/tip. Thick capsule bones read as chunky
    // little arms/legs; the tip is a hand (a cream sphere) or a boot (a brown ellipsoid toe stretched forward).
    private static void DefineLimb(CreatorScene scene, string name, Vector3 root, Vector3 mid, Vector3 tip, int material, int handMaterial, bool foot = false) {
        var rootId = PlaceNamed(scene: scene, name: $"{name}Root", type: AvatarPrimitive.Capsule, position: root, scale: new Vector3(value: 0.34f), material: material);
        var midId = PlaceNamed(scene: scene, name: $"{name}Mid", type: AvatarPrimitive.Capsule, position: mid, scale: new Vector3(value: 0.3f), material: material);
        int tipId;

        if (foot) {
            // A boot: an ellipsoid whose Z is stretched (radii 0.42/0.28/0.34 at unit scale, so scaling Z up makes a
            // toe) and X/Y kept stout — reads as a planted little boot pointing where the hero walks, not a donut.
            tipId = PlaceNamed(scene: scene, name: $"{name}Tip", type: AvatarPrimitive.Ellipsoid, position: tip, scale: new Vector3(x: 0.62f, y: 0.5f, z: 0.92f), material: handMaterial);
        } else {
            // A hand: a plump little cream sphere at the arm's end.
            tipId = PlaceNamed(scene: scene, name: $"{name}Tip", type: AvatarPrimitive.Sphere, position: tip, scale: new Vector3(value: 0.3f), material: handMaterial);
        }

        _ = scene.DefineChain(
            name: name,
            shapeIdsOrNames: [
                rootId.ToString(provider: System.Globalization.CultureInfo.InvariantCulture),
                midId.ToString(provider: System.Globalization.CultureInfo.InvariantCulture),
                tipId.ToString(provider: System.Globalization.CultureInfo.InvariantCulture),
            ],
            kind: CreatorChainState.KindLimb
        );
    }

    // Records frames 3-4: a two-pose whole-body bob layered over the walk. The gait already recorded frames 1-2 and
    // left the live scene at rest; here the arm chains swing counter to the legs' stride (arm-forward on frame 3,
    // arm-back on frame 4) and the whole figure lifts a hair, so the loop reads as a lively march. These frames sit
    // AFTER the walk-pair, so the bake (which consumes only frames 1-2) is untouched.
    private static void RecordBob(CreatorScene scene) {
        // Arms lead/trail opposite the reference leg — a natural counter-swing. armLeft forward with legRight, etc.
        var armLeftRest = FindChain(scene: scene, name: "armLeft").Goal;
        var armRightRest = FindChain(scene: scene, name: "armRight").Goal;

        // Frame 3: left arm swings forward (+Z), right arm back (−Z).
        SetChainGoal(scene: scene, chainName: "armLeft", goal: (armLeftRest + new Vector3(x: 0f, y: 0.02f, z: 0.22f)));
        SetChainGoal(scene: scene, chainName: "armRight", goal: (armRightRest + new Vector3(x: 0f, y: 0.02f, z: -0.22f)));
        _ = scene.RecordFrame();
        ResetTimelineCursor(scene: scene);

        // Frame 4: the mirror swing — right arm forward, left arm back.
        SetChainGoal(scene: scene, chainName: "armLeft", goal: (armLeftRest + new Vector3(x: 0f, y: 0.02f, z: -0.22f)));
        SetChainGoal(scene: scene, chainName: "armRight", goal: (armRightRest + new Vector3(x: 0f, y: 0.02f, z: 0.22f)));
        _ = scene.RecordFrame();
        ResetTimelineCursor(scene: scene);

        // Restore the arm goals to rest — the animation AUTHORS frames; it does not leave the live scene mid-swing.
        RestoreChainGoal(scene: scene, chainName: "armLeft", goal: armLeftRest);
        RestoreChainGoal(scene: scene, chainName: "armRight", goal: armRightRest);
    }

    // Finds a defined chain by name (the recipe always names its chains, so this never returns null in practice).
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
    // transforms. Harmless for the bob recorder: each frame re-sets the arm goals (re-solving) before RecordFrame.
    private static void ResetTimelineCursor(CreatorScene scene) {
        while (scene.CurrentFrame != 0) {
            _ = scene.StepFrame(direction: -1);
        }
    }
}
