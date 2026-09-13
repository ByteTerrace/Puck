using System.Numerics;

using Puck.Text;

namespace Puck.SignedDistance;

public sealed partial class SdfProgramBuilder {
    /// <summary>Adds a single glyph cell sampled from a bound font atlas (see <c>Puck.SdfVm.SdfWorldEngine.SetGlyphAtlas</c>) as
    /// a distance-level field — text as real world geometry (marchable, liftable, blendable, and with
    /// <see cref="SdfBlendOp.Subtraction"/> engravable into any surface). The glyph is the atlas letter where the atlas
    /// is bound (the world-lit render) and the conservative extruded cell box everywhere else. Most callers use
    /// <see cref="Text(FontAtlas, string, Vector3, Vector3, Vector3, float, int, SdfBlendOp, float, float, TextLayoutOptions, int?, TextLayoutResult?)"/>, which
    /// bakes these arguments from a laid-out string; this primitive is the one-cell seam.
    /// <para>The cell's sampled alpha field is corrected using the atlas's measured derivative bounds and the
    /// packed UV mapping, including stretched cells. Metadata-only atlases use the worst-case RGBA8 bound.
    /// The correction preserves the reconstructed zero set, not the exact source outline. The atlas UVs
    /// are unorm2x16-packed host-side into two lanes so the ISA-wide <paramref name="smooth"/> radius keeps its lane
    /// (KEEP IN SYNC with SDF_SHAPE_GLYPH / sdfGlyphUnpackUv in Assets/Shaders/Sdf/sdf-vm.hlsli).</para></summary>
    /// <param name="uvBottomLeft">The atlas UV (in <c>[0, 1]²</c>) at the cell's local <c>(-halfWidth, -halfHeight)</c> corner.</param>
    /// <param name="uvTopRight">The atlas UV at the cell's local <c>(+halfWidth, +halfHeight)</c> corner.</param>
    /// <param name="halfWidth">The cell's local X half-extent, in world units.</param>
    /// <param name="halfHeight">The cell's local Y half-extent, in world units.</param>
    /// <param name="extrudeHalfDepth">The half-depth the glyph extrudes along local Z (clamped to ≥ 0).</param>
    /// <param name="distanceScale">The atlas distance range (in texels) times the world size of one texel: converts the
    /// encoded <c>[0, 1]</c> distance to world units. Host-baked (foot-gun discipline).</param>
    /// <param name="material">The material id the letter shades with.</param>
    /// <param name="blend">The blend against the field accumulated so far (Subtraction engraves).</param>
    /// <param name="smooth">The smooth/chamfer radius (meaningful only for a smooth/chamfer <paramref name="blend"/>).</param>
    /// <param name="atlas">The atlas whose pixels will be uploaded; its dimensions and alpha derivatives bound sampling.</param>
    /// <exception cref="ArgumentNullException"><paramref name="atlas"/> is null.</exception>
    /// <exception cref="ArgumentException">Atlas image dimensions disagree with its metadata, or the sampling
    /// correction cannot be represented.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A UV or cell dimension is not finite, a cell half-extent is not positive,
    /// <paramref name="distanceScale"/> is not finite and non-negative, <paramref name="material"/> is negative,
    /// <paramref name="blend"/> is not a defined <see cref="SdfBlendOp"/>, or <paramref name="smooth"/> is not
    /// finite.</exception>
    public SdfProgramBuilder Glyph(FontAtlas atlas, Vector2 uvBottomLeft, Vector2 uvTopRight, float halfWidth, float halfHeight, float extrudeHalfDepth, float distanceScale, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        ArgumentNullException.ThrowIfNull(atlas);
        // PackUv clamps UVs into [0, 1]. Positive cell extents keep the derivative mapping defined;
        // nonnegative distanceScale preserves the field's inside/outside convention.
        RequireFinite(
            value: uvBottomLeft,
            paramName: nameof(uvBottomLeft),
            subject: "A glyph atlas UV"
        );
        RequireFinite(
            value: uvTopRight,
            paramName: nameof(uvTopRight),
            subject: "A glyph atlas UV"
        );
        RequirePositive(
            value: halfWidth,
            paramName: nameof(halfWidth),
            subject: "A glyph cell half-width"
        );
        RequirePositive(
            value: halfHeight,
            paramName: nameof(halfHeight),
            subject: "A glyph cell half-height"
        );
        RequireFinite(
            value: extrudeHalfDepth,
            paramName: nameof(extrudeHalfDepth),
            subject: "A glyph extrude half-depth"
        );
        RequireNonNegative(
            value: distanceScale,
            paramName: nameof(distanceScale),
            subject: "A glyph distance scale"
        );

        return Shape(
            blend: blend,
            derived1: MathF.Abs(x: halfWidth),   // Data1.y = halfWidth
            derived2: MathF.Abs(x: halfHeight),  // Data1.z = halfHeight
            derived3: GlyphSamplingCorrection(atlas, uvBottomLeft, uvTopRight, halfWidth, halfHeight, distanceScale),
            dimensions: new Vector4(
                w: MathF.Max(
                    x: 0f,
                    y: extrudeHalfDepth
                ),  // Data0.w = extrudeHalfDepth
                x: PackUv(uv: uvBottomLeft),         // Data0.x = packed uvMin
                y: PackUv(uv: uvTopRight),           // Data0.y = packed uvMax
                z: distanceScale                     // Data0.z = distanceScale
            ),
            material: material,
            shape: SdfShapeType.Glyph,
            smooth: smooth
        );
    }

