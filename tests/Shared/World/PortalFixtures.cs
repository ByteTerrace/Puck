using System.Numerics;


using Puck.World.Authoring;
using Puck.SignedDistance;


namespace Puck.World.Testing;

/// <summary>The door creation a portal arrival names, and its face.</summary>
internal static class PortalFixtures {
    // A minimal creation declaring ONE face ("door") — the authored anchor ValidateFaceSources' faceNames set (and,
    // separately, WorldPortalCounterpart's own placement/face resolution) checks a portal-bearing placement against.
    // Mirrors Fixtures.BuildBallCreation's own canonicalize-at-build shape (a real creation, hash COMPILER-derived
    // through the SAME pipeline the validator re-verifies, never hand-pinned). The face names a BOX shape because a
    // portal facet needs a surface that opens a walkable aperture (WorldFaceApertures) — the aperture refusal
    // is its own law below, so every other law here must clear it to discriminate on what it is actually testing.
    internal static WorldPrototype BuildDoorCreation() => BuildDoorCreation(
        faceNamesShape: true,
        faceShapeType: SdfSolidPrimitive.Box
    );
    internal static WorldPrototype BuildDoorCreation(SdfSolidPrimitive faceShapeType, bool faceNamesShape) {
        var shape = new ShapeDocument(
            Id: 0,
            Name: null,
            Type: faceShapeType,
            Position: Vector3.Zero,
            Rotation: Quaternion.Identity,
            Scale: Vector3.One,
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0
        );
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "door",
            Palette: null,
            Shapes: [shape],
            Frames: null,
            Behavior: new CreationBehaviorDocument(
                Locomotion: null,
                Faces: [new CreationFaceDocument(
                        DefaultSource: null,
                        Name: DoorFace,
                        ShapeId: (faceNamesShape
            ? 0
            : null)
                    )]
            )
        );
        var canonical = CreationCanonicalizer.Canonicalize(
            document: document,
            source: "door"
        );

        return new WorldPrototype(
            Id: "door",
            Document: canonical.Document,
            HashRaw: canonical.Hash
        );
    }

    internal const string DoorFace = "door";
}
