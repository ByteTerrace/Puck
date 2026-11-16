using System.Numerics;
using Puck.SignedDistance;

namespace Puck.World.Authoring;

/// <summary>The frame one creation shape's emission rides: where its chains start, where its relief samples, and how
/// its other palette slots resolve. A static stamp and a pooled instance each supply one to
/// <see cref="ShapeEmission.Emit"/>, which owns everything else about the shape's emission.</summary>
public interface IShapeEmissionFrame {
    /// <summary>Gets the factor a creation length becomes a world length by, for what acts on the running world-unit
    /// accumulator (a blend radius, a dilation, an onion's thickness, an erosion's reach) and for a relief's
    /// amplitude and frequency.</summary>
    float FacetScale { get; }

    /// <summary>Starts a chain of the emitted shape: <see cref="SdfProgramBuilder.ResetPoint"/> and the frame's prefix,
    /// leaving the point in creation units at the creation's origin.</summary>
    /// <param name="builder">The program builder.</param>
    /// <returns>The chain.</returns>
    SdfProgramBuilder Prefix(SdfProgramBuilder builder);
    /// <summary>Starts a chain of another shape of the same creation (a trim's reference), as
    /// <see cref="Prefix"/> does for the emitted shape: under whatever moves that shape.</summary>
    /// <param name="builder">The program builder.</param>
    /// <param name="shapeIndex">The other shape's index in the creation's shape list.</param>
    /// <returns>The chain.</returns>
    SdfProgramBuilder PrefixOf(SdfProgramBuilder builder, int shapeIndex);
    /// <summary>Starts a relief chain on the shape's rigid frame, in world units: its origin and orientation, never its
    /// domain folds, residual scale or panel offset.</summary>
    /// <param name="builder">The program builder.</param>
    /// <param name="shape">The emitted shape.</param>
    /// <returns>The chain.</returns>
    SdfProgramBuilder Relief(SdfProgramBuilder builder, ShapeDocument shape);
    /// <summary>Resolves a palette slot the shape names besides its own material (its panel's or a trim's) to a program
    /// material id.</summary>
    /// <param name="shape">The emitted shape.</param>
    /// <param name="paletteSlot">The creation palette slot.</param>
    /// <returns>The program material id.</returns>
    int MaterialOf(ShapeDocument shape, int paletteSlot);
}
/// <summary>Emits one creation shape's render chain: its pose and point deformations, the primitive, its field ops,
/// panel, relief and trims, under the field scope it needs. Static stamps and pooled instances both emit through it,
/// differing only in their <see cref="IShapeEmissionFrame"/>.</summary>
/// <remarks>The field ops run in one order. A sine relief (<see cref="SdfProgramBuilder.Displace"/>) follows the
/// primitive at once, so it displaces the shape's own surface before a dilation, an onion's shell or a panel acts on
/// the result: an onion then hollows the relieved surface, both walls carrying the relief, and a panel's recess or
/// raise stays flat. A cellular relief follows the panel. A panel copy carries the plate's erosion, sampled at the
/// plate's own point, so a raised panel erodes with its plate.</remarks>
public static class ShapeEmission {
    /// <summary>Emits one shape.</summary>
    /// <typeparam name="TFrame">The frame's type.</typeparam>
    /// <param name="builder">The program builder.</param>
    /// <param name="frame">The frame the shape rides.</param>
    /// <param name="shapes">The creation's shapes, where a trim finds its reference by name.</param>
    /// <param name="shape">The shape to emit.</param>
    /// <param name="material">The shape's resolved program material id.</param>
    /// <param name="inScope">Whether the caller already holds a field scope around this emission (a blend group's or
    /// the whole creation's): a shape whose field op or warp would otherwise open its own scope rides it, and no trim
    /// is emitted, since validation refuses trims there.</param>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/>, <paramref name="shapes"/> or
    /// <paramref name="shape"/> is <see langword="null"/>.</exception>
    public static void Emit<TFrame>(SdfProgramBuilder builder, in TFrame frame, IReadOnlyList<ShapeDocument> shapes, ShapeDocument shape, int material, bool inScope) where TFrame : struct, IShapeEmissionFrame {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(shapes);
        ArgumentNullException.ThrowIfNull(shape);

        var facetScale = frame.FacetScale;
        var blend = (shape.Blend ?? SdfBlendOp.Union);
        var smooth = ((shape.Smooth ?? 0f) * facetScale);
        var dilate = ((shape.Dilate ?? 0f) * facetScale);
        var onion = ((shape.Onion ?? 0f) * facetScale);
        var detail = (shape.Detail ?? false);
        var panel = shape.Panel;
        var chain = Deformed(builder: builder, frame: in frame, shape: shape);
        // Unscoped, a field op would act on every shape the program emitted before this one, and a panel's blend
        // would bite a sibling; a warp's or relief's Lipschitz factor would fold into the whole program's step scale.
        // A primitive alone never needs one: every primitive is 1-Lipschitz. A pop-only blend composes at a pop alone.
        var ownScope = (
            (!inScope && ((dilate != 0f) || (onion != 0f) || (panel is not null) || ShapePointOps.NeedsScope(shape: shape))) ||
            ShapeScope.IsPopOnly(blend: blend)
        );

        if (ownScope) {
            var scoped = AppendPrimitive(
                chain: Erode(chain: ShapeScope.Open(blend: blend, chain: chain, morph: shape.Morph, smooth: smooth, steps: shape.Steps), facetScale: facetScale, shape: shape),
                shape: shape,
                scale: shape.Scale,
                material: material,
                detail: detail
            );

            EmitDisplace(builder: builder, frame: in frame, shape: shape);
            if (dilate != 0f) { scoped = scoped.Dilate(radius: dilate); }
            if (onion != 0f) { _ = scoped.Onion(thickness: onion); }
            if (panel is not null) {
                EmitPanelCopy(
                    chain: Erode(chain: Deformed(builder: builder, frame: in frame, shape: shape), facetScale: facetScale, shape: shape),
                    material: frame.MaterialOf(paletteSlot: panel.Material, shape: shape),
                    panel: panel,
                    shape: shape
                );
            }
            if (shape.Cells is { } cells) {
                _ = frame.Relief(builder: builder, shape: shape).CellDisplace(
                    (cells.Frequency / facetScale),
                    (cells.Amplitude * facetScale),
                    cells.Seed,
                    cells.Mode,
                    cells.Randomness
                );
            }
            _ = builder.PopField();
        } else {
            var unscoped = AppendPrimitive(
                chain: Erode(chain: chain, facetScale: facetScale, shape: shape),
                shape: shape,
                scale: shape.Scale,
                material: material,
                blend: blend,
                smooth: smooth,
                detail: detail
            );

            EmitDisplace(builder: builder, frame: in frame, shape: shape);
            if (dilate != 0f) { unscoped = unscoped.Dilate(radius: dilate); }
            if (onion != 0f) { _ = unscoped.Onion(thickness: onion); }
        }

        if (!inScope && (shape.Trims is { Count: > 0 } trims)) {
            foreach (var trim in trims) {
                EmitTrim(builder: builder, frame: in frame, shapes: shapes, shape: shape, trim: trim);
            }
        }
    }
    /// <summary>Appends a shape's primitive with its authored facets (taper, profile, lift, rounding, chamfer, exponent,
    /// curve): the one place they reach <see cref="SdfSolidGeometry.AppendScaledPrimitive"/>, so no emission of a
    /// shape's primitive, panel copy, trim host or trim reference can disagree on a default.</summary>
    /// <param name="chain">The chain the primitive closes.</param>
    /// <param name="shape">The shape.</param>
    /// <param name="scale">The primitive's per-axis scale, in the chain's units.</param>
    /// <param name="material">The program material id.</param>
    /// <param name="blend">The primitive's blend into the accumulator.</param>
    /// <param name="smooth">The blend radius, world units.</param>
    /// <param name="detail">Whether the primitive is a detail shape.</param>
    /// <param name="curve">Whether to carry the shape's sweep curve; a trim's reference re-reads only the primitive.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shape"/> is <see langword="null"/>.</exception>
    public static SdfProgramBuilder AppendPrimitive(SdfProgramBuilder chain, ShapeDocument shape, Vector3 scale, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f, bool detail = false, bool curve = true) {
        ArgumentNullException.ThrowIfNull(shape);

        return SdfSolidGeometry.AppendScaledPrimitive(
            chain: chain,
            type: shape.Type,
            taper: (shape.Taper ?? 0.5f),
            profile: shape.Profile,
            lift: (shape.Lift ?? SdfLift.Extrude),
            rounding: (shape.Rounding ?? 0f),
            chamfer: (shape.Chamfer ?? 0f),
            exponent: (shape.Exponent ?? SdfProgramBuilder.MinSuperellipsoidExponent),
            curve: (curve
            ? shape.Curve?.Parameters()
            : null),
            scale: scale,
            material: material,
            blend: blend,
            smooth: smooth,
            detail: detail
        );
    }

