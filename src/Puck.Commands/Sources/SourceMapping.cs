using System.Diagnostics.CodeAnalysis;
using Puck.Abstractions.Presentation;
using Puck.Maths;

namespace Puck.Commands;

/// <summary>The published chain from where a source is shown to the source's pixels: the placement, any warp pass the
/// face is drawn through, the UV layout, the fit that letterboxes a crop, and the crop itself. It is data: every hit is
/// mapped from it in fixed point, it is what the GPU is to draw with, and nothing reads a mapping back from the
/// GPU.</summary>
/// <remarks>
/// <para>A point on the face runs the chain from the display side: the warp's declared inverse takes it to the face point
/// the warp sampled, <see cref="Layout"/> turns that into an image point, <see cref="Fit"/> takes the image point to a
/// point of the crop, and the crop places it in source pixels. Drawing runs the same chain the other way.</para>
/// <para>Floats enter fixed point once, through <see cref="FixedVector3.FromVector3"/> and
/// <see cref="FixedQ4816.FromDouble"/>, so the same mapping and input produce a bit-identical <see cref="SourceHit"/>
/// on every run and machine.</para>
/// </remarks>
/// <param name="Source">The image shown.</param>
/// <param name="Placement">Where it is shown.</param>
/// <param name="SourceWidth">The image's width, in pixels; positive.</param>
/// <param name="SourceHeight">The image's height, in pixels; positive.</param>
/// <param name="Crop">The part of the image shown, inside it and not empty.</param>
/// <param name="Layout">How the crop is turned or reflected onto the face.</param>
/// <param name="Fit">How the crop is fitted to a face of another aspect ratio.</param>
/// <param name="Warp">The warp pass the face is drawn through, or <see langword="null"/> for none.</param>
/// <param name="Destination">Where a point mapped onto the source goes.</param>
/// <param name="Opener">Who opened the source.</param>
public sealed record SourceMapping(
    SourceHandle Source,
    SourcePlacement Placement,
    int SourceWidth,
    int SourceHeight,
    SourcePixelRect Crop,
    SourceUvLayout Layout = SourceUvLayout.Identity,
    SourceFit Fit = SourceFit.Stretch,
    SourceWarp? Warp = null,
    SourceDestination Destination = SourceDestination.Presentation,
    SourceOpener Opener = SourceOpener.Document
) {
    private static readonly FixedQ4816 Half = FixedQ4816.FromRawBits(value: (FixedQ4816.One.Value >> 1));

    /// <summary>Gets whether a hit on the face can reach the source: a warp pass the face is drawn through must declare
    /// its inverse.</summary>
    public bool AcceptsInput => ((Warp?.Inverse is not null) || (Warp is null));

    private static bool IsInside(FixedQ4816 value) => (
        (value >= FixedQ4816.Zero) &&
        (value < FixedQ4816.One)
    );
    private static bool IsTransposing(SourceUvLayout layout) =>
        (layout is SourceUvLayout.Rotate90 or SourceUvLayout.Rotate270 or SourceUvLayout.Transpose or SourceUvLayout.Transverse);
    // The shape every map needs before it can divide: extents, crop, placement and warp coefficients. Destination and
    // opener rules are TryValidate's alone, so a mapping they refuse still maps for presentation.
    private string? ShapeRefusal() {
        if (string.IsNullOrWhiteSpace(value: Source.Name)) {
            return "The source handle names nothing.";
        }
        if (
            (SourceWidth <= 0) ||
            (SourceHeight <= 0)
        ) {
            return $"The source extent {SourceWidth}x{SourceHeight} is not positive.";
        }
        if (
            (Crop.Width <= 0) ||
            (Crop.Height <= 0) ||
            (Crop.X < 0) ||
            (Crop.Y < 0) ||
            ((((long)Crop.X) + Crop.Width) > SourceWidth) ||
            ((((long)Crop.Y) + Crop.Height) > SourceHeight)
        ) {
            return $"The crop {Crop.Width}x{Crop.Height} at ({Crop.X}, {Crop.Y}) is empty or leaves the {SourceWidth}x{SourceHeight} source.";
        }
        if (
            !Enum.IsDefined(value: Layout) ||
            !Enum.IsDefined(value: Fit) ||
            !Enum.IsDefined(value: Destination) ||
            !Enum.IsDefined(value: Opener)
        ) {
            return "The layout, fit, destination or opener is not a defined value.";
        }

        switch (Placement) {
            case SourcePlacement.Surface surface:
                if (
                    !VectorFunctions.IsFinite(vector: surface.Origin) ||
                    !VectorFunctions.IsFinite(vector: surface.Right) ||
                    !VectorFunctions.IsFinite(vector: surface.Up) ||
                    !float.IsFinite(f: surface.HalfWidth) ||
                    !float.IsFinite(f: surface.HalfHeight) ||
                    (surface.HalfWidth <= 0f) ||
                    (surface.HalfHeight <= 0f) ||
                    (FixedQ4816.FromDouble(value: surface.HalfWidth) == FixedQ4816.Zero) ||
                    (FixedQ4816.FromDouble(value: surface.HalfHeight) == FixedQ4816.Zero)
                ) {
                    return "The surface's frame is not finite or its half extents are not positive.";
                }
                if (FixedVector3.Cross(
                    left: FixedVector3.FromVector3(value: surface.Right),
                    right: FixedVector3.FromVector3(value: surface.Up)
                ) == FixedVector3.Zero) {
                    return "The surface's right and up axes are parallel or zero, so they span no plane.";
                }

                break;
            case SourcePlacement.Pane pane:
                if (
                    !float.IsFinite(f: pane.Region.X) ||
                    !float.IsFinite(f: pane.Region.Y) ||
                    !float.IsFinite(f: pane.Region.Width) ||
                    !float.IsFinite(f: pane.Region.Height) ||
                    (FixedQ4816.FromDouble(value: pane.Region.Width) <= FixedQ4816.Zero) ||
                    (FixedQ4816.FromDouble(value: pane.Region.Height) <= FixedQ4816.Zero)
                ) {
                    return "The pane's region is not finite or its width and height are not positive.";
                }

                break;
            default:
                return "The mapping names no placement.";
        }

        if (Warp is { } warp) {
            if (string.IsNullOrWhiteSpace(value: warp.Pass)) {
                return "The warp names no pass.";
            }
            if (
                (warp.Inverse is SourceWarpInverse.Affine affine) &&
                !(float.IsFinite(f: affine.M11) && float.IsFinite(f: affine.M12) && float.IsFinite(f: affine.M13) &&
                  float.IsFinite(f: affine.M21) && float.IsFinite(f: affine.M22) && float.IsFinite(f: affine.M23))
            ) {
                return $"The warp pass '{warp.Pass}' declares an inverse whose coefficients are not finite.";
            }
        }

        return null;
    }
    private void RequireShape() {
        if (ShapeRefusal() is { } refusal) {
            throw new InvalidOperationException(message: refusal);
        }
    }
    private SourceHit MapFace(FixedQ4816 distance, FixedVector2 face, FixedQ4816 faceAspect) {
        var outcome = ((IsInside(value: face.X) && IsInside(value: face.Y))
            ? SourceHitOutcome.OnSource
            : SourceHitOutcome.OutsidePlacement
        );
        var sampled = face;

        if (Warp is { } warp) {
            if (warp.Inverse is not SourceWarpInverse.Affine affine) {
                return new SourceHit(
                    Coordinate: FixedVector2.Zero,
                    Distance: distance,
                    Face: face,
                    Outcome: SourceHitOutcome.WarpNotInvertible
                );
            }

            sampled = new FixedVector2(
                X: (((FixedQ4816.FromDouble(value: affine.M11) * face.X) + (FixedQ4816.FromDouble(value: affine.M12) * face.Y)) + FixedQ4816.FromDouble(value: affine.M13)),
                Y: (((FixedQ4816.FromDouble(value: affine.M21) * face.X) + (FixedQ4816.FromDouble(value: affine.M22) * face.Y)) + FixedQ4816.FromDouble(value: affine.M23))
            );

            if (
                (outcome == SourceHitOutcome.OnSource) &&
                !(IsInside(value: sampled.X) && IsInside(value: sampled.Y))
            ) {
                outcome = SourceHitOutcome.OutsideWarp;
            }
        }

        var (u, v) = (sampled.X, sampled.Y);
        var (x, y) = Layout switch {
            SourceUvLayout.Rotate90 => (v, (FixedQ4816.One - u)),
            SourceUvLayout.Rotate180 => ((FixedQ4816.One - u), (FixedQ4816.One - v)),
            SourceUvLayout.Rotate270 => ((FixedQ4816.One - v), u),
            SourceUvLayout.MirrorHorizontal => ((FixedQ4816.One - u), v),
            SourceUvLayout.MirrorVertical => (u, (FixedQ4816.One - v)),
            SourceUvLayout.Transpose => (v, u),
            SourceUvLayout.Transverse => ((FixedQ4816.One - v), (FixedQ4816.One - u)),
            _ => (u, v),
        };

        if (Fit != SourceFit.Stretch) {
            // The crop's aspect ratio over the image's, where the image sees the face turned over by a transposing layout.
            // A face or crop so much wider than tall that a ratio rounds to zero keeps the smallest positive ratio, so the
            // map stays a total function of its input.
            var aspect = FixedQ4816.Max(
                x: faceAspect,
                y: FixedQ4816.Epsilon
            );
            var cropWidth = FixedQ4816.FromInteger(value: Crop.Width);
            var cropHeight = FixedQ4816.FromInteger(value: Crop.Height);
            var ratio = (IsTransposing(layout: Layout)
                ? ((cropWidth * aspect) / cropHeight)
                : (cropWidth / (cropHeight * aspect))
            );

            ratio = FixedQ4816.Max(
                x: ratio,
                y: FixedQ4816.Epsilon
            );

            var horizontal = ((Fit == SourceFit.Contain)
                ? (ratio < FixedQ4816.One)
                : (ratio > FixedQ4816.One)
            );

            if (horizontal) {
                x = (((x - Half) / ratio) + Half);
            } else {
                y = (((y - Half) * ratio) + Half);
            }
            if (
                (Fit == SourceFit.Contain) &&
                (outcome == SourceHitOutcome.OnSource) &&
                !(IsInside(value: x) && IsInside(value: y))
            ) {
                outcome = SourceHitOutcome.Letterbox;
            }
        }

        return new SourceHit(
            Coordinate: new FixedVector2(
                X: (FixedQ4816.FromInteger(value: Crop.X) + (FixedQ4816.FromInteger(value: Crop.Width) * x)),
                Y: (FixedQ4816.FromInteger(value: Crop.Y) + (FixedQ4816.FromInteger(value: Crop.Height) * y))
            ),
            Distance: distance,
            Face: face,
            Outcome: outcome
        );
    }

    /// <summary>Validates the mapping: its shape, and the destination rules. A <see cref="SourceDestination.Passthrough"/>
    /// destination needs a source the local user opened; a <see cref="SourceDestination.Simulation"/> destination needs
    /// a surface, because a pane's aspect ratio depends on the host's display rather than on document data; and an input
    /// destination needs a warp that declares its inverse.</summary>
    /// <param name="refusal">The refusal naming what is wrong, when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the mapping is valid.</returns>
    public bool TryValidate([NotNullWhen(returnValue: false)] out string? refusal) {
        refusal = ShapeRefusal();

        if (refusal is not null) {
            return false;
        }
        if (
            (Destination == SourceDestination.Passthrough) &&
            !SourcePassthrough.IsPermitted(opener: Opener)
        ) {
            refusal = $"The source '{Source.Name}' takes the passthrough destination, which exists only for a source the local user opened on their own machine; a world document can never create one or send it input.";

            return false;
        }
        if (
            (Destination == SourceDestination.Simulation) &&
            (Placement is not SourcePlacement.Surface)
        ) {
            refusal = $"The source '{Source.Name}' takes the simulation destination on a pane, whose aspect ratio depends on the host's display; the simulation maps only a surface placement, from document data.";

            return false;
        }
        if (
            (Destination != SourceDestination.Presentation) &&
            !AcceptsInput
        ) {
            refusal = $"The source '{Source.Name}' takes the {Destination.ToString().ToLowerInvariant()} destination through the warp pass '{Warp!.Pass}', which declares no inverse, so it cannot be an input path.";

            return false;
        }

        return true;
    }
    /// <summary>Maps a ray onto a surface placement's source pixels.</summary>
    /// <param name="ray">The ray, in the world the surface stands in.</param>
    /// <returns>The hit; <see cref="SourceHitOutcome.NoIntersection"/> when the ray misses the surface's plane.</returns>
    /// <exception cref="InvalidOperationException">The placement is not a <see cref="SourcePlacement.Surface"/>, or the
    /// mapping's shape is invalid.</exception>
    public SourceHit MapRay(SourceRay ray) {
        RequireShape();

        if (Placement is not SourcePlacement.Surface surface) {
            throw new InvalidOperationException(message: $"The source '{Source.Name}' is placed on a pane, which a display point maps, not a ray.");
        }

        var center = FixedVector3.FromVector3(value: surface.Origin);
        var right = FixedVector3.FromVector3(value: surface.Right);
        var up = FixedVector3.FromVector3(value: surface.Up);
        var halfWidth = FixedQ4816.FromDouble(value: surface.HalfWidth);
        var halfHeight = FixedQ4816.FromDouble(value: surface.HalfHeight);

        if (!FixedVector3.TryIntersectPlane(
            direction: ray.Direction,
            distance: out var distance,
            origin: ray.Origin,
            planeNormal: FixedVector3.Cross(
                left: right,
                right: up
            ),
            planePoint: center
        )) {
            return new SourceHit(
                Coordinate: FixedVector2.Zero,
                Distance: FixedQ4816.Zero,
                Face: FixedVector2.Zero,
                Outcome: SourceHitOutcome.NoIntersection
            );
        }

        var local = ((ray.Origin + (ray.Direction * distance)) - center);

        return MapFace(
            distance: distance,
            face: new FixedVector2(
                X: ((FixedVector3.Dot(
                    left: local,
                    right: right
                ) + halfWidth) / (halfWidth + halfWidth)),
                Y: ((halfHeight - FixedVector3.Dot(
                    left: local,
                    right: up
                )) / (halfHeight + halfHeight))
            ),
            faceAspect: (halfWidth / halfHeight)
        );
    }
    /// <summary>Maps a point on the display onto a pane placement's source pixels.</summary>
    /// <param name="point">The point, in display pixels from the display's top-left corner.</param>
    /// <param name="displayWidth">The display's width, in pixels; positive.</param>
    /// <param name="displayHeight">The display's height, in pixels; positive.</param>
    /// <returns>The hit; its <see cref="SourceHit.Distance"/> is zero.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="displayWidth"/> or <paramref name="displayHeight"/>
    /// is not positive.</exception>
    /// <exception cref="InvalidOperationException">The placement is not a <see cref="SourcePlacement.Pane"/>, or the
    /// mapping's shape is invalid.</exception>
    public SourceHit MapDisplayPoint(FixedVector2 point, int displayWidth, int displayHeight) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: displayWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: displayHeight);
        RequireShape();

        if (Placement is not SourcePlacement.Pane pane) {
            throw new InvalidOperationException(message: $"The source '{Source.Name}' is placed on a world surface, which a ray maps, not a display point.");
        }

        var width = FixedQ4816.FromInteger(value: displayWidth);
        var height = FixedQ4816.FromInteger(value: displayHeight);
        var paneWidth = (FixedQ4816.FromDouble(value: pane.Region.Width) * width);
        var paneHeight = (FixedQ4816.FromDouble(value: pane.Region.Height) * height);

        return MapFace(
            distance: FixedQ4816.Zero,
            face: new FixedVector2(
                X: ((point.X - (FixedQ4816.FromDouble(value: pane.Region.X) * width)) / paneWidth),
                Y: ((point.Y - (FixedQ4816.FromDouble(value: pane.Region.Y) * height)) / paneHeight)
            ),
            faceAspect: (paneWidth / paneHeight)
        );
    }
    /// <summary>Creates the mapping for a whole source stretched over a pane, the shape a pipeline or graph pane shows.</summary>
    /// <param name="source">The image shown.</param>
    /// <param name="region">The pane's rectangle in normalized display coordinates.</param>
    /// <param name="width">The image's width, in pixels.</param>
    /// <param name="height">The image's height, in pixels.</param>
    /// <returns>The mapping, with the default layout, fit, destination and opener.</returns>
    public static SourceMapping WholePane(SourceHandle source, NormalizedRect region, int width, int height) => new(
        Crop: SourcePixelRect.Whole(
            height: height,
            width: width
        ),
        Placement: new SourcePlacement.Pane(Region: region),
        Source: source,
        SourceHeight: height,
        SourceWidth: width
    );
}
