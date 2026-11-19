using System.Numerics;
using Puck.SignedDistance;
using Puck.SdfVm;
using Puck.SignedDistance.Baking;
using Puck.World.Authoring;

namespace Puck.World.Client;

/// <summary>
/// The one rule for the spacing a creation's shape asks its bake to contour it at (<see cref="SdfInstanceRange.Detail"/>,
/// <see cref="SdfInstancePart.Detail"/>), alike for a static placement's shape, a pooled blend group's part and an
/// ungrouped pooled shape. A bound measures a shape's reach, not its smallest curved or thin feature, so each such feature
/// declares a spacing of its own, and the shape declares the finest. The declaration bounds the refinement; the bake
/// decides what it costs: a detail no finer than the lattice it bakes on already is refines nothing, a union refines each
/// member's box toward its own detail alone (<see cref="SdfMeshPathBake"/>), and within the box only the cells
/// whose samples show the surface turning refine, each only as far as its turn needs
/// (<see cref="SdfSampledLattice.Refine"/>), so a drum's flat faces bake at the lattice's own spacing while its rim and
/// fillet refine.
/// </summary>
internal static class WorldShapeDetail {
    /// <summary>The finest spacing, in metres, a shape declares: at the closest the chase and studio cameras come, about
    /// 0.4 m, a cell this size spans a pixel or two, so a finer request buys triangles no view resolves. A feature that
    /// asks finer (a small rounding, a thin shell) bakes at this spacing.</summary>
    internal const float FinestDetail = 0.002f;

    // How much longer than thick a shape is before the bake contours it at its half thickness (PlateDetail).
    private const float ThinRatio = 8f;
    // A warped field's spacing as a share of its smallest half extent: the warp moves the surface by up to that extent
    // over a few cells, which a lattice sized by the unwarped bound steps over.
    private const float WarpCells = 32f;

