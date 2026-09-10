using System.Numerics;
using Puck.World.Authoring;
using Puck.SignedDistance;

namespace Puck.World.Client;

/// <content>
/// The trim half of the stamp pool: painting a shape's own surface near a REFERENCE shape declared earlier in the
/// same creation — the panel lane's own scope pattern, but against a second shape's geometry instead of a copy of
/// the host's own.
/// </content>
public sealed partial class WorldStampPool {
    // Called from EmitShape after the panel emission, for a scope of its own PER trim (sequential, never nested —
    // each trim's own PushField/PopField opens and closes before the next). The scope pops via Union into the same
    // accumulator the shape's own plain instance already folds into elsewhere, so a genuine erosion of the host
    // copy could never win there (max(a, b) >= a bounds an eroded-then-intersected candidate below by "host eroded
    // by Inset", strictly worse than the plain shape). The host copy instead grows by Inset — an isolated Dilate()
    // field op at a POSITIVE radius (exact — nothing else has joined the scope's accumulator yet) that nudges it
    // narrowly closer than the plain shape, losing everywhere by default (Inset is tiny) and winning only where the
    // reference's own dilated copy (its own scale grown by Width — ShapeTrimDocument.DilatedScale) does not push
    // the Intersection candidate past it, i.e. near the reference. Reads the reference's OWN dynamic transform slot
    // (rootSlot + 1 + its index in allShapes) so a swung/slid reference keeps trimming correctly. probeWorstCase
    // reserves ShapeTrimDocument.MaxTrims worst forms unconditionally, standing the host's own type/scale in for
    // the reference (no live document exists to resolve a name against during a probe — the reservation needs only
    // the right instruction shape). Refused at validation wherever a domain/group/whole-creation scope would leave
    // this scope nowhere to open, so this never runs for a shape any of those apply to.
    private static void EmitTrims(SdfProgramBuilder builder, int slot, int rootSlot, SdfSolidPrimitive type, Vector3 scale, int material, float taper, SdfPrismProfile? profile, SdfLift lift, float rounding, float chamfer, bool probeWorstCase, float placementScale, IReadOnlyList<ShapeTrimDocument>? trims, IReadOnlyList<ShapeDocument>? allShapes, int[]? paletteIds, float exponent = SdfProgramBuilder.MinSuperellipsoidExponent) {
        var count = (probeWorstCase
            ? ShapeTrimDocument.MaxTrims
            : (trims?.Count ?? 0)
        );

        for (var i = 0; (i < count); i++) {
            var trim = (probeWorstCase ? null : trims![i]);
            var referenceSlot = slot;
            var referenceType = type;
            var referenceScale = scale;
            var referenceTaper = taper;
            var referenceProfile = profile;
            var referenceLift = lift;
            var referenceRounding = rounding;
            var referenceChamfer = chamfer;
            var referenceExponent = exponent;
            var trimMaterial = material;
            var inset = 0.003f;
            var width = ShapeTrimDocument.ProbeWidth;

            if (!probeWorstCase) {
                var referenceIndex = -1;

                for (var candidate = 0; (candidate < (allShapes?.Count ?? 0)); candidate++) {
                    if (string.Equals(
                        a: allShapes![candidate].Name?.Value,
                        b: trim!.Shape,
                        comparisonType: StringComparison.Ordinal
                    )) {
                        referenceIndex = candidate;

                        break;
                    }
                }

                if (referenceIndex < 0) {
                    continue; // Validation refuses an undeclared reference before this ever runs.
                }

                var reference = allShapes![referenceIndex];

                referenceSlot = (rootSlot + 1 + referenceIndex);
                referenceType = reference.Type;
                referenceScale = (reference.Scale * placementScale);
                referenceTaper = (reference.Taper ?? 0.5f);
                referenceProfile = reference.Profile;
                referenceLift = (reference.Lift ?? SdfLift.Extrude);
                referenceRounding = ((reference.Rounding ?? 0f) * placementScale);
                referenceChamfer = ((reference.Chamfer ?? 0f) * placementScale);
                referenceExponent = (reference.Exponent ?? SdfProgramBuilder.MinSuperellipsoidExponent);
                trimMaterial = paletteIds![(trim!.Material % paletteIds!.Length)];
                inset = (trim.Inset * placementScale);
                width = (trim.Width * placementScale);
            }

            var eroded = SdfSolidGeometry.AppendScaledPrimitive(
                chain: builder.ResetPoint().TransformDynamic(slot: slot).PushField(compose: SdfBlendOp.Union),
                type: type, taper: taper, profile: profile, lift: lift, rounding: rounding, chamfer: chamfer, exponent: exponent,
                scale: scale,
                material: trimMaterial,
                blend: SdfBlendOp.Union,
                smooth: 0f
            ).Dilate(radius: inset);

            _ = SdfSolidGeometry.AppendScaledPrimitive(
                chain: eroded.ResetPoint().TransformDynamic(slot: referenceSlot),
                type: referenceType, taper: referenceTaper, profile: referenceProfile,
                lift: referenceLift, rounding: referenceRounding, chamfer: referenceChamfer, exponent: referenceExponent,
                scale: ShapeTrimDocument.DilatedScale(scale: referenceScale, width: width),
                material: trimMaterial,
                blend: SdfBlendOp.Intersection,
                smooth: 0f
            );

            _ = builder.PopField();
        }
    }
}
