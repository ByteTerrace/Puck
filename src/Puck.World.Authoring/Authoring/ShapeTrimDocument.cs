using System.Numerics;

using Puck.SignedDistance;

namespace Puck.World.Authoring;

/// <summary>
/// A second-material band painted onto a <see cref="ShapeDocument"/>'s own surface wherever it sits near another,
/// earlier-declared shape in the same creation — the study's ivory boot band (<c>max(shin - .003, -ankleCut -
/// .095)</c>) generalized: the host's surface where it is within <see cref="Width"/> of the reference shape's own
/// surface, painted with <see cref="Material"/>. Where <see cref="ShapePanelDocument"/> recesses or raises a copy
/// of a shape's OWN geometry, a trim colors the host's surface against a REFERENCE shape's geometry instead — a
/// Subtraction cutter riding the host's own <see cref="ShapeDocument.Group"/> is the common case (the carve's own
/// edge becomes the trim's guide), or a plain plane-like Box authored purely to mark a seam. Authored as one shape
/// against the per-stamp shape budget, counting 2 (the host's own eroded copy plus the reference's own dilated
/// copy) — see <see cref="CreationDocument.StampShapeCount"/> and the shape-field table in <c>documents.md</c>.
/// </summary>
/// <param name="Shape">The <see cref="ShapeDocument.Name"/> of another shape in the same creation, declared
/// EARLIER in <c>shapes</c> (so a chain resolves in one pass and can never cycle — mirrors
/// <see cref="ShapeDocument.Parent"/>'s own rule). A Subtraction cutter riding the host's own
/// <see cref="ShapeDocument.Group"/> is the common case; a plain plane-like Box with blend Union works too. Only
/// the reference's own primitive geometry (type, scale, taper, profile, lift, rounding, chamfer) and pose are
/// re-read; its own blend, domain, panel, and trims play no part in the band.</param>
/// <param name="Width">How far outward from the reference shape's own surface the band reaches, in creation units.
/// Finite and positive; refused by name otherwise. Baked into the reference copy's own scale (a sharp-cornered
/// growth — the mirror image of, and the same imprecision, <see cref="ShapePanelDocument.Inset"/>'s own erosion
/// already accepts) rather than a field-op dilation: it is the only way the reference's own offset survives
/// composing through the macro's <see cref="SdfBlendOp.Intersection"/> blend inside the one field scope both copies
/// share — a field op applied after that blend would dilate the ALREADY-INTERSECTED result (eroding the host's own
/// copy a second time by <see cref="Width"/> too), not the reference alone.</param>
/// <param name="Material">The trim's own palette slot — see <see cref="ShapeDocument.Material"/>. Clamped into
/// <c>[0, CreationDocument.PaletteSize)</c> at normalization. Used for both copies the macro composes, so whichever
/// term the blend resolves to at a given point still paints the same color.</param>
/// <param name="Inset">The margin the trim's own scope must beat the host's already-present, unmodified surface by
/// to render at all — a real <see cref="SdfProgramBuilder.Dilate"/> field op at a POSITIVE radius on the host's own
/// copy (exact and isolated: the copy is alone in the trim's scope at that point, so nothing else is affected). The
/// scope pops via Union into the accumulator the host's own plain shape already folded into, and
/// <c>max(a, b) &gt;= a</c> makes a genuine erosion (a negative radius) provably always lose to that plain
/// surface — the trim would never render — so the copy is instead nudged narrowly CLOSER than the plain surface by
/// <see cref="Inset"/>, imperceptibly proud rather than inward: it loses everywhere by default (small) and wins
/// only where the reference's own dilated copy does not additionally push the Intersection candidate past it,
/// i.e. near the reference. Creation units, finite, in <c>[0, MaxInset]</c>; refused by name otherwise. Default
/// <c>0.003</c> — just enough to clear the plain surface without z-fighting, the study's own shin margin.</param>
public sealed record ShapeTrimDocument(string Shape, float Width, int Material, float Inset = 0.003f) {
    /// <summary>The most entries <see cref="ShapeDocument.Trims"/> carries.</summary>
    public const int MaxTrims = 4;
    /// <summary>The largest <see cref="Inset"/> — keeps the host copy's outward nudge well inside the per-shape
    /// cull-bound margins every emission path already carries, so a trim never needs its own bound widening.</summary>
    public const float MaxInset = 0.05f;
    /// <summary>A representative <see cref="Width"/> a capacity probe reserves <see cref="MaxTrims"/> worst-case
    /// forms with — the probe never renders, and every trim costs the same instruction words regardless of
    /// magnitude, so any finite positive value reserves correctly.</summary>
    public const float ProbeWidth = 0.1f;

    /// <summary>Grows a scale per axis by <paramref name="width"/> — the sharp-cornered dilation <see cref="Width"/>
    /// bakes into the reference copy's own primitive, the mirror image of the erosion
    /// <see cref="ShapePanelDocument.Resolve"/> computes for a panel copy. Floored at
    /// <see cref="SdfSolidGeometry.MinimumScale"/> for the same defensive reason erosion floors there, though a
    /// positive growth never actually needs it.</summary>
    /// <param name="scale">The reference's own per-axis scale.</param>
    /// <param name="width">The outward growth per axis.</param>
    /// <returns>The grown scale.</returns>
    public static Vector3 DilatedScale(Vector3 scale, float width) => Vector3.Max(
        value1: (Vector3.Abs(value: scale) + new Vector3(value: width)),
        value2: new Vector3(value: SdfSolidGeometry.MinimumScale)
    );
}
