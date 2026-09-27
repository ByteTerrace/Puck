using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;

namespace Puck.Commands;

/// <summary>A mapping's chain in the single-precision form a GPU draws a face with: the warp's declared inverse, which
/// takes a face point to the face point the warp sampled, then one affine map from that point to the source, normalized
/// to its extent, that folds the layout, the fit and the crop together. <see cref="SourceMapping.MapRay"/> runs the same
/// chain in fixed point.
/// <para>Each <see cref="Matrix3x2"/> maps a row vector: a point <c>(u, v)</c> goes to
/// <c>(u·M11 + v·M21 + M31, u·M12 + v·M22 + M32)</c>. A face point whose warped point lies outside the unit square falls on
/// the warp's border (the screen glass's bezel); a source point outside <see cref="Crop"/> falls on a letterbox bar when
/// <see cref="Letterboxes"/> is set.</para></summary>
/// <param name="Warp">The warp's declared inverse, from a face point to the face point the warp sampled; the identity
/// for a mapping with no warp.</param>
/// <param name="Image">The map from a warped face point to the source, normalized to its extent: the layout, then the
/// fit, then the crop.</param>
/// <param name="Crop">The crop, normalized to the source's extent.</param>
/// <param name="Texel">One source pixel, normalized to the source's extent: the reciprocal of its width and
/// height.</param>
/// <param name="Letterboxes">Whether a source point outside <see cref="Crop"/> is a letterbox bar, which the fit
/// <see cref="SourceFit.Contain"/> leaves; the other fits never reach past the crop.</param>
/// <param name="Filter">The filter the source is sampled with (<see cref="SourceMapping.Filter"/>).</param>
public readonly record struct SourceDraw(Matrix3x2 Warp, Matrix3x2 Image, NormalizedRect Crop, Vector2 Texel, bool Letterboxes, GpuSamplerFilter Filter);
public sealed partial record SourceMapping {
    /// <summary>Returns the chain a surface placement's face is drawn through, in single precision: the form the SDF
    /// screen shading reads. It composes the layout, fit and crop in double precision and rounds each coefficient
    /// once.</summary>
    /// <returns>The draw form.</returns>
    /// <exception cref="InvalidOperationException">The placement is not a <see cref="SourcePlacement.Surface"/>, the warp
    /// declares no inverse, or the mapping's shape is invalid.</exception>
    public SourceDraw Draw() {
        RequireShape();

        if (Placement is not SourcePlacement.Surface surface) {
            throw new InvalidOperationException(message: $"The source '{Source.Name}' is placed on a pane, whose face aspect depends on the host's display; only a surface is drawn from its mapping.");
        }

        var warp = Matrix3x2.Identity;

        if (Warp is { } drawn) {
            if (drawn.Inverse is not SourceWarpInverse.Affine affine) {
                throw new InvalidOperationException(message: $"The warp pass '{drawn.Pass}' declares no inverse, so the source '{Source.Name}' cannot be drawn from its mapping.");
            }

            warp = new Matrix3x2(
                m11: affine.M11,
                m12: affine.M21,
                m21: affine.M12,
                m22: affine.M22,
                m31: affine.M13,
                m32: affine.M23
            );
        }

        // The layout as an affine map of the unit square, (x, y) = (a·u + b·v + c, d·u + e·v + f).
        var (a, b, c, d, e, f) = Layout switch {
            SourceUvLayout.Rotate90 => (0d, 1d, 0d, -1d, 0d, 1d),
            SourceUvLayout.Rotate180 => (-1d, 0d, 1d, 0d, -1d, 1d),
            SourceUvLayout.Rotate270 => (0d, -1d, 1d, 1d, 0d, 0d),
            SourceUvLayout.MirrorHorizontal => (-1d, 0d, 1d, 0d, 1d, 0d),
            SourceUvLayout.MirrorVertical => (1d, 0d, 0d, 0d, -1d, 1d),
            SourceUvLayout.Transpose => (0d, 1d, 0d, 1d, 0d, 0d),
            SourceUvLayout.Transverse => (0d, -1d, 1d, -1d, 0d, 1d),
            _ => (1d, 0d, 0d, 0d, 1d, 0d),
        };

        if (Fit != SourceFit.Stretch) {
            // The fit scales one image axis about its centre by the crop's aspect ratio over the face's as the image sees
            // it, as MapFace does in fixed point.
            var aspect = (((double)surface.HalfWidth) / surface.HalfHeight);
            var ratio = (IsTransposing(layout: Layout)
                ? ((Crop.Width * aspect) / Crop.Height)
                : (Crop.Width / (Crop.Height * aspect))
            );
            var horizontal = ((Fit == SourceFit.Contain)
                ? (ratio < 1d)
                : (ratio > 1d)
            );

            if (horizontal) {
                (a, b, c) = ((a / ratio), (b / ratio), (((c - 0.5d) / ratio) + 0.5d));
            } else {
                (d, e, f) = ((d * ratio), (e * ratio), (((f - 0.5d) * ratio) + 0.5d));
            }
        }

        // The crop places the fitted point in source pixels, normalized to the source's extent.
        var cropX = (((double)Crop.X) / SourceWidth);
        var cropY = (((double)Crop.Y) / SourceHeight);
        var cropWidth = (((double)Crop.Width) / SourceWidth);
        var cropHeight = (((double)Crop.Height) / SourceHeight);

        return new SourceDraw(
            Crop: new NormalizedRect(
                Height: ((float)cropHeight),
                Width: ((float)cropWidth),
                X: ((float)cropX),
                Y: ((float)cropY)
            ),
            Image: new Matrix3x2(
                m11: ((float)(cropWidth * a)),
                m12: ((float)(cropHeight * d)),
                m21: ((float)(cropWidth * b)),
                m22: ((float)(cropHeight * e)),
                m31: ((float)(cropX + (cropWidth * c))),
                m32: ((float)(cropY + (cropHeight * f)))
            ),
            Filter: Filter,
            Letterboxes: (Fit == SourceFit.Contain),
            Texel: new Vector2(
                x: ((float)(1d / SourceWidth)),
                y: ((float)(1d / SourceHeight))
            ),
            Warp: warp
        );
    }
}
