using System.Numerics;
using Puck.Demo.Text;
using Puck.SdfVm;

namespace Puck.Demo.Museum;

/// <summary>
/// THE REPLAY MUSEUM + THE DROSTE DOOR: static room furniture — every word/instance emitted here is unconditional
/// (no sim state to read, so a probe and a live rebuild are byte-identical), delegated out of
/// <c>OverworldFrameSource.Emitters.cs</c>'s one museum/door emitter block, mirroring
/// <see cref="Puck.Demo.Garden.GardenRenderer"/>'s "wraps an existing renderer" shape so that class's own coupling
/// stays flat.
/// <para>
/// The museum is four wall-mounted screens along the room's +Z wall — the terminal anchors that wall's −X end
/// (<c>OverworldFrameSource.EmitTerminal</c>, centered around x≈−5 in the default room); the museum takes the +X
/// remainder, each screen on its own screen-surface slot (<see cref="ScreenSlotBase"/>..+3) with an engraved
/// placard naming what it proves. The Droste door is a free-standing frame near the +X (workbench) wall's south
/// end, its opening a fifth screen (<see cref="DoorScreenSlot"/>).
/// </para>
/// <para>
/// Every slot is a permanent <c>Anchored</c>-band <see cref="Puck.SdfVm.Views.ScreenSlotPriority"/> ledger claim
/// (see <c>OverworldFrameSource.InstallFeeds</c>) — reserved so no floating claimant (a companion face, a placed
/// <c>world.camera</c>) can ever seat there instead — but the claims carry no source of their own: content arrives
/// ONLY through <c>world.wire named:&lt;name&gt; &lt;screen&gt;</c> (see the <c>*ViewName</c> constants), so the
/// museum ships with its structure/screens/placards live and its exhibits UNWIRED until a script or player wires
/// them — the same discipline the existing <c>nested:0</c> canary already established.
/// </para>
/// </summary>
public static class MuseumRenderer {
    /// <summary>The first of four museum screen-surface slots (4 = the AGB debug slot, 5 = the terminal — see
    /// <c>OverworldFrameSource.TerminalScreenSlot</c> — so the museum's headroom starts at 6).</summary>
    public const int ScreenSlotBase = 6;
    /// <summary>The museum's wall-screen count.</summary>
    public const int ScreenCount = 4;
    /// <summary>The Droste door's own screen-surface slot (just past the museum's four).</summary>
    public const int DoorScreenSlot = (ScreenSlotBase + ScreenCount);

    /// <summary>The existing Wave-4 canary nested world (the shared drift-monolith torture scene) — the museum's
    /// first exhibit reuses it verbatim rather than building a second copy. "The torture scene that guards our
    /// parity" — Droste + P6M wallpaper + a deep smooth/chamfer chain, shared verbatim with Puck.Post's own
    /// drift-ceiling stage.</summary>
    public const string MonolithViewName = "nested:0";
    /// <summary>The museum's second exhibit's named view — a standalone P4G wallpaper-fold nested world (see
    /// <c>OverworldFrameSource.EnsureMuseumNestedViewsRegistered</c>).</summary>
    public const string WallpaperViewName = "museum:wallpaper-p4g";
    /// <summary>The Droste door's interior named view — a standalone P6M wallpaper-fold nested world (the name keeps
    /// its working title from the LogSphere/Droste-spiral attempt that preceded it — see
    /// <c>OverworldFrameSource.DoorInteriorEmitter</c>'s remarks for why).</summary>
    public const string DoorViewName = "door:logsphere";

    private const float ScreenAspect = (160f / 144f); // the GB / NestedWorldView native aspect
    private const float ScreenHalfWidth = 0.60f;

    private static readonly float ScreenHalfHeight = (ScreenHalfWidth / ScreenAspect);

    private const float ScreenHalfDepth = 0.06f;
    private const float ScreenStandoff = 0.02f; // clears the wall's inner face — avoids z-fighting
    private const float ScreenMountY = 1.55f;   // screen CENTER height above the floor
    private const float ScreenSpacing = 2.5f;
    private const float MuseumInsetFromMaxX = 1.7f; // keeps the rightmost screen clear of the +X/+Z corner
    private const float PlaqueEmHeight = 0.050f;
    private const float PlaqueEngraveDepth = 0.018f;
    private const float PlaqueHalfDepth = 0.035f;
    private const float PlaqueHalfHeight = 0.20f;
    private const float PlaqueHalfWidth = 0.62f;
    private const float PlaqueMountY = 0.85f;
    private const float TitleEmHeight = 0.11f;
    private const float TitleMountY = 2.15f;

