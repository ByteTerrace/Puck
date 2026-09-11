using System.Numerics;

using Puck.Assets.Documents;
using Puck.SignedDistance;

namespace Puck.World.Authoring;

/// <summary>
/// A second-material inset face region carried by a <see cref="ShapeDocument"/> — one authored shape that reads as
/// two: an eroded copy of the same primitive, offset along its own local face and composed with its own material,
/// recessed into or raised proud of the plate. Authored as one shape against the per-stamp shape budget — see
/// <see cref="CreationDocument.StampShapeCount"/>, which charges a panelled shape as 2, and the shape-field table in
/// <c>documents.md</c>.
/// </summary>
/// <param name="Inset">How far the panel copy shrinks on every local axis before it is placed — the copy's own
/// scale is <c>shape scale − Inset</c> per axis (floored at <see cref="SdfSolidGeometry.MinimumScale"/>), a
/// sharp-cornered shrink rather than a rounded Minkowski erosion: <see cref="SdfProgramBuilder.MaxFieldScopeDepth"/>
/// is 1, so nothing is free to isolate a <see cref="SdfProgramBuilder.Dilate"/> field op to the copy alone inside the
/// shared scope this and the plate ride. Creation units. Finite and non-negative; refused by name past the shape's
/// smallest local half-extent (the smallest of <see cref="SdfSolidGeometry.HalfExtent"/> over the X, Y, and Z local
/// axes) — past that the eroded copy is empty everywhere.</param>
/// <param name="Depth">How far the panel's own face sits from the plate's face along <see cref="Face"/>, in creation
/// units, exact whatever the <see cref="Inset"/>. Positive recesses it: the eroded copy is composed with
/// <see cref="SdfBlendOp.Subtraction"/> so its near face becomes the recess floor exactly <see cref="Depth"/> below
/// the plate's face — the floor and walls shade with <see cref="Material"/>, the subtraction-shades-with-the-subtrahend
/// convention the Moth's hood opening rides. Negative raises the panel proud by <c>|Depth|</c> instead: the copy is
/// composed with <see cref="SdfBlendOp.Union"/>, its far face standing <c>|Depth|</c> past the plate's own and its
/// back end buried inside the plate. Finite; refused by name past <c>±2·h′</c>, the eroded copy's own full extent
/// along <see cref="Face"/> — deeper, a recess carves an enclosed void and a raise floats detached, both silent
/// no-ops on the surface. See <see cref="Resolve"/> for the offset each case translates the copy by.</param>
/// <param name="Material">The panel's own palette slot — see <see cref="ShapeDocument.Material"/>. Clamped into
/// <c>[0, CreationDocument.PaletteSize)</c> at normalization.</param>
/// <param name="Face">The local direction the panel erodes and offsets against, read in the primitive's own local
/// frame — before its placement transform, the same frame <see cref="ShapeDocument.Domain"/> ops read. Null defaults
/// to <c>+Z</c> (the Prism extrude axis / a Box's front); a non-finite or zero-length authored value is refused by
/// name, and a finite non-unit one is normalized at canonicalization, mirroring a domain op's own normal. Any
/// primitive may author a curved face (a Cylinder's <c>+X</c> rim, say) — the erode/translate/blend recipe reads only
/// <see cref="SdfSolidGeometry.HalfExtent"/> along the axis, never the primitive's curvature.</param>
/// <remarks>
/// <para>Render-only, like <see cref="ShapeDocument.Domain"/>: the deterministic fixed-point contact evaluator
/// (<c>CreationStampEmitter.EmitFixed</c>/<c>VisitFixedPrimitiveCopies</c>) never reads this member, so a panelled
/// solid placement's collider is bit-identical to the same shape with no panel. A panel composes with rounding,
/// chamfer, taper, and profile — the eroded copy is emitted through the same <c>SdfSolidGeometry.AppendScaledPrimitive</c>
/// call the plate itself uses, so it inherits them — but never with the plate's own <see cref="ShapeDocument.Dilate"/>/
/// <see cref="ShapeDocument.Onion"/>, which apply to the plate's own field only.</para>
/// <para>A panel needs its own one-deep field scope (<c>PushField</c>/<c>PopField</c>) around
/// <c>[plate primitive, panel copy]</c> so its subtraction/union bites only this shape, never a sibling that happens
/// to occupy the same space — and because <see cref="SdfProgramBuilder.MaxFieldScopeDepth"/> is 1, that scope cannot
/// nest inside one a caller already opened. A panel is therefore refused by name on: a <see cref="SdfSolidPrimitive.Plane"/>
/// (no meaningful local face); a shape carrying <see cref="ShapeDocument.Domain"/> ops (their fold already owns the
/// shape's field, the same reason they refuse <see cref="ShapeDocument.Swings"/>/<see cref="ShapeDocument.Slides"/>/
/// <see cref="ShapeDocument.Parent"/>); a shape whose <see cref="ShapeDocument.Group"/> is set (a blend group's
/// members share one scope in the animated stamp pool, with none left over to nest); and a shape whose creation as a
/// whole needs a field scope (<c>CreationStampEmitter.RequiresScope</c> — any other shape's non-Union blend, an
/// engraved text run, or a noise facet), since the static placement path then shares one scope across the whole
/// creation and a panel there would have nowhere of its own to nest either.</para>
/// <para>The copy is emitted from its own transform chain (a second <c>ResetPoint</c> and prefix, then the face
/// translate, then the primitive) rather than chained after the plate's shape instruction: the plate's own emission
/// may leave a persistent <c>Scale</c> point op on the chain, which would scale the face translate and compound onto
/// the copy's own scale. Both paths reserve that second chain — the static per-shape probe through
/// <c>CreationStampEmitter.PerCopyInstanceCount</c> (two probe instances per panelled shape), the animated pool's
/// probe unconditionally.</para>
/// </remarks>
public sealed record ShapePanelDocument(
    float Inset,
    float Depth,
    int Material,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    DocumentVector3? Face = null
) {
    /// <summary>The default <see cref="Face"/>: the primitive's local <c>+Z</c>.</summary>
    public static readonly Vector3 DefaultFace = Vector3.UnitZ;

    /// <summary>Resolves where a panel copy sits against its plate — the one derivation both emission paths emit
    /// from. KEEP IN SYNC with <c>CreationCanonicalizer.ValidatePanel</c>, which refuses by name what this would
    /// place outside the plate.</summary>
    /// <param name="type">The plate's primitive.</param>
    /// <param name="scale">The plate's per-axis scale in the units <paramref name="inset"/> and
    /// <paramref name="depth"/> are given in — creation units on the static path, placement-scaled world units in
    /// the animated pool (which multiplies the authored values by the placement scale first).</param>
    /// <param name="lift">Prism only: extrude or revolve.</param>
    /// <param name="faceAxis">The panel's local face direction; normalized here.</param>
    /// <param name="inset">The per-axis erosion, in <paramref name="scale"/>'s units.</param>
    /// <param name="depth">The signed depth, in <paramref name="scale"/>'s units: positive recesses, negative
    /// raises.</param>
    /// <returns>The normalized face axis, the copy's eroded scale, the translation along the face axis the copy is
    /// emitted at, and the blend that composes it with the plate.</returns>
    /// <remarks>With <c>h</c> the plate's half-extent along the face and <c>h′</c> the eroded copy's: a recess
    /// translates by <c>h + h′ − depth</c>, so the copy's near face (<c>offset − h′</c>) is the floor at
    /// <c>h − depth</c>; a raise translates by <c>|depth| + (h − h′)</c>, so the copy's far face (<c>offset + h′</c>)
    /// stands at <c>h + |depth|</c>. Either way the authored depth is exact regardless of the inset.</remarks>
    public static ShapePanelPlacement Resolve(SdfSolidPrimitive type, Vector3 scale, SdfLift lift, Vector3 faceAxis, float inset, float depth) {
        var direction = Vector3.Normalize(value: faceAxis);
        var erodedScale = Vector3.Max(
            value1: (Vector3.Abs(value: scale) - new Vector3(value: inset)),
            value2: new Vector3(value: SdfSolidGeometry.MinimumScale)
        );
        var plateHalfExtent = SdfSolidGeometry.HalfExtent(
            type: type,
            scale: scale,
            lift: lift,
            axis: direction
        );
        var copyHalfExtent = SdfSolidGeometry.HalfExtent(
            type: type,
            scale: erodedScale,
            lift: lift,
            axis: direction
        );
        var recess = (depth >= 0f);

        return new ShapePanelPlacement(
            FaceAxis: direction,
            ErodedScale: erodedScale,
            Offset: (recess
            ? ((plateHalfExtent + copyHalfExtent) - depth)
            : (-depth + (plateHalfExtent - copyHalfExtent))),
            Blend: (recess
            ? SdfBlendOp.Subtraction
            : SdfBlendOp.Union)
        );
    }
}

/// <summary>Where a panel copy sits against its plate — see <see cref="ShapePanelDocument.Resolve"/>.</summary>
/// <param name="FaceAxis">The normalized local face direction.</param>
/// <param name="ErodedScale">The copy's own per-axis scale, in the plate scale's units.</param>
/// <param name="Offset">The signed translation along <paramref name="FaceAxis"/> the copy is emitted at, in the
/// plate scale's units.</param>
/// <param name="Blend">How the copy composes with the plate: <see cref="SdfBlendOp.Subtraction"/> for a recess,
/// <see cref="SdfBlendOp.Union"/> for a raise.</param>
public readonly record struct ShapePanelPlacement(Vector3 FaceAxis, Vector3 ErodedScale, float Offset, SdfBlendOp Blend);
