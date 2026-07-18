using System.Numerics;
using Puck.Demo.Creator;
using Puck.Demo.Forge;
using Puck.SdfVm;

namespace Puck.Demo.Town;

/// <summary>
/// Authors the town street's six small props as <see cref="CreationDocument"/>s — committed content, not code: each
/// recipe drives a fresh, headless <see cref="CreatorScene"/> through EXACTLY the verbs a player has at the bench
/// (<see cref="CreatorScene.Place"/>, <see cref="CreatorScene.SetPaletteEntry"/>), then lifts
/// <see cref="CreatorScene.ToDocument"/>. Mirrors the <c>FlagshipCreations</c> discipline (see
/// <see cref="Forge.FlagshipCreations"/>): no rigging here, just plain-placed shapes under Union, so every prop is a
/// small, deterministic stamp-many street object. Deterministic by construction — no RNG, no wall-clock, every value
/// a literal — so re-running a recipe reproduces its committed document byte-for-byte.
/// </summary>
internal static class TownProps {
    /// <summary>The synthetic authoring bench the recipes build against — the same envelope
    /// <c>FlagshipCreations.Bench</c> uses, centred on the origin since a recipe has no room to sit inside.</summary>
    private static WorkbenchRegion Bench => new(Center: Vector3.Zero, HalfExtent: 4f, MinY: 0.35f, MaxY: 3.0f);

    /// <summary>The six town props in registry order, paired with their creation name.</summary>
    /// <returns>Each prop's name and normalized document.</returns>
    internal static (string Name, CreationDocument Document)[] All() => [
        ("town-lamp", BuildLamp()),
        ("town-tree", BuildTree()),
        ("town-bench", BuildBench()),
        ("town-planter", BuildPlanter()),
        ("town-mailbox", BuildMailbox()),
        ("town-fountain", BuildFountain()),
    ];

    // ---- town-lamp: the stamp-many hero — post, base, cross-arm, glowing amber head (5 shapes) --------------------

    /// <summary>Builds the town-lamp document: a small base, a thin tall post, a short cross-arm, and a glowing
    /// amber lamp head — the fixture repeated along both sidewalks.</summary>
    /// <returns>The normalized document.</returns>
    public static CreationDocument BuildLamp() {
        var scene = new CreatorScene(workbench: Bench);

        scene.SetActive(active: true);
        scene.SetName(name: "town-lamp");
        scene.SetIntent(intent: CreatorIntent.Object);

        scene.SetPaletteEntry(index: 0, material: new SdfMaterial(Albedo: new Vector3(x: 0.22f, y: 0.22f, z: 0.24f)));
        scene.SetPaletteEntry(index: 1, material: new SdfMaterial(Albedo: new Vector3(x: 0.18f, y: 0.18f, z: 0.2f)));
        scene.SetPaletteEntry(index: 2, material: new SdfMaterial(Albedo: new Vector3(x: 1.0f, y: 0.7f, z: 0.3f), Emissive: 3.0f));

        _ = PlaceNamed(scene: scene, name: "base", type: AvatarPrimitive.Cylinder, position: new Vector3(x: 0f, y: 0.42f, z: 0f), scale: new Vector3(x: 0.4f, y: 0.16f, z: 0.4f), material: 1);
        _ = PlaceNamed(scene: scene, name: "post", type: AvatarPrimitive.Cylinder, position: new Vector3(x: 0f, y: 1.55f, z: 0f), scale: new Vector3(x: 0.12f, y: 1.1f, z: 0.12f), material: 0);
        _ = PlaceNamed(scene: scene, name: "crossArm", type: AvatarPrimitive.Cylinder, position: new Vector3(x: 0f, y: 2.5f, z: 0f), rotation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitZ, angle: (0.5f * MathF.PI)), scale: new Vector3(x: 0.06f, y: 0.34f, z: 0.06f), material: 0);
        _ = PlaceNamed(scene: scene, name: "head", type: AvatarPrimitive.Ellipsoid, position: new Vector3(x: 0f, y: 2.68f, z: 0f), scale: new Vector3(x: 0.3f, y: 0.24f, z: 0.3f), material: 2);