    private static float GlyphSamplingCorrection(FontAtlas atlas, Vector2 uvMin, Vector2 uvMax, float halfWidth, float halfHeight, float distanceScale) {
        if (atlas.ImageData is { } image && (image.Width != atlas.Width || image.Height != atlas.Height)) {
            throw new ArgumentException("Glyph atlas pixels must match its declared dimensions.", nameof(atlas));
        }
        var gradient = atlas.ImageData?.AlphaGradientBound ?? Vector2.One;
        var first = BitConverter.SingleToUInt32Bits(PackUv(uvMin));
        var last = BitConverter.SingleToUInt32Bits(PackUv(uvMax));
        // Decode the actual unorm16 endpoints. Two float ULPs at one cover their shader decode/subtract rounding;
        // the final relative guard covers the mapping arithmetic. Round the reciprocal downward as well.
        const double decodeSlack = 2d / 8388608;
        var spanX = Math.Abs((long)(first & 65535) - (last & 65535)) / 65535d + decodeSlack;
        var spanY = Math.Abs((long)(first >> 16) - (last >> 16)) / 65535d + decodeSlack;
        var x = gradient.X * (double)atlas.Width * spanX * distanceScale / (2d * halfWidth);
        var y = gradient.Y * (double)atlas.Height * spanY * distanceScale / (2d * halfHeight);
        var bound = Math.Max(1d, Math.Sqrt(x * x + y * y) * 1.00001d);
        var correction = MathF.BitDecrement((float)(1d / bound));
        if (!float.IsFinite(correction) || correction <= 0) {
            throw new ArgumentException("The glyph sampling scale cannot be represented safely.", nameof(atlas));
        }
        return correction;
    }
    /// <summary>Lays <paramref name="text"/> out against <paramref name="atlas"/> and emits one <see cref="Glyph"/> cell
    /// per drawn character, positioned on the plane spanned by <paramref name="right"/>/<paramref name="up"/> at
    /// <paramref name="origin"/> (the first line's baseline pen). Each glyph is a self-contained
    /// <see cref="ResetPoint"/> + transform + <see cref="Glyph"/> segment, so a whole string is a multi-segment run the
    /// caller wraps in one <see cref="BeginInstance"/>/<see cref="EndInstance"/> with a bound covering the block. The
    /// atlas must be uploaded to the engine (<c>Puck.SdfVm.SdfWorldEngine.SetGlyphAtlas</c>) for the letters to resolve;
    /// unbound, each cell renders as its conservative box.</summary>
    /// <param name="atlas">The font atlas providing glyph geometry, metrics, and per-glyph atlas rectangles.</param>
    /// <param name="text">The string to lay out (line feeds break lines; unmapped code points are skipped).</param>
    /// <param name="origin">The pen origin — the first line's baseline, left edge. World space, or the dynamic slot's
    /// local space when <paramref name="dynamicSlot"/> is supplied.</param>
    /// <param name="right">The unit axis local +X (advance direction) maps to, in the same space as <paramref name="origin"/>.</param>
    /// <param name="up">The unit axis local +Y (ascent direction) maps to; the glyphs extrude along right×up.</param>
    /// <param name="worldEmHeight">The world height of one em — the text's world scale.</param>
    /// <param name="material">The material id the letters shade with.</param>
    /// <param name="blend">The blend against the field accumulated so far (Subtraction engraves the text).</param>
    /// <param name="extrudeHalfDepth">The half-depth each glyph extrudes along the plane normal.</param>
    /// <param name="smooth">The smooth/chamfer radius for a smooth/chamfer <paramref name="blend"/>.</param>
    /// <param name="layout">The layout options (wrapping, alignment, tracking, line spacing) in the run's scaled
    /// units; <see langword="null"/> = <see cref="TextLayoutOptions.Default"/>.</param>
    /// <param name="dynamicSlot">A dynamic-transform slot each glyph's chain rides (<see cref="TransformDynamic"/>
    /// after its <see cref="ResetPoint"/>), so the whole run follows the slot's per-frame pose;
    /// <see langword="null"/> = a static run in world space.</param>
    /// <param name="precomputedLayout">A <see cref="TextLayoutResult"/> already computed for this exact
    /// (<paramref name="atlas"/>, <paramref name="text"/>, <paramref name="worldEmHeight"/>, <paramref name="layout"/>)
    /// — reused instead of laying the run out again (<see cref="TextLayout.Layout(FontAtlas, string, TextLayoutOptions, float)"/>
    /// is a pure function of those four inputs, so a caller that already computed it for the same call — measuring
    /// reach, say — gets a bit-identical result either way). The caller is responsible for keeping it in sync;
    /// <see langword="null"/> (the default) lays the run out fresh, exactly as before this parameter existed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="atlas"/> or <paramref name="text"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="worldEmHeight"/> is not finite and greater than
    /// zero, <paramref name="origin"/> or <paramref name="extrudeHalfDepth"/> is not finite, <paramref name="right"/>
    /// and <paramref name="up"/> are not orthogonal, or <paramref name="blend"/> is not a defined
    /// <see cref="SdfBlendOp"/>.</exception>
    public SdfProgramBuilder Text(FontAtlas atlas, string text, Vector3 origin, Vector3 right, Vector3 up, float worldEmHeight, int material, SdfBlendOp blend = SdfBlendOp.Union, float extrudeHalfDepth = 0.1f, float smooth = 0f, TextLayoutOptions? layout = null, int? dynamicSlot = null, TextLayoutResult? precomputedLayout = null) {
        ArgumentNullException.ThrowIfNull(atlas);
        ArgumentNullException.ThrowIfNull(text);

        // The pre-existing check missed NaN and +infinity: `NaN <= 0f` and `infinity <= 0f` are both false, so a
        // non-finite em height passed and divided into every glyph's world-per-texel scale.
        RequirePositive(
            value: worldEmHeight,
            paramName: nameof(worldEmHeight),
            subject: "A text world em height"
        );
        RequireFinite(
            value: origin,
            paramName: nameof(origin),
            subject: "A text origin"
        );
        RequireDirection(
            value: right,
            paramName: nameof(right),
            subject: "A text right axis"
        );
        RequireDirection(
            value: up,
            paramName: nameof(up),
            subject: "A text up axis"
        );
        RequireFinite(
            value: extrudeHalfDepth,
            paramName: nameof(extrudeHalfDepth),
            subject: "A text extrude half-depth"
        );

        // Uniform world-per-texel keeps text proportions; Glyph separately bounds the filtered alpha field.
        var worldPerTexel = (worldEmHeight / atlas.Size);
        var distanceScale = (atlas.DistanceRange * worldPerTexel);
        // Local (right, up, forward=right×up) → world: the rotation whose rows are the basis (System.Numerics'
        // row-vector Transform), so Rotate places each glyph's authored local XY onto the text plane.
        var unitRight = Vector3.Normalize(value: right);
        var unitUp = Vector3.Normalize(value: up);

        // Orthogonality subsumes the parallel case (a parallel pair has |dot| = 1, and its cross product normalizes to
        // NaN) and is what the two halves below need to agree: the pen places each glyph along unitRight/unitUp while
        // the glyph's own geometry rides the orthonormal quaternion built from them.
        RequireOrthogonalBasis(
            paramName: nameof(up),
            right: unitRight,
            subject: "A text right and up axis",
            up: unitUp
        );

        var forward = Vector3.Normalize(value: Vector3.Cross(
            vector1: unitRight,
            vector2: unitUp
        ));
        var orientation = Quaternion.CreateFromRotationMatrix(matrix: new Matrix4x4(
            m11: unitRight.X,
            m12: unitRight.Y,
            m13: unitRight.Z,
            m14: 0f,
            m21: unitUp.X,
            m22: unitUp.Y,
            m23: unitUp.Z,
            m24: 0f,
            m31: forward.X,
            m32: forward.Y,
            m33: forward.Z,
            m34: 0f,
            m41: 0f,
            m42: 0f,
            m43: 0f,
            m44: 1f
        ));
        var laidOut = (precomputedLayout ?? new TextLayout().Layout(
            atlas: atlas,
            options: (layout ?? TextLayoutOptions.Default),
            text: text,
            scale: worldEmHeight
        ));
        var atlasWidth = ((float)atlas.Width);
        var atlasHeight = ((float)atlas.Height);

        foreach (var placement in laidOut.Placements) {
            var atlasBounds = placement.AtlasBounds;
            var planeBounds = placement.PlaneBounds;
            // Uniform half-extents from the atlas cell's texel size; the cell CENTRE from the laid-out plane bounds (the
            // pen already placed it in the block). The two agree up to the padded margin, which is empty field.
            var halfWidth = ((0.5f * (atlasBounds.Right - atlasBounds.Left)) * worldPerTexel);
            var halfHeight = ((0.5f * (atlasBounds.Bottom - atlasBounds.Top)) * worldPerTexel);
            var centre2D = new Vector2(
                x: (0.5f * (planeBounds.Left + planeBounds.Right)),
                y: (0.5f * (planeBounds.Bottom + planeBounds.Top))
            );
            var worldCentre = ((origin + (unitRight * centre2D.X)) + (unitUp * centre2D.Y));
            // Local (-hw,-hh) is the cell's bottom-left → atlas (uMin, vBottom = the LARGER texel row, top-down); local
            // (+hw,+hh) is top-right → (uMax, vTop). The lerp in the shader maps local→uv along this diagonal.
            var uvBottomLeft = new Vector2(
                x: (atlasBounds.Left / atlasWidth),
                y: (atlasBounds.Bottom / atlasHeight)
            );
            var uvTopRight = new Vector2(
                x: (atlasBounds.Right / atlasWidth),
                y: (atlasBounds.Top / atlasHeight)
            );

            var chain = ResetPoint();

            if (dynamicSlot is { } slot) {
                chain = chain.TransformDynamic(slot: slot);
            }

            _ = chain
                .Translate(offset: worldCentre)
                .Rotate(rotation: orientation)
                .Glyph(
                atlas: atlas,
                blend: blend,
                distanceScale: distanceScale,
                extrudeHalfDepth: extrudeHalfDepth,
                halfHeight: halfHeight,
                halfWidth: halfWidth,
                material: material,
                smooth: smooth,
                uvBottomLeft: uvBottomLeft,
                uvTopRight: uvTopRight
            );
        }

        return this;
    }
}