    // The Droste door's frame, near the +X (workbench) wall's SOUTH end (workbench sits at z≈0 — see
    // OverworldWorld.WorkbenchCenterZ — so the door clears it by construction).
    private const float DoorInsetFromMaxX = 1.4f;
    private const float DoorSouthInsetZ = 5.5f; // offset from the room's z-center toward −Z
    private const float DoorOpeningHalfWidth = 0.55f;  // along world Z (the door's "width" — see the face-normal derivation below)
    private const float DoorOpeningHalfHeight = 0.95f; // along world Y
    private const float DoorJambHalfDepth = 0.12f;     // along world X (the frame's thickness)
    private const float DoorJambHalfWidth = 0.15f;     // along world Z (each post's width)
    private const float DoorLintelHalfHeight = 0.16f;
    private const float DoorRecess = 0.16f;            // how far behind the jambs' front face the screen sits
    private const float DoorPlaqueEmHeight = 0.046f;
    private const float DoorScreenHalfDepth = 0.05f;

    private static readonly Vector3 WallPanelAlbedo = new(x: 0.16f, y: 0.15f, z: 0.20f);
    private static readonly Vector3 ScreenOffAlbedo = new(x: 0.03f, y: 0.05f, z: 0.05f);
    private static readonly Vector3 PlaqueAlbedo = new(x: 0.22f, y: 0.20f, z: 0.17f);
    private static readonly Vector3 TitleAlbedo = new(x: 0.80f, y: 0.72f, z: 0.42f);
    private static readonly Vector3 DoorFrameAlbedo = new(x: 0.24f, y: 0.19f, z: 0.15f);
    private static readonly string[] ExhibitTitles = [
        "PARITY MONOLITH",
        "WALLPAPER P4G",
        "LINK GAME REPLAY",
        "CROSS-GEN TRADE",
    ];
    private static readonly string[] ExhibitPlaques = [
        "Droste + P6M wallpaper + a\nsmooth chain, bit for bit -\nthe SDF VM's own torture rig.",
        "A documented wallpaper-fold\ndefect, left visible on\npurpose. Watch for a change.",
        "The cross-generation link\ntrade, proven in Post. The\nrecord/replay format is real\nnow; no GBA tape yet.",
        "The cable-club trade is\nPost-only. link-cable.console\nis the stand-in - persistence\nis proven for real now.",
    ];

    /// <summary>Emits the museum and Droste door into the room composition.</summary>
    /// <param name="builder">The program builder.</param>
    /// <param name="origin">The render-relative origin (the room composes at the spawn anchor, so this is
    /// <see cref="Vector3.Zero"/> in every live caller — threaded through for symmetry with the room's other props).</param>
    /// <param name="boundsMin">The room's minimum XZ corner.</param>
    /// <param name="boundsMax">The room's maximum XZ corner.</param>
    /// <param name="floorY">The floor plane height.</param>
    /// <param name="wallThickness">The perimeter walls' half-thickness (the room's single source of truth).</param>
    public static void Emit(SdfProgramBuilder builder, Vector3 origin, Vector2 boundsMin, Vector2 boundsMax, float floorY, float wallThickness) {
        ArgumentNullException.ThrowIfNull(builder);

        EmitGalleryWall(builder: builder, origin: origin, boundsMax: boundsMax, floorY: floorY, wallThickness: wallThickness);
        EmitDrosteDoor(builder: builder, origin: origin, boundsMax: boundsMax, floorY: floorY);
    }

