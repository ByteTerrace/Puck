using System.Numerics;
using Puck.Demo.Creator;

namespace Puck.Demo.Forge;

/// <summary>
/// Authors Arc 3's three flagship avatars as <see cref="CreationDocument"/>s — committed content, not code: each
/// recipe drives a fresh, headless <see cref="CreatorScene"/> through EXACTLY the verbs a player has at the bench
/// (<see cref="CreatorScene.Place"/>, <see cref="CreatorScene.DefineChain"/>, <see cref="CreatorScene.Gait"/>,
/// <see cref="CreatorScene.RecordFrame"/>), then lifts <see cref="CreatorScene.ToDocument"/>. Nothing here reaches
/// past the player-reachable vocabulary: the six primitives, the scale envelope
/// [<see cref="CreatorScene.MinScale"/>, <see cref="CreatorScene.MaxScale"/>], the <see cref="CreatorScene.Capacity"/>
/// shape ceiling, the <see cref="CreatorScene.PaletteSize"/> palette, and chains as the RIG page defines them
/// ("limb" = exactly 3 shapes/2 bones, "spine" = any longer run). Deterministic by construction — no RNG, no
/// wall-clock, every value a literal — so re-running a recipe reproduces its committed
/// <c>docs/examples/creations/*.creation.json</c> byte-for-byte (see <see cref="RomForge.RunFlagshipsAsync"/>'s
/// regeneration proof).
/// </summary>
internal static class FlagshipCreations {
    /// <summary>The synthetic authoring bench the recipes build against — the same proportions
    /// <c>OverworldFrameSource</c> derives for the live room (4-unit horizontal half-extent, a floor-relative
    /// vertical band), just centred on the origin since a recipe has no room to sit inside.</summary>
    private static WorkbenchRegion Bench => new(Center: Vector3.Zero, HalfExtent: 4f, MinY: 0.35f, MaxY: 3.0f);

    // ---- the lantern fish: a 6-link SPINE (the tapering body), a 2-link lure stalk (the camera-lure), two fins -----

    /// <summary>Builds the lantern-fish document: a body tapering along a 6-link spine chain, a 2-link lure stalk
    /// arcing overhead, and two flank fins. The swim cycle sweeps the spine's goal through a sinusoid (four frames,
    /// so the cycle loops without a jump-cut) and records each pose — <see cref="RomForge.RunFlagshipsAsync"/> then
    /// asserts the spine solves without NaN and visibly curves across the recorded frames.</summary>
    /// <returns>The normalized document, ready for <see cref="CreationStore.ToJson"/> or the bake.</returns>
    public static CreationDocument BuildLanternFish() {
        var scene = new CreatorScene(workbench: Bench);

        scene.SetActive(active: true);
        scene.SetName(name: "lantern-fish");
        scene.SetIntent(intent: CreatorIntent.Object);

        // The spine: 6 ellipsoids strung nose→tail along -Z, each a little smaller than the last (the taper), so the
        // chain's rest geometry (captured from these very positions) reads as a body even before any goal moves it.
        var spineCount = 6;
        var spineIds = new int[spineCount];

        for (var index = 0; (index < spineCount); index++) {
            var t = (index / (float)(spineCount - 1));
            var position = new Vector3(x: 0f, y: 1.4f, z: (1.0f - (t * 2.0f)));
            var scale = (1.15f - (t * 0.7f));

            spineIds[index] = PlaceNamed(scene: scene, name: $"spine{index}", type: AvatarPrimitive.Ellipsoid, position: position, scale: new Vector3(x: (scale * 0.9f), y: (scale * 0.62f), z: scale), material: (index % CreatorScene.PaletteSize));
        }

        _ = scene.DefineChain(name: "spine", shapeIdsOrNames: [.. spineIds.Select(selector: static id => id.ToString(provider: System.Globalization.CultureInfo.InvariantCulture))], kind: CreatorChainState.KindSpine);

        // The lure stalk: 2 shapes (one bone) arcing up and forward from the head — a "spine" chain too (only a
        // 3-shape run is ever "limb"), so it drags/lags behind the head the same gentle way the body does. The bulb
        // at its tip IS the camera-lure the game-studio plan names.
        var stalkBaseId = PlaceNamed(scene: scene, name: "lureStalk", type: AvatarPrimitive.Capsule, position: new Vector3(x: 0f, y: 1.95f, z: 1.05f), scale: new Vector3(value: 0.24f), material: 6);
        var stalkTipId = PlaceNamed(scene: scene, name: "lureBulb", type: AvatarPrimitive.Sphere, position: new Vector3(x: 0f, y: 2.55f, z: 1.55f), scale: new Vector3(value: 0.34f), material: 7);

        _ = scene.DefineChain(name: "lure", shapeIdsOrNames: [stalkBaseId.ToString(provider: System.Globalization.CultureInfo.InvariantCulture), stalkTipId.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)], kind: CreatorChainState.KindSpine);