    /// <summary>Returns the spacing, in world units at <paramref name="scale"/>, a shape asks its bake for: the finest
    /// of its round sections' (<see cref="SdfSampledLattice.RoundCell"/>: a sphere's, an ellipsoid's middle radius, a
    /// cylinder's or cone's, a capsule's, a torus's tube, a strand's, an elliptic or rounded profile's tightest bend), its
    /// rounding's radius, a thin shape's half thickness, a shell's quarter thickness, cellular relief's cell, a sine
    /// relief's or warp's tightest bend (<see cref="ShapeWarpDocument.ReliefRadius"/>) and a warped field's share of its
    /// extent; no finer than <see cref="FinestDetail"/>.</summary>
    /// <param name="shape">The shape, in creation units.</param>
    /// <param name="scale">The placement's uniform scale from creation units to world units.</param>
    /// <returns>The spacing, in world units; zero for a shape that declares none.</returns>
    internal static float Of(ShapeDocument shape, float scale) {
        var size = (shape.Scale ?? Vector3.One);
        var detail = float.PositiveInfinity;

        void Take(float spacing) {
            if (spacing > 0f) { detail = MathF.Min(x: detail, y: spacing); }
        }

        var section = RoundRadius(shape: shape);
        // A folded shape's bound spans its copies, and a lattice sized by it steps over each copy's own thickness, as one
        // sized by a plate's length steps over the plate: its copies are contoured no coarser than their half thickness.
        var plate = ((shape.Domain is { Count: > 0 }) ? HalfExtents(shape: shape, size: size).Smallest : PlateDetail(shape: shape, size: size));

        Take(spacing: RoundCell(radius: section, scale: scale));
        Take(spacing: RoundCell(radius: ProfileCorner(shape: shape, size: size), scale: scale));
        // A fillet far narrower than a cell bakes as the crease the contour keeps sharp; one about a cell wide leaves the
        // contour choosing crease or face cell by cell along the edge, a staircase. At its radius a fillet spans a cell and
        // a half of arc, which the contour follows; shading it smooth (RoundCell) would refine every rounded edge's whole
        // shape to a few millimetres. A fillet under a third of the bake's own spacing (SdfMeshPathBake.MinimumCell) is a
        // crease on any lattice a part bakes alone on, and declares nothing.
        if ((shape.Rounding ?? 0f) * scale >= (SdfMeshPathBake.MinimumCell / 3f)) { Take(spacing: shape.Rounding!.Value); }
        Take(spacing: plate);
        // A blend's seam is a feature of the blend's own radius, which neither shape's bound measures: a smooth seam's
        // fillet, a pipe's bead and a groove's channel turn at `smooth`; a chamfer's bevel is a face `smooth` wide, kept
        // as a face by a cell per half width; stairs are `steps` turns of a share of `smooth` each.
        if ((shape.Smooth is > 0f and var smooth) && (shape.Blend is { } blend)) {
            switch (blend) {
                case SdfBlendOp.SmoothUnion or SdfBlendOp.SmoothIntersection or SdfBlendOp.SmoothSubtraction
                    or SdfBlendOp.GrooveUnion or SdfBlendOp.GrooveSubtraction or SdfBlendOp.PipeUnion or SdfBlendOp.PipeSubtraction:
                    Take(spacing: RoundCell(radius: smooth, scale: scale));
                    break;
                case SdfBlendOp.ChamferUnion or SdfBlendOp.ChamferIntersection or SdfBlendOp.ChamferSubtraction:
                    Take(spacing: (smooth / 2f));
                    break;
                case SdfBlendOp.StairsUnion or SdfBlendOp.StairsSubtraction:
                    Take(spacing: RoundCell(radius: (smooth / Math.Max(val1: 1, val2: (shape.Steps ?? ShapeScope.DefaultSteps))), scale: scale));
                    break;
                default:
                    break;
            }
        }
        if (shape.Onion is > 0f) { Take(spacing: (shape.Onion.Value / 4f)); }
        if (shape.Cells is { } cells) { Take(spacing: (1f / (32f * cells.Frequency))); }
        if (shape.Displace is { } displace) { Take(spacing: RoundCell(radius: ShapeWarpDocument.ReliefRadius(amplitude: displace.Amplitude, frequency: displace.Frequency.Value), scale: scale)); }
        if (shape.Warp is { } warp) { Take(spacing: RoundCell(radius: ShapeWarpDocument.ReliefRadius(amplitude: warp.Amplitude, frequency: warp.Frequency.Value), scale: scale)); }
        // A zero twist or bend is none: the canonicalizer writes an absent one as zero, so a live edit's document reads it
        // as authored.
        if (ShapePointOps.NeedsScope(shape)) {
            Take(spacing: (HalfExtents(shape: shape, size: size).Smallest / WarpCells));
        }

        return (float.IsFinite(f: detail) ? MathF.Max(x: (detail * scale), y: FinestDetail) : 0f);
    }
    // A round section's spacing in creation units at `scale` (SdfSampledLattice.RoundCell, in world units).
    private static float RoundCell(float radius, float scale) => (SdfSampledLattice.RoundCell(radius: (radius * scale)) / scale);
    // A plate's, slab's, strip's or streamer's half thickness, in creation units: its bound measures its length, but its
    // thickness is the feature, and a lattice sized by the bound steps clean over a sheet thinner than its cell. Zero for a
    // shape no more than ThinRatio times longer than thick.
    private static float PlateDetail(ShapeDocument shape, Vector3 size) {
        var (thinnest, longest) = HalfExtents(shape: shape, size: size);

        return (((thinnest > 0f) && (longest > (ThinRatio * thinnest))) ? thinnest : 0f);
    }