    // Four wall-mounted screens along the +Z wall's +X remainder (clear of the terminal), each with a dark backing
    // frame (the "sliver-behind-a-frame" discipline the terminal/cabinets already use — a hair larger, sitting
    // deeper toward the wall so it never occludes the room-facing screen) and an engraved placard below.
    private static void EmitGalleryWall(SdfProgramBuilder builder, Vector3 origin, Vector2 boundsMax, float floorY, float wallThickness) {
        var font = SharedGlyphAtlas.MonoFont;
        var panelMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: WallPanelAlbedo));
        var plaqueMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: PlaqueAlbedo));
        var titleMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: TitleAlbedo, Emissive: 0.15f));

        var wallInnerZ = (boundsMax.Y - wallThickness);
        var screenZ = ((wallInnerZ - ScreenStandoff) - ScreenHalfDepth);
        // The entrance title, centered over the four-screen span.
        var titleX = ((boundsMax.X - MuseumInsetFromMaxX) - (1.5f * ScreenSpacing));
        var titleCenter = (new Vector3(x: titleX, y: (floorY + TitleMountY), z: (wallInnerZ - ScreenStandoff)) - origin);

        if (font is not null) {
            // "THE REPLAY MUSEUM" is 18 characters; the pen starts to the +X side (worldRight = −X, so the text
            // advances toward −X as it is laid out) and the whole run — plus a generous margin for font-metric
            // variance — stays well inside the instance bound below.
            _ = builder.BeginInstance(boundCenter: titleCenter, boundRadius: 2.0f);
            _ = builder.Text(atlas: font, text: "THE REPLAY MUSEUM", origin: (titleCenter + new Vector3(x: 1.3f, y: 0f, z: 0f)), right: -Vector3.UnitX, up: Vector3.UnitY, worldEmHeight: TitleEmHeight, material: titleMaterial, blend: SdfBlendOp.Union, extrudeHalfDepth: 0.02f);
            _ = builder.EndInstance();
        }

        for (var index = 0; (index < ScreenCount); index++) {
            var x = ((boundsMax.X - MuseumInsetFromMaxX) - (index * ScreenSpacing));
            var screenCenter = (new Vector3(x: x, y: (floorY + ScreenMountY), z: screenZ) - origin);
            var frameCenter = (screenCenter + new Vector3(x: 0f, y: 0f, z: (ScreenHalfDepth * 0.7f)));
            var plaqueCenter = (new Vector3(x: x, y: (floorY + PlaqueMountY), z: ((wallInnerZ - ScreenStandoff) - PlaqueHalfDepth)) - origin);

            _ = builder.BeginInstance(boundCenter: screenCenter, boundRadius: 1.3f);

            // The dark backing frame — deeper toward the wall (+Z), a hair larger than the slab, never occluding
            // the −Z-facing screen the player/camera see.
            _ = builder.ResetPoint().Translate(offset: frameCenter).Box(halfExtents: new Vector3(x: (ScreenHalfWidth + 0.05f), y: (ScreenHalfHeight + 0.05f), z: (ScreenHalfDepth * 0.5f)), round: 0.03f, material: panelMaterial);

            // The screen itself: samples whatever `world.wire named:<name> <screen>` last routed onto this slot
            // (0/unwired falls back to the flat/procedural screen material — no separate "off" box needed here,
            // unlike a cabinet/terminal that toggles between two states).
            var screenFaceOrigin = (screenCenter + new Vector3(x: 0f, y: 0f, z: -ScreenHalfDepth));

            _ = builder.ResetPoint().Translate(offset: screenCenter).ScreenSlab(
                halfExtents: new Vector3(x: ScreenHalfWidth, y: ScreenHalfHeight, z: ScreenHalfDepth),
                round: 0.03f,
                worldOrigin: screenFaceOrigin,
                worldRight: -Vector3.UnitX,
                worldUp: Vector3.UnitY,
                screenIndex: (ScreenSlotBase + index)
            );

            // The engraved placard plate below the screen: title + a short 3-line proof, cut INTO the plate
            // (Subtraction — never coplanar with a separate emboss, avoiding the coincident-zero-set speckle).
            _ = builder.ResetPoint().Translate(offset: plaqueCenter).Box(halfExtents: new Vector3(x: PlaqueHalfWidth, y: PlaqueHalfHeight, z: PlaqueHalfDepth), round: 0.015f, material: plaqueMaterial);

            if (font is not null) {
                var textOrigin = (plaqueCenter + new Vector3(x: (PlaqueHalfWidth - 0.05f), y: (PlaqueHalfHeight - 0.07f), z: (PlaqueHalfDepth - PlaqueEngraveDepth)));
                var text = ((ExhibitTitles[index] + "\n") + ExhibitPlaques[index]);

                _ = builder.Text(atlas: font, text: text, origin: textOrigin, right: -Vector3.UnitX, up: Vector3.UnitY, worldEmHeight: PlaqueEmHeight, material: plaqueMaterial, blend: SdfBlendOp.Subtraction, extrudeHalfDepth: PlaqueEngraveDepth);
            }

            _ = builder.EndInstance();
        }
    }

    // A free-standing doorway near the +X wall's south end (workbench sits at z≈0 — see
    // OverworldWorld.WorkbenchCenterZ — so the door clears it by construction): two jambs + a lintel framing an
    // opening whose "glass" is a screen slab. The screen recesses slightly behind the frame's front face (DoorRecess)
    // so the opening reads with real depth, not a flat cutout.
    //
    // FACE-NORMAL DERIVATION (so future edits don't have to re-derive it): a screen's worldRight/worldUp must read
    // un-mirrored for a viewer standing on the screen's FRONT (face-normal) side. The two existing wall pairs in
    // this file establish the rule "worldRight = faceNormal rotated +90 degrees about +Y" (cabinets: faceNormal
    // +Z -> worldRight +X; terminal/museum: faceNormal −Z -> worldRight −X — both match). The door faces −X (it
    // opens toward the room interior, away from the +X wall), so worldRight = (−X) rotated +90 about Y = +Z.
    private static void EmitDrosteDoor(SdfProgramBuilder builder, Vector3 origin, Vector2 boundsMax, float floorY) {
        var font = SharedGlyphAtlas.MonoFont;
        var frameMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: DoorFrameAlbedo));
        var plaqueMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: PlaqueAlbedo));

        var doorX = (boundsMax.X - DoorInsetFromMaxX);
        var doorZ = -DoorSouthInsetZ;
        var doorCenter = (new Vector3(x: doorX, y: (floorY + DoorOpeningHalfHeight), z: doorZ) - origin);
        var postHalfHeight = ((DoorOpeningHalfHeight + DoorLintelHalfHeight) + 0.05f);
        var leftJambCenter = (doorCenter + new Vector3(x: 0f, y: (postHalfHeight - DoorOpeningHalfHeight), z: -(DoorOpeningHalfWidth + DoorJambHalfWidth)));
        var rightJambCenter = (doorCenter + new Vector3(x: 0f, y: (postHalfHeight - DoorOpeningHalfHeight), z: (DoorOpeningHalfWidth + DoorJambHalfWidth)));
        var lintelCenter = (doorCenter + new Vector3(x: 0f, y: (DoorOpeningHalfHeight + DoorLintelHalfHeight), z: 0f));
        var screenCenter = (doorCenter + new Vector3(x: DoorRecess, y: 0f, z: 0f));
        var screenFaceOrigin = (screenCenter + new Vector3(x: -DoorScreenHalfDepth, y: 0f, z: 0f));
        var plaqueCenter = (doorCenter + new Vector3(x: -0.02f, y: -(DoorOpeningHalfHeight * 0.55f), z: ((DoorOpeningHalfWidth + DoorJambHalfWidth) + 0.42f)));

        _ = builder.BeginInstance(boundCenter: doorCenter, boundRadius: 2.2f);

        _ = builder.ResetPoint().Translate(offset: leftJambCenter).Box(halfExtents: new Vector3(x: DoorJambHalfDepth, y: postHalfHeight, z: DoorJambHalfWidth), round: 0.02f, material: frameMaterial);
        _ = builder.ResetPoint().Translate(offset: rightJambCenter).Box(halfExtents: new Vector3(x: DoorJambHalfDepth, y: postHalfHeight, z: DoorJambHalfWidth), round: 0.02f, material: frameMaterial);
        _ = builder.ResetPoint().Translate(offset: lintelCenter).Box(halfExtents: new Vector3(x: DoorJambHalfDepth, y: DoorLintelHalfHeight, z: ((DoorOpeningHalfWidth + DoorJambHalfWidth) + 0.05f)), round: 0.02f, material: frameMaterial);

        // The opening: samples whatever `world.wire named:door:logsphere <screen>` routes onto DoorScreenSlot — the
        // folded interior IS the other side (no teleport-through — see MuseumRenderer's type remarks).
        _ = builder.ResetPoint().Translate(offset: screenCenter).ScreenSlab(
            halfExtents: new Vector3(x: DoorScreenHalfDepth, y: DoorOpeningHalfHeight, z: DoorOpeningHalfWidth),
            round: 0.0f,
            worldOrigin: screenFaceOrigin,
            worldRight: Vector3.UnitZ,
            worldUp: Vector3.UnitY,
            screenIndex: DoorScreenSlot
        );

        // The floor-adjacent placard beside the door, same face convention as the opening.
        _ = builder.ResetPoint().Translate(offset: plaqueCenter).Box(halfExtents: new Vector3(x: PlaqueHalfDepth, y: PlaqueHalfHeight, z: PlaqueHalfWidth), round: 0.015f, material: plaqueMaterial);

        if (font is not null) {
            var textOrigin = (plaqueCenter + new Vector3(x: (PlaqueHalfDepth - PlaqueEngraveDepth), y: (PlaqueHalfHeight - 0.07f), z: -(PlaqueHalfWidth - 0.05f)));

            _ = builder.Text(atlas: font, text: "THE DROSTE DOOR\nAn ordinary doorway.\nThe other side is folded\nspace (a wallpaper fold).", origin: textOrigin, right: Vector3.UnitZ, up: Vector3.UnitY, worldEmHeight: DoorPlaqueEmHeight, material: plaqueMaterial, blend: SdfBlendOp.Subtraction, extrudeHalfDepth: PlaqueEngraveDepth);
        }

        _ = builder.EndInstance();
    }
}
