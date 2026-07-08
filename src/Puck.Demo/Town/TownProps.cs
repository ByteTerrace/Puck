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

        scene.SetPaletteEntry(index: 0, material: new SdfMaterial(Albedo: new Vector3(0.22f, 0.22f, 0.24f)));
        scene.SetPaletteEntry(index: 1, material: new SdfMaterial(Albedo: new Vector3(0.18f, 0.18f, 0.2f)));
        scene.SetPaletteEntry(index: 2, material: new SdfMaterial(Albedo: new Vector3(1.0f, 0.7f, 0.3f), Emissive: 3.0f));

        _ = PlaceNamed(scene: scene, name: "base", type: AvatarPrimitive.Cylinder, position: new Vector3(0f, 0.42f, 0f), scale: new Vector3(0.4f, 0.16f, 0.4f), material: 1);
        _ = PlaceNamed(scene: scene, name: "post", type: AvatarPrimitive.Cylinder, position: new Vector3(0f, 1.55f, 0f), scale: new Vector3(0.12f, 1.1f, 0.12f), material: 0);
        _ = PlaceNamed(scene: scene, name: "crossArm", type: AvatarPrimitive.Cylinder, position: new Vector3(0f, 2.5f, 0f), rotation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitZ, angle: (0.5f * MathF.PI)), scale: new Vector3(0.06f, 0.34f, 0.06f), material: 0);
        _ = PlaceNamed(scene: scene, name: "head", type: AvatarPrimitive.Ellipsoid, position: new Vector3(0f, 2.68f, 0f), scale: new Vector3(0.3f, 0.24f, 0.3f), material: 2);

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

        scene.SetPaletteEntry(index: 0, material: new SdfMaterial(Albedo: new Vector3(0.36f, 0.24f, 0.16f)));
        scene.SetPaletteEntry(index: 1, material: new SdfMaterial(Albedo: new Vector3(0.24f, 0.42f, 0.2f)));
        scene.SetPaletteEntry(index: 2, material: new SdfMaterial(Albedo: new Vector3(0.28f, 0.48f, 0.24f)));

        _ = PlaceNamed(scene: scene, name: "trunk", type: AvatarPrimitive.Capsule, position: new Vector3(0f, 1.05f, 0f), scale: new Vector3(0.22f, 0.9f, 0.22f), material: 0);
        _ = PlaceNamed(scene: scene, name: "foliageLower", type: AvatarPrimitive.Ellipsoid, position: new Vector3(0f, 1.85f, 0f), scale: new Vector3(0.85f, 0.65f, 0.85f), material: 1);
        _ = PlaceNamed(scene: scene, name: "foliageUpper", type: AvatarPrimitive.Ellipsoid, position: new Vector3(0f, 2.35f, 0f), scale: new Vector3(0.62f, 0.5f, 0.62f), material: 2);

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

        scene.SetPaletteEntry(index: 0, material: new SdfMaterial(Albedo: new Vector3(0.4f, 0.27f, 0.16f)));
        scene.SetPaletteEntry(index: 1, material: new SdfMaterial(Albedo: new Vector3(0.16f, 0.16f, 0.17f)));

        _ = PlaceNamed(scene: scene, name: "seat", type: AvatarPrimitive.Box, position: new Vector3(0f, 0.62f, 0f), scale: new Vector3(0.9f, 0.06f, 0.42f), material: 0);
        _ = PlaceNamed(scene: scene, name: "back", type: AvatarPrimitive.Box, position: new Vector3(0f, 0.95f, -0.36f), rotation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitX, angle: (-0.12f * MathF.PI)), scale: new Vector3(0.9f, 0.32f, 0.05f), material: 0);
        _ = PlaceNamed(scene: scene, name: "legLeft", type: AvatarPrimitive.Box, position: new Vector3(-0.75f, 0.42f, 0f), scale: new Vector3(0.07f, 0.24f, 0.38f), material: 1);
        _ = PlaceNamed(scene: scene, name: "legRight", type: AvatarPrimitive.Box, position: new Vector3(0.75f, 0.42f, 0f), scale: new Vector3(0.07f, 0.24f, 0.38f), material: 1);

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

        scene.SetPaletteEntry(index: 0, material: new SdfMaterial(Albedo: new Vector3(0.38f, 0.26f, 0.17f)));
        scene.SetPaletteEntry(index: 1, material: new SdfMaterial(Albedo: new Vector3(0.26f, 0.44f, 0.22f)));

        _ = PlaceNamed(scene: scene, name: "box", type: AvatarPrimitive.Box, position: new Vector3(0f, 0.6f, 0f), scale: new Vector3(0.5f, 0.25f, 0.5f), material: 0);
        _ = PlaceNamed(scene: scene, name: "bush", type: AvatarPrimitive.Sphere, position: new Vector3(0f, 1.02f, 0f), scale: new Vector3(0.46f, 0.4f, 0.46f), material: 1);

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

        scene.SetPaletteEntry(index: 0, material: new SdfMaterial(Albedo: new Vector3(0.2f, 0.2f, 0.22f)));
        scene.SetPaletteEntry(index: 1, material: new SdfMaterial(Albedo: new Vector3(0.32f, 0.34f, 0.38f)));
        scene.SetPaletteEntry(index: 2, material: new SdfMaterial(Albedo: new Vector3(0.72f, 0.14f, 0.12f)));

        _ = PlaceNamed(scene: scene, name: "post", type: AvatarPrimitive.Cylinder, position: new Vector3(0f, 0.72f, 0f), scale: new Vector3(0.08f, 0.55f, 0.08f), material: 0);
        _ = PlaceNamed(scene: scene, name: "body", type: AvatarPrimitive.Capsule, position: new Vector3(0f, 1.34f, 0f), rotation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitZ, angle: (0.5f * MathF.PI)), scale: new Vector3(0.2f, 0.34f, 0.2f), material: 1);
        _ = PlaceNamed(scene: scene, name: "flag", type: AvatarPrimitive.Box, position: new Vector3(0.24f, 1.42f, 0.05f), scale: new Vector3(0.03f, 0.16f, 0.1f), material: 2);

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

        scene.SetPaletteEntry(index: 0, material: new SdfMaterial(Albedo: new Vector3(0.62f, 0.6f, 0.58f)));
        scene.SetPaletteEntry(index: 1, material: new SdfMaterial(Albedo: new Vector3(0.7f, 0.68f, 0.64f)));
        scene.SetPaletteEntry(index: 2, material: new SdfMaterial(Albedo: new Vector3(0.16f, 0.42f, 0.44f), Emissive: 0.5f));

        _ = PlaceNamed(scene: scene, name: "wall", type: AvatarPrimitive.Cylinder, position: new Vector3(0f, 0.55f, 0f), scale: new Vector3(1.4f, 0.2f, 1.4f), material: 0);
        _ = PlaceNamed(scene: scene, name: "rim", type: AvatarPrimitive.Torus, position: new Vector3(0f, 0.75f, 0f), scale: new Vector3(1.45f, 0.16f, 1.45f), material: 1);
        _ = PlaceNamed(scene: scene, name: "water", type: AvatarPrimitive.Cylinder, position: new Vector3(0f, 0.7f, 0f), scale: new Vector3(1.2f, 0.03f, 1.2f), material: 2);
        _ = PlaceNamed(scene: scene, name: "column", type: AvatarPrimitive.Cylinder, position: new Vector3(0f, 1.15f, 0f), scale: new Vector3(0.22f, 0.6f, 0.22f), material: 0);
        _ = PlaceNamed(scene: scene, name: "spout", type: AvatarPrimitive.Sphere, position: new Vector3(0f, 1.78f, 0f), scale: new Vector3(0.22f, 0.22f, 0.22f), material: 1);

        return scene.ToDocument();
    }

    // ---- shared authoring helper (the SAME verb sequence FlagshipCreations.PlaceNamed drives) -----------------------

    // Places one named shape at an exact transform: deselect (targets the ghost), cycle the ghost to the desired
    // primitive from its always-Sphere-at-construction start, set the exact transform/material, place, then select
    // and rename the just-placed shape (RenameSelected needs a real selection, never the ghost).
    private static int PlaceNamed(CreatorScene scene, string name, AvatarPrimitive type, Vector3 position, Vector3 scale, int material, Quaternion? rotation = null, float onion = 0f) {
        scene.Deselect();

        var steps = (((int)type - (int)scene.GhostType) + CreatorScene.PrimitiveCount) % CreatorScene.PrimitiveCount;

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

        _ = scene.Select(idOrName: placedId.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
        var angle = (2f * MathF.Atan2(MathF.Sqrt((rotation.X * rotation.X) + (rotation.Y * rotation.Y) + (rotation.Z * rotation.Z)), rotation.W));

        if (rotation.X != 0f) {
            scene.SetTargetRotation(yawDegrees: 0f, pitchDegrees: (angle * (180f / MathF.PI) * MathF.Sign(rotation.X)), rollDegrees: 0f);
        }
        else if (rotation.Z != 0f) {
            scene.SetTargetRotation(yawDegrees: 0f, pitchDegrees: 0f, rollDegrees: (angle * (180f / MathF.PI) * MathF.Sign(rotation.Z)));
        }
        else {
            scene.SetTargetRotation(yawDegrees: (angle * (180f / MathF.PI) * MathF.Sign(rotation.Y)), pitchDegrees: 0f, rollDegrees: 0f);
        }
    }
}