        return scene.ToDocument();
    }

    // ---- town-tree: trunk + two foliage blobs (3 shapes) ------------------------------------------------------------

    /// <summary>Builds the town-tree document: a brown trunk and two green foliage blobs, plain-unioned.</summary>
    /// <returns>The normalized document.</returns>
    public static CreationDocument BuildTree() {
        var scene = new CreatorScene(workbench: Bench);

        scene.SetActive(active: true);
        scene.SetName(name: "town-tree");
        scene.SetIntent(intent: CreatorIntent.Object);

        scene.SetPaletteEntry(index: 0, material: new SdfMaterial(Albedo: new Vector3(x: 0.36f, y: 0.24f, z: 0.16f)));
        scene.SetPaletteEntry(index: 1, material: new SdfMaterial(Albedo: new Vector3(x: 0.24f, y: 0.42f, z: 0.2f)));
        scene.SetPaletteEntry(index: 2, material: new SdfMaterial(Albedo: new Vector3(x: 0.28f, y: 0.48f, z: 0.24f)));

        _ = PlaceNamed(scene: scene, name: "trunk", type: AvatarPrimitive.Capsule, position: new Vector3(x: 0f, y: 1.05f, z: 0f), scale: new Vector3(x: 0.22f, y: 0.9f, z: 0.22f), material: 0);
        _ = PlaceNamed(scene: scene, name: "foliageLower", type: AvatarPrimitive.Ellipsoid, position: new Vector3(x: 0f, y: 1.85f, z: 0f), scale: new Vector3(x: 0.85f, y: 0.65f, z: 0.85f), material: 1);
        _ = PlaceNamed(scene: scene, name: "foliageUpper", type: AvatarPrimitive.Ellipsoid, position: new Vector3(x: 0f, y: 2.35f, z: 0f), scale: new Vector3(x: 0.62f, y: 0.5f, z: 0.62f), material: 2);

        return scene.ToDocument();
    }

    // ---- town-bench: seat + back + two legs (4 shapes) ------------------------------------------------------------

    /// <summary>Builds the town-bench document: a flat seat, a flat back, and two legs, all wood-brown.</summary>
    /// <returns>The normalized document.</returns>
    public static CreationDocument BuildBench() {
        var scene = new CreatorScene(workbench: Bench);

        scene.SetActive(active: true);
        scene.SetName(name: "town-bench");
        scene.SetIntent(intent: CreatorIntent.Object);

        scene.SetPaletteEntry(index: 0, material: new SdfMaterial(Albedo: new Vector3(x: 0.4f, y: 0.27f, z: 0.16f)));
        scene.SetPaletteEntry(index: 1, material: new SdfMaterial(Albedo: new Vector3(x: 0.16f, y: 0.16f, z: 0.17f)));

        _ = PlaceNamed(scene: scene, name: "seat", type: AvatarPrimitive.Box, position: new Vector3(x: 0f, y: 0.62f, z: 0f), scale: new Vector3(x: 0.9f, y: 0.06f, z: 0.42f), material: 0);
        _ = PlaceNamed(scene: scene, name: "back", type: AvatarPrimitive.Box, position: new Vector3(x: 0f, y: 0.95f, z: -0.36f), rotation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitX, angle: (-0.12f * MathF.PI)), scale: new Vector3(x: 0.9f, y: 0.32f, z: 0.05f), material: 0);
        _ = PlaceNamed(scene: scene, name: "legLeft", type: AvatarPrimitive.Box, position: new Vector3(x: -0.75f, y: 0.42f, z: 0f), scale: new Vector3(x: 0.07f, y: 0.24f, z: 0.38f), material: 1);
        _ = PlaceNamed(scene: scene, name: "legRight", type: AvatarPrimitive.Box, position: new Vector3(x: 0.75f, y: 0.42f, z: 0f), scale: new Vector3(x: 0.07f, y: 0.24f, z: 0.38f), material: 1);

        return scene.ToDocument();
    }

    // ---- town-planter: planter box + bush blob (2 shapes) ---------------------------------------------------------

    /// <summary>Builds the town-planter document: a wood-brown planter box topped by a green bush blob.</summary>
    /// <returns>The normalized document.</returns>
    public static CreationDocument BuildPlanter() {
        var scene = new CreatorScene(workbench: Bench);

        scene.SetActive(active: true);
        scene.SetName(name: "town-planter");
        scene.SetIntent(intent: CreatorIntent.Object);

        scene.SetPaletteEntry(index: 0, material: new SdfMaterial(Albedo: new Vector3(x: 0.38f, y: 0.26f, z: 0.17f)));
        scene.SetPaletteEntry(index: 1, material: new SdfMaterial(Albedo: new Vector3(x: 0.26f, y: 0.44f, z: 0.22f)));

        _ = PlaceNamed(scene: scene, name: "box", type: AvatarPrimitive.Box, position: new Vector3(x: 0f, y: 0.6f, z: 0f), scale: new Vector3(x: 0.5f, y: 0.25f, z: 0.5f), material: 0);
        _ = PlaceNamed(scene: scene, name: "bush", type: AvatarPrimitive.Sphere, position: new Vector3(x: 0f, y: 1.02f, z: 0f), scale: new Vector3(x: 0.46f, y: 0.4f, z: 0.46f), material: 1);

        return scene.ToDocument();
    }

    // ---- town-mailbox: post + rounded body + small flag (3 shapes) -------------------------------------------------

    /// <summary>Builds the town-mailbox document: a thin post, a rounded body, and a small red flag — a whimsy
    /// street piece.</summary>
    /// <returns>The normalized document.</returns>
    public static CreationDocument BuildMailbox() {
        var scene = new CreatorScene(workbench: Bench);

        scene.SetActive(active: true);
        scene.SetName(name: "town-mailbox");
        scene.SetIntent(intent: CreatorIntent.Object);

        scene.SetPaletteEntry(index: 0, material: new SdfMaterial(Albedo: new Vector3(x: 0.2f, y: 0.2f, z: 0.22f)));
        scene.SetPaletteEntry(index: 1, material: new SdfMaterial(Albedo: new Vector3(x: 0.32f, y: 0.34f, z: 0.38f)));
        scene.SetPaletteEntry(index: 2, material: new SdfMaterial(Albedo: new Vector3(x: 0.72f, y: 0.14f, z: 0.12f)));

        _ = PlaceNamed(scene: scene, name: "post", type: AvatarPrimitive.Cylinder, position: new Vector3(x: 0f, y: 0.72f, z: 0f), scale: new Vector3(x: 0.08f, y: 0.55f, z: 0.08f), material: 0);
        _ = PlaceNamed(scene: scene, name: "body", type: AvatarPrimitive.Capsule, position: new Vector3(x: 0f, y: 1.34f, z: 0f), rotation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitZ, angle: (0.5f * MathF.PI)), scale: new Vector3(x: 0.2f, y: 0.34f, z: 0.2f), material: 1);
        _ = PlaceNamed(scene: scene, name: "flag", type: AvatarPrimitive.Box, position: new Vector3(x: 0.24f, y: 1.42f, z: 0.05f), scale: new Vector3(x: 0.03f, y: 0.16f, z: 0.1f), material: 2);

        return scene.ToDocument();
    }

    // ---- town-fountain: town-square centerpiece — rim, wall, column, water disc, spout bulb (5 shapes) --------------

    /// <summary>Builds the town-fountain document: a torus rim, a short basin wall, a central column, a faintly
    /// emissive teal water disc, and a spout bulb — the town square's centerpiece.</summary>
    /// <returns>The normalized document.</returns>
    public static CreationDocument BuildFountain() {
        var scene = new CreatorScene(workbench: Bench);

        scene.SetActive(active: true);
        scene.SetName(name: "town-fountain");
        scene.SetIntent(intent: CreatorIntent.Object);

        scene.SetPaletteEntry(index: 0, material: new SdfMaterial(Albedo: new Vector3(x: 0.62f, y: 0.6f, z: 0.58f)));
        scene.SetPaletteEntry(index: 1, material: new SdfMaterial(Albedo: new Vector3(x: 0.7f, y: 0.68f, z: 0.64f)));
        scene.SetPaletteEntry(index: 2, material: new SdfMaterial(Albedo: new Vector3(x: 0.16f, y: 0.42f, z: 0.44f), Emissive: 0.5f));

        _ = PlaceNamed(scene: scene, name: "wall", type: AvatarPrimitive.Cylinder, position: new Vector3(x: 0f, y: 0.55f, z: 0f), scale: new Vector3(x: 1.4f, y: 0.2f, z: 1.4f), material: 0);
        _ = PlaceNamed(scene: scene, name: "rim", type: AvatarPrimitive.Torus, position: new Vector3(x: 0f, y: 0.75f, z: 0f), scale: new Vector3(x: 1.45f, y: 0.16f, z: 1.45f), material: 1);
        _ = PlaceNamed(scene: scene, name: "water", type: AvatarPrimitive.Cylinder, position: new Vector3(x: 0f, y: 0.7f, z: 0f), scale: new Vector3(x: 1.2f, y: 0.03f, z: 1.2f), material: 2);
        _ = PlaceNamed(scene: scene, name: "column", type: AvatarPrimitive.Cylinder, position: new Vector3(x: 0f, y: 1.15f, z: 0f), scale: new Vector3(x: 0.22f, y: 0.6f, z: 0.22f), material: 0);
        _ = PlaceNamed(scene: scene, name: "spout", type: AvatarPrimitive.Sphere, position: new Vector3(x: 0f, y: 1.78f, z: 0f), scale: new Vector3(x: 0.22f, y: 0.22f, z: 0.22f), material: 1);

        return scene.ToDocument();
    }

    // ---- shared authoring helper (the SAME verb sequence FlagshipCreations.PlaceNamed drives) -----------------------

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

        var placedId = scene.PlacedCount;

        scene.Place();

        _ = scene.Select(idOrName: placedId.ToString(provider: System.Globalization.CultureInfo.InvariantCulture));
        _ = scene.RenameSelected(name: name);
        scene.Deselect();

        return placedId;
    }

    // SetTargetRotation only takes Tait-Bryan degrees (the console-assist precision path a player has, no raw
    // quaternion setter exists). Every caller in this file only ever passes a pure axis-aligned rotation
    // (Quaternion.CreateFromAxisAngle), so this decomposes an X- or Z-axis rotation back to exact pitch/roll degrees;
    // the X-axis (pitch) and Z-axis (roll) callers here never combine, so recovering whichever axis is non-identity
    // as its matching Tait-Bryan angle round-trips exactly.
    private static void ApplyGhostRotation(CreatorScene scene, Quaternion rotation) {
        var angle = (2f * MathF.Atan2(MathF.Sqrt(x: (((rotation.X * rotation.X) + (rotation.Y * rotation.Y)) + (rotation.Z * rotation.Z))), rotation.W));

        if (rotation.X != 0f) {
            scene.SetTargetRotation(yawDegrees: 0f, pitchDegrees: ((angle * (180f / MathF.PI)) * MathF.Sign(x: rotation.X)), rollDegrees: 0f);
        } else if (rotation.Z != 0f) {
            scene.SetTargetRotation(yawDegrees: 0f, pitchDegrees: 0f, rollDegrees: ((angle * (180f / MathF.PI)) * MathF.Sign(x: rotation.Z)));
        } else {
            scene.SetTargetRotation(yawDegrees: ((angle * (180f / MathF.PI)) * MathF.Sign(x: rotation.Y)), pitchDegrees: 0f, rollDegrees: 0f);
        }
    }
}