        // Two flank fins — plain placed shapes (not rigged; a fin reads fine as a flattened, twisted ellipsoid
        // riding the body, no goal needed).
        _ = PlaceNamed(scene: scene, name: "finLeft", type: AvatarPrimitive.Ellipsoid, position: new Vector3(x: -0.62f, y: 1.1f, z: -0.1f), rotation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: (-0.5f * MathF.PI)), scale: new Vector3(x: 0.5f, y: 0.22f, z: 0.32f), material: 3);
        _ = PlaceNamed(scene: scene, name: "finRight", type: AvatarPrimitive.Ellipsoid, position: new Vector3(x: 0.62f, y: 1.1f, z: -0.1f), rotation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: (0.5f * MathF.PI)), scale: new Vector3(x: 0.5f, y: 0.22f, z: 0.32f), material: 3);

        // The swim cycle: sweep the SPINE chain's goal through one full sinusoid, recording 4 frames (a clean loop —
        // frame 4 hands back off to frame 1 with no jump). This is the SAME sweep-and-record shape
        // CreatorScene.Gait uses internally (move the goal, SolveChains via the goal setter, RecordFrame); driven
        // directly here since a single-chain sinusoidal sweep (not a name-prefixed multi-chain gait) is the fish's
        // own cycle, not a walk. Each iteration resets the timeline cursor to REST first (harmless — the very next
        // step moves the goal again before recording), so RecordFrame APPENDS a new frame instead of overwriting
        // frame 1 on every pass.
        var restGoal = FindChain(scene: scene, name: "spine").Goal;
        const int swimFrames = 4;

        for (var frame = 0; (frame < swimFrames); frame++) {
            var phase = ((frame / (float)swimFrames) * MathF.Tau);
            var sway = new Vector3(x: (MathF.Sin(x: phase) * 0.55f), y: 0f, z: 0f);

            ResetTimelineCursor(scene: scene);
            SetChainGoal(scene: scene, chainName: "spine", goal: (restGoal + sway));
            _ = scene.RecordFrame();
        }

        ResetTimelineCursor(scene: scene);
        RestoreChainGoal(scene: scene, chainName: "spine", goal: restGoal);

        return scene.ToDocument();
    }

    // ---- the CRT-faced robot: boxy body, a named "face" plate, four 2-link LIMB chains ----------------------------

    /// <summary>Builds the crt-robot document: a boxy torso, a flattened, slightly-curved "face" plate (the
    /// screen-slab assignment lives at the host's ledger by NAME, never in the document), and four 2-link limb
    /// chains (arms, legs). Idle and wave poses land as frames 1-2.</summary>
    /// <returns>The normalized document.</returns>
    public static CreationDocument BuildCrtRobot() {
        var scene = new CreatorScene(workbench: Bench);

        scene.SetActive(active: true);
        scene.SetName(name: "crt-robot");
        scene.SetIntent(intent: CreatorIntent.Object);

        _ = PlaceNamed(scene: scene, name: "body", type: AvatarPrimitive.Box, position: new Vector3(x: 0f, y: 1.35f, z: 0f), scale: new Vector3(x: 0.95f, y: 1.05f, z: 0.7f), material: 0);

        // The face: a flattened plate given a small Onion shell (a rounded rim rather than a razor-flat decal, the
        // "slightly-curved" read) mounted on the body's front — named "face" per the flagship contract; the host
        // decides what a shape named this way becomes (the screen-slab assignment is a ledger concern, not this
        // document's).
        _ = PlaceNamed(scene: scene, name: "face", type: AvatarPrimitive.Box, position: new Vector3(x: 0f, y: 1.55f, z: 0.62f), scale: new Vector3(x: 0.62f, y: 0.5f, z: 0.2f), material: 1, onion: 0.05f);

        // Four 2-link LIMB chains (arms, legs) — each a 3-shape/2-bone run, so CreatorChainState infers "limb"
        // without an explicit kind (still passed explicitly for readability and to survive a future default change).
        DefineLimb(scene: scene, name: "armLeft", root: new Vector3(x: -0.85f, y: 1.55f, z: 0f), mid: new Vector3(x: -1.25f, y: 1.15f, z: 0f), tip: new Vector3(x: -1.35f, y: 0.7f, z: 0f), material: 2);
        DefineLimb(scene: scene, name: "armRight", root: new Vector3(x: 0.85f, y: 1.55f, z: 0f), mid: new Vector3(x: 1.25f, y: 1.15f, z: 0f), tip: new Vector3(x: 1.35f, y: 0.7f, z: 0f), material: 2);
        DefineLimb(scene: scene, name: "legLeft", root: new Vector3(x: -0.4f, y: 0.75f, z: 0f), mid: new Vector3(x: -0.42f, y: 0.4f, z: 0f), tip: new Vector3(x: -0.42f, y: 0.05f, z: 0f), material: 4);
        DefineLimb(scene: scene, name: "legRight", root: new Vector3(x: 0.4f, y: 0.75f, z: 0f), mid: new Vector3(x: 0.42f, y: 0.4f, z: 0f), tip: new Vector3(x: 0.42f, y: 0.05f, z: 0f), material: 4);

        // Frame 1 (idle): every limb goal held at its rest tip — record it as the neutral pose.
        _ = scene.RecordFrame();
        ResetTimelineCursor(scene: scene);

        // Frame 2 (wave): armRight's goal lifts up and out, the rest hold rest — an emote pose distinct from idle.
        var armRightRestGoal = FindChain(scene: scene, name: "armRight").Goal;

        SetChainGoal(scene: scene, chainName: "armRight", goal: new Vector3(x: 1.15f, y: 2.05f, z: 0.15f));
        _ = scene.RecordFrame();
        ResetTimelineCursor(scene: scene);
        RestoreChainGoal(scene: scene, chainName: "armRight", goal: armRightRestGoal);

        return scene.ToDocument();
    }

    // ---- the adventurer: the classic top-down humanoid, 2-link arms/legs, a planted-foot walk stride at frames 1-2 --

    /// <summary>Builds the adventurer document: a torso/head and four 2-link limb chains (arms, legs), with a
    /// planted-foot walk stride recorded at frames 1-2 via the SAME ellipse-sweep <see cref="CreatorScene.Gait"/>
    /// uses (legLeft/legRight alternate half a cycle apart, the bake's walk-pair convention).</summary>
    /// <returns>The normalized document.</returns>
    public static CreationDocument BuildAdventurer() {
        var scene = new CreatorScene(workbench: Bench);

        scene.SetActive(active: true);
        scene.SetName(name: "adventurer");
        scene.SetIntent(intent: CreatorIntent.Object);

        _ = PlaceNamed(scene: scene, name: "torso", type: AvatarPrimitive.Capsule, position: new Vector3(x: 0f, y: 1.15f, z: 0f), scale: new Vector3(value: 0.62f), material: 0);
        _ = PlaceNamed(scene: scene, name: "head", type: AvatarPrimitive.Sphere, position: new Vector3(x: 0f, y: 1.85f, z: 0f), scale: new Vector3(value: 0.55f), material: 5);

        DefineLimb(scene: scene, name: "armLeft", root: new Vector3(x: -0.55f, y: 1.5f, z: 0f), mid: new Vector3(x: -0.75f, y: 1.1f, z: 0f), tip: new Vector3(x: -0.8f, y: 0.65f, z: 0f), material: 2);
        DefineLimb(scene: scene, name: "armRight", root: new Vector3(x: 0.55f, y: 1.5f, z: 0f), mid: new Vector3(x: 0.75f, y: 1.1f, z: 0f), tip: new Vector3(x: 0.8f, y: 0.65f, z: 0f), material: 2);
        DefineLimb(scene: scene, name: "legLeft", root: new Vector3(x: -0.28f, y: 0.75f, z: 0f), mid: new Vector3(x: -0.3f, y: 0.4f, z: 0f), tip: new Vector3(x: -0.3f, y: 0.05f, z: 0f), material: 4);
        DefineLimb(scene: scene, name: "legRight", root: new Vector3(x: 0.28f, y: 0.75f, z: 0f), mid: new Vector3(x: 0.3f, y: 0.4f, z: 0f), tip: new Vector3(x: 0.3f, y: 0.05f, z: 0f), material: 4);

        // The walk-pair convention: creator.gait's own ellipse-sweep math, invoked on the "leg" name prefix so
        // legLeft/legRight (the first/second half of the 2-member match set) land half a cycle apart — a
        // planted-foot stride. Two frames is the bake's walk-pair (frames 1-2).
        _ = scene.Gait(prefix: "leg", frameCount: 2, stride: 0.4f);

        return scene.ToDocument();
    }

    // ---- shared authoring helpers (drive the SAME verbs the pad/console would) -------------------------------------

    // Places one named shape at an exact transform: deselect (targets the ghost), cycle the ghost to the desired
    // primitive from its always-Sphere-at-construction start, set the exact transform/material, place, then select
    // and rename the just-placed shape (RenameSelected needs a real selection, never the ghost).
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

        _ = scene.Select(idOrName: placedId.ToString(provider: System.Globalization.CultureInfo.InvariantCulture));
        _ = scene.RenameSelected(name: name);
        scene.Deselect();

        return placedId;
    }

    // SetTargetRotation only takes Tait-Bryan degrees (the console-assist precision path a player has, no raw
    // quaternion setter exists). Every caller in this file only ever passes a pure Y-axis rotation
    // (Quaternion.CreateFromAxisAngle(UnitY, angle)) for the fins, so this decomposes that back to an exact yaw
    // degrees value instead of pretending to support the general axis-angle case no flagship needs.
    private static void ApplyGhostRotation(CreatorScene scene, Quaternion rotation) {
        var angle = (2f * MathF.Atan2(x: rotation.W, y: rotation.Y));

        scene.SetTargetRotation(yawDegrees: (angle * (180f / MathF.PI)), pitchDegrees: 0f, rollDegrees: 0f);
    }

    // The id CreatorScene will hand the NEXT placed shape — mirrors the scene's private m_nextShapeId counter, which
    // for a freshly authored recipe (no deletes) always equals PlacedCount (ids assign 0, 1, 2, ... in place order).
    private static int NextShapeId(CreatorScene scene) => scene.PlacedCount;

    // Defines a "limb" (2-bone) chain from three FRESH shapes placed at the given root/mid/tip — small unit spheres
    // acting as joints, exactly what a player would place before linking them into a chain via the RIG page.
    private static void DefineLimb(CreatorScene scene, string name, Vector3 root, Vector3 mid, Vector3 tip, int material) {
        var rootId = PlaceNamed(scene: scene, name: $"{name}Root", type: AvatarPrimitive.Capsule, position: root, scale: new Vector3(value: 0.26f), material: material);
        var midId = PlaceNamed(scene: scene, name: $"{name}Mid", type: AvatarPrimitive.Capsule, position: mid, scale: new Vector3(value: 0.22f), material: material);
        var tipId = PlaceNamed(scene: scene, name: $"{name}Tip", type: AvatarPrimitive.Sphere, position: tip, scale: new Vector3(value: 0.2f), material: material);

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

    // Finds a defined chain by name (recipes always name theirs, so this never returns null in practice here).
    private static CreatorChainState FindChain(CreatorScene scene, string name) {
        foreach (var chain in scene.Chains) {
            if (string.Equals(a: chain.Name, b: name, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                return chain;
            }
        }

        throw new InvalidOperationException(message: $"No chain named '{name}' is defined.");
    }

    // Moves a named chain's goal to an EXACT position by selecting it as the target (CycleSelection walks the
    // combined shape+goal cursor space) and driving SetTargetPosition — the same console-assist precision path the
    // RIG page's goal nudge stands in for on the pad.
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
    // shape, in chain-definition order — see CreatorScene.CycleSelection's combined cursor-space remarks): from a
    // freshly deselected (ghost) target, cycling forward PlacedCount times lands on goal index 0, one further step
    // per chain position after that.
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

    // Walks the timeline cursor back to REST (index 0) via the public StepFrame/SetFrame path — which DOES restore
    // the rest pose's transforms (ApplyPoses), unlike CreatorScene.Gait's private bare-field reset. That is harmless
    // for every caller here: each sweep iteration re-sets the chain goal (which re-solves) immediately after this
    // call and before the next RecordFrame, so the momentarily-restored rest pose never gets recorded.
    private static void ResetTimelineCursor(CreatorScene scene) {
        while (scene.CurrentFrame != 0) {
            _ = scene.StepFrame(direction: -1);
        }
    }
}