    // The radius of a shape's own round section, in creation units: a strand's (its thicker end's: a lock tapers to a tip
    // that is a small part of it), a capsule's or a sphere's, an ellipsoid's middle radius, tightened as a superellipsoid's
    // exponent squares it off, a cylinder's or a cone's outline's tightest bend (EllipseBend), a torus's tube, or an
    // elliptic prism's outline's tightest bend. The middle radius is a finger's or a limb's girth, where its surface turns
    // all the way round; a flattened ellipsoid turns tightly only along its rim, an edge the contour keeps as a crease, as
    // it does a box's or a polygonal prism's. Zero for none.
    private static float RoundRadius(ShapeDocument shape) {
        var size = (shape.Scale ?? Vector3.One);
        var middle = MathF.Max(x: MathF.Min(x: size.X, y: size.Y), y: MathF.Min(x: MathF.Max(x: size.X, y: size.Y), y: size.Z));

        return shape.Type switch {
            SdfSolidPrimitive.Sweep when (shape.Curve is { } curve) => (MathF.Max(x: curve.RadiusStart, y: curve.RadiusEnd) * size.X),
            SdfSolidPrimitive.Sphere or SdfSolidPrimitive.Ellipsoid => middle,
            SdfSolidPrimitive.Superellipsoid => (middle * (SdfProgramBuilder.MinSuperellipsoidExponent / (shape.Exponent ?? SdfProgramBuilder.MinSuperellipsoidExponent))),
            SdfSolidPrimitive.Prism when (shape.Profile is { Kind: SdfPrismProfileKind.Ellipse }) => EllipseBend(a: size.X, b: size.Y),
            SdfSolidPrimitive.Cylinder or SdfSolidPrimitive.Cone => EllipseBend(a: size.X, b: size.Z),
            SdfSolidPrimitive.Capsule => size.X,
            SdfSolidPrimitive.RoundCone => (0.5f * size.X),
            SdfSolidPrimitive.Torus => (0.4f * size.X),
            _ => 0f,
        };
    }
    // An ellipse's tightest bend: its smaller radius squared over its larger, at the larger radius's ends. An elliptic
    // prism's wall turns round it, and so does the crease it carves when it cuts (an eye socket in a face).
    private static float EllipseBend(float a, float b) => ((MathF.Max(x: a, y: b) > 0f) ? ((MathF.Min(x: a, y: b) * MathF.Min(x: a, y: b)) / MathF.Max(x: a, y: b)) : 0f);
    // A rounded-rectangle or convex prism profile's corner radius, in creation units, its rounding included for a convex
    // one as the builder clamps it; zero for another profile.
    private static float ProfileCorner(ShapeDocument shape, Vector3 size) {
        if (shape.Profile is not { Kind: SdfPrismProfileKind.Convex or SdfPrismProfileKind.RoundedRectangle } profile) { return 0f; }
        if (profile.Kind == SdfPrismProfileKind.RoundedRectangle) { return (profile.CornerRadius * MathF.Min(x: size.X, y: size.Y)); }

        var inradius = SdfSolidGeometry.ScaledConvexProfile(profile, size).Inradius;

        return SdfProgramBuilder.ClampRounding(
            rounding: ((profile.CornerRadius * inradius) + (shape.Rounding ?? 0f)),
            profileInradius: inradius,
            lift: (shape.Lift ?? SdfLift.Extrude),
            liftAmount: size.Z
        );
    }
    /// <summary>Returns one shape's part of the instance it is emitted in (<see cref="SdfInstancePart"/>): its box where it
    /// sits under <paramref name="transform"/>, widened by its smooth blend's reach so the seam it blends refines with it
    /// and by <paramref name="grow"/>, and its detail (<see cref="Of"/>). The box is the stamper's own
    /// (<see cref="CreationStampEmitter.ShapeStampBox"/>) where it holds the shape, else the cube of the shape's tight bound
    /// (<see cref="CreationStampEmitter.ShapeStampBound"/>); a shape carrying domain folds, whose copies leave any
    /// shape-local box, takes the cube about the stamp's origin its copies reach.</summary>
    /// <param name="document">The creation document.</param>
    /// <param name="index">The shape's index in <see cref="CreationDocument.Shapes"/>.</param>
    /// <param name="transform">The stamp transform: the world stamp for a static instance's part, or the identity at the
    /// creation's scale for a part in its slot's frame.</param>
    /// <param name="slot">The part's slot, or <see cref="SdfInstancePart.StaticSlot"/> for a static instance's part.</param>
    /// <param name="grow">How far, in world units, the composed surface may move past the shape's own: a relief's
    /// amplitude; zero for none.</param>
    /// <returns>The part.</returns>
    internal static SdfInstancePart PartOf(CreationDocument document, int index, CreationStampTransform transform, int slot, float grow = 0f) {
        var (low, high) = BoxOf(document: document, index: index, transform: transform);
        var halo = new Vector3(value: (((document.Shapes![index].Smooth ?? 0f) * transform.Scale) + grow));

        return new SdfInstancePart(Detail: Of(scale: transform.Scale, shape: document.Shapes[index]), High: (high + halo), Low: (low - halo), Slot: slot);
    }
    /// <summary>Returns the parts of a creation emitted as one instance (<see cref="SdfInstanceRange.Parts"/>): each shown
    /// shape's (<see cref="PartOf"/>), and its relief's over the box its composed surface lies in at the relief's own
    /// spacing (<see cref="CreationNoiseDocument.DetailSpacing"/>), so each shape refines its own box at its own detail and
    /// the relief refines the creation's surface, never one detail spread over the creation's whole bound.</summary>
    /// <param name="document">The creation document.</param>
    /// <param name="hidden">The shapes left out, indexed like <see cref="CreationDocument.Shapes"/>; <see langword="null"/>
    /// for none.</param>
    /// <param name="transform">The stamp transform (<see cref="PartOf"/>).</param>
    /// <param name="slot">The parts' slot, or <see cref="SdfInstancePart.StaticSlot"/> for a static instance's parts.</param>
    /// <param name="relief">The relief the instance applies to its composed field; <see langword="null"/> for none.</param>
    /// <param name="reliefBounds">A box the relieved surface lies in when the shapes' composition does not hold all of
    /// it (text composed with them), in the parts' frame; <see langword="null"/> to compose the shapes' boxes.</param>
    /// <returns>The parts.</returns>
    internal static List<SdfInstancePart> PartsOf(CreationDocument document, bool[]? hidden, CreationStampTransform transform, int slot, CreationNoiseDocument? relief, (Vector3 Low, Vector3 High)? reliefBounds = null) {
        var shapes = (document.Shapes ?? []);
        var parts = new List<SdfInstancePart>(capacity: (shapes.Count + 1));
        var grow = ((relief?.Amplitude ?? 0f) * transform.Scale);
        var surface = (Low: new Vector3(value: float.PositiveInfinity), High: new Vector3(value: float.NegativeInfinity));

        for (var index = 0; (index < shapes.Count); index++) {
            if ((hidden is not null) && hidden[index]) { continue; }

            var shape = shapes[index];

            parts.Add(item: PartOf(document: document, index: index, transform: transform, slot: slot, grow: grow));
            surface = SdfBoundAlgebra.ComposeBox(accumulated: surface, blend: (shape.Blend ?? SdfBlendOp.Union), operand: BoxOf(document: document, index: index, transform: transform), radius: ((shape.Smooth ?? 0f) * transform.Scale));
        }

        if ((relief is not null) && (relief.DetailSpacing() is > 0f and var spacing)) {
            var (low, high) = (reliefBounds ?? surface);

            if ((low.X <= high.X) && (low.Y <= high.Y) && (low.Z <= high.Z)) {
                parts.Add(item: new SdfInstancePart(Detail: (spacing * transform.Scale), High: (high + new Vector3(value: grow)), Low: (low - new Vector3(value: grow)), Slot: slot));
            }
        }

        return parts;
    }
    // A shape's box under a stamp transform, before any halo (PartOf).
    private static (Vector3 Low, Vector3 High) BoxOf(CreationDocument document, int index, CreationStampTransform transform) {
        var shape = document.Shapes![index];

        if (shape.Domain is { Count: > 0 } domain) {
            var reach = ((((shape.Position?.Length() ?? 0f) + ShapeDomainOps.Reach(domain: domain)) * transform.Scale) +
                SdfSolidGeometry.Reach(type: shape.Type, scale: ((shape.Scale ?? Vector3.One) * transform.Scale), lift: (shape.Lift ?? SdfLift.Extrude)));

            return ((transform.Origin - new Vector3(value: reach)), (transform.Origin + new Vector3(value: reach)));
        }
        if (CreationStampEmitter.ShapeStampBox(document: document, shapeIndex: index, transform: transform) is { } box) {
            return box;
        }

        var (center, radius) = CreationStampEmitter.ShapeStampBound(document: document, shapeIndex: index, transform: transform);

        return ((center - new Vector3(value: radius)), (center + new Vector3(value: radius)));
    }
    // A shape's smallest and largest half extents along its own axes, in creation units (SdfSolidGeometry.HalfExtent: a
    // revolve's scale Z is an offset and may be zero while its radial extent remains positive).
    private static (float Smallest, float Largest) HalfExtents(ShapeDocument shape, Vector3 size) {
        var lift = (shape.Lift ?? SdfLift.Extrude);
        var x = SdfSolidGeometry.HalfExtent(type: shape.Type, scale: size, lift: lift, axis: Vector3.UnitX);
        var y = SdfSolidGeometry.HalfExtent(type: shape.Type, scale: size, lift: lift, axis: Vector3.UnitY);
        var z = SdfSolidGeometry.HalfExtent(type: shape.Type, scale: size, lift: lift, axis: Vector3.UnitZ);

        return (MathF.Max(x: 0f, y: MathF.Min(x: x, y: MathF.Min(x: y, y: z))), MathF.Max(x: x, y: MathF.Max(x: y, y: z)));
    }
}
