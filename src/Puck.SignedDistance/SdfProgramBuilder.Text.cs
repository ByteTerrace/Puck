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
    /// <para>The cell must map with uniform scale — <paramref name="halfWidth"/>/<paramref name="halfHeight"/>
    /// proportional to the atlas cell's texel width/height — for the field to stay 1-Lipschitz (factor 1, no step
    /// clamp); a stretched cell is the caller's risk, exactly as <see cref="Repeat"/>'s in-cell rule is. The atlas UVs
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
    /// <exception cref="ArgumentOutOfRangeException">A UV or cell dimension is not finite,
    /// <paramref name="distanceScale"/> is not finite and non-negative, <paramref name="material"/> is negative,
    /// <paramref name="blend"/> is not a defined <see cref="SdfBlendOp"/>, or <paramref name="smooth"/> is not
    /// finite.</exception>
    public SdfProgramBuilder Glyph(Vector2 uvBottomLeft, Vector2 uvTopRight, float halfWidth, float halfHeight, float extrudeHalfDepth, float distanceScale, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        // PackUv clamps the UVs into [0, 1] and the half-extents/extrusion take MathF.Abs / MathF.Max(0), so only
        // finiteness is refused there. distanceScale is the ONE lane packed raw: the decoder gates the atlas tap on
        // `dQuad < 0.5 * distanceScale` and then converts with `(0.5 - encoded) * distanceScale`, so a negative scale
        // inverts inside and outside.
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
        RequireFinite(
            value: halfWidth,
            paramName: nameof(halfWidth),
            subject: "A glyph cell half-width"
        );
        RequireFinite(
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

        // Uniform world-per-texel (atlas.Size = pixels per em): every glyph derives BOTH half-extents from it, so the
        // sampled field stays 1-Lipschitz (factor 1). distanceScale rides the same factor.
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