    // The frame's prefix, the shape's domain folds and rest pose, then its point deformations, in creation units.
    private static SdfProgramBuilder Deformed<TFrame>(SdfProgramBuilder builder, in TFrame frame, ShapeDocument shape) where TFrame : struct, IShapeEmissionFrame {
        var posed = ShapeDomainOps.Apply(chain: frame.Prefix(builder: builder), domain: shape.Domain, subject: shape.Position)
            .Translate(offset: shape.Position)
            .Rotate(rotation: shape.Rotation);

        return ShapePointOps.Apply(chain: posed, twist: (shape.Twist ?? 0f), bend: (shape.Bend ?? 0f),
            flare: shape.Flare, shear: shape.Shear, bumps: shape.Bumps, warp: shape.Warp, elongate: shape.Elongate);
    }
    // The shape's lane-driven erosion, pending on the next primitive the chain emits; its reach is the shape's own, in
    // world units.
    private static SdfProgramBuilder Erode(SdfProgramBuilder chain, ShapeDocument shape, float facetScale) =>
        ((shape.Erode is { } erode)
            ? chain.LaneErode(
                from: erode.From,
                lane: erode.Lane,
                noiseScale: (erode.Noise ?? 1f),
                reach: (SdfSolidGeometry.Reach(lift: (shape.Lift ?? SdfLift.Extrude), scale: shape.Scale, type: shape.Type) * facetScale),
                to: erode.To
            )
            : chain);
    // The sine relief on the shape's rigid frame: frequency and amplitude convert inversely, preserving its bound.
    private static void EmitDisplace<TFrame>(SdfProgramBuilder builder, in TFrame frame, ShapeDocument shape) where TFrame : struct, IShapeEmissionFrame {
        if (shape.Displace is not { } displace) { return; }

        _ = frame.Relief(builder: builder, shape: shape)
            .Displace(amplitude: (displace.Amplitude * frame.FacetScale), frequency: (displace.Frequency.Value / frame.FacetScale));
    }
    // The panel copy: the same primitive on a chain of its own (the plate's emission may leave a persistent Scale op
    // behind), offset and eroded as ShapePanelDocument.Resolve places it and composed against the plate inside the
    // plate's scope. Inset and depth are creation units, the units the chain reads.
    private static void EmitPanelCopy(SdfProgramBuilder chain, ShapeDocument shape, int material, ShapePanelDocument panel) {
        var placement = ShapePanelDocument.Resolve(
            depth: panel.Depth,
            faceAxis: ((panel.Face is { } face)
            ? face.Value
            : ShapePanelDocument.DefaultFace),
            inset: panel.Inset,
            lift: (shape.Lift ?? SdfLift.Extrude),
            scale: shape.Scale,
            type: shape.Type
        );

        _ = AppendPrimitive(
            chain: chain.Translate(offset: (placement.FaceAxis * placement.Offset)),
            shape: shape,
            scale: placement.ErodedScale,
            material: material,
            blend: placement.Blend
        );
    }
    // A trim's own scope pops by Union into the accumulator the host's plain shape already folded into, so its
    // candidate wins only where it undercuts that value. An erosion never could (max(a, b) >= a bounds an
    // eroded-then-intersected candidate below by the host eroded by Inset), so the host copy grows by Inset instead:
    // narrowly closer than the plain host, it loses everywhere except where its intersection with the reference's
    // dilated copy keeps it, near the reference. The host stays imperceptibly proud there; the trim exists to avoid a
    // coincident-surface fight, not to show a step. Both copies ride chains of their own, the reference under whatever
    // moves it.
    private static void EmitTrim<TFrame>(SdfProgramBuilder builder, in TFrame frame, IReadOnlyList<ShapeDocument> shapes, ShapeDocument shape, ShapeTrimDocument trim) where TFrame : struct, IShapeEmissionFrame {
        var referenceIndex = -1;

        for (var index = 0; (index < shapes.Count); index++) {
            if (string.Equals(a: shapes[index].Name?.Value, b: trim.Shape, comparisonType: StringComparison.Ordinal)) {
                referenceIndex = index;

                break;
            }
        }
        if (referenceIndex < 0) {
            return; // Validation refuses an undeclared reference.
        }

        var reference = shapes[referenceIndex];
        var material = frame.MaterialOf(paletteSlot: trim.Material, shape: shape);
        var grown = AppendPrimitive(
            chain: frame.Prefix(builder: builder).Translate(offset: shape.Position).Rotate(rotation: shape.Rotation).PushField(compose: SdfBlendOp.Union),
            shape: shape,
            scale: shape.Scale,
            material: material
        ).Dilate(radius: (trim.Inset * frame.FacetScale));

        _ = AppendPrimitive(
            chain: frame.PrefixOf(builder: grown, shapeIndex: referenceIndex).Translate(offset: reference.Position).Rotate(rotation: reference.Rotation),
            shape: reference,
            scale: ShapeTrimDocument.DilatedScale(scale: reference.Scale, width: trim.Width),
            material: material,
            blend: SdfBlendOp.Intersection,
            curve: false
        );
        _ = builder.PopField();
    }
}
