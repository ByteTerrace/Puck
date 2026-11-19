using System.Numerics;

using Puck.Maths;
using Puck.Text;

namespace Puck.SignedDistance;

public sealed partial class SdfProgramBuilder {
    /// <summary>Adds a single glyph cell sampled from a bound font atlas (see <c>Puck.SdfVm.SdfWorldTables.SetGlyphAtlas</c>) as
    /// a distance-level field — text as real world geometry (marchable, liftable, blendable, and with
    /// <see cref="SdfBlendOp.Subtraction"/> engravable into any surface). The glyph is the atlas letter where the atlas
    /// is bound (the world-lit render) and the conservative extruded cell box everywhere else. Most callers use
    /// <see cref="Text(FontAtlas, string, Vector3, Vector3, Vector3, float, int, SdfBlendOp, float, float, TextLayoutOptions, int?, TextLayoutResult?)"/>, which
    /// bakes these arguments from a laid-out string; this primitive is the one-cell seam.
    /// <para>The cell's sampled alpha field is corrected using the atlas's measured derivative bounds and the
    /// packed UV mapping, including stretched cells. Metadata-only atlases use the worst-case RGBA8 bound.
    /// The correction preserves the reconstructed zero set, not the exact source outline. The atlas UVs
    /// are unorm2x16-packed host-side into two lanes so the ISA-wide <paramref name="smooth"/> radius keeps its lane
    /// (KEEP IN SYNC with SDF_SHAPE_GLYPH / sdfGlyphUnpackUv in Assets/Shaders/Sdf/field/sdf-shapes.hlsli).</para></summary>
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
    /// <exception cref="InvalidOperationException">The builder already holds a glyph of an atlas with other pixels: one
    /// atlas binds for every glyph of a program (<see cref="SdfProgram.GlyphAtlas"/>).</exception>
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
        if (atlas.ImageData is { } pixels) { SampleGlyphAtlas(atlas: pixels.ContentHash); }
        m_glyphStroke = MathF.Min(x: m_glyphStroke, y: GlyphStrokeScale(atlas: atlas));

        return Shape(
            blend: blend,
            derived1: MathF.Abs(x: halfWidth),   // Data1.y = halfWidth
            derived2: MathF.Abs(x: halfHeight),  // Data1.z = halfHeight
            derived3: GlyphSamplingCorrection(
                atlas: atlas,
                distanceScale: distanceScale,
                halfHeight: halfHeight,
                halfWidth: halfWidth,
                uvMax: uvTopRight,
                uvMin: uvBottomLeft
            ),
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

    // Records the pixels an emitted glyph samples: the program's one bound atlas (SdfProgram.GlyphAtlas).
    private void SampleGlyphAtlas(Puck.Assets.AssetContentHash atlas) {
        if ((m_glyphAtlas != default) && (m_glyphAtlas != atlas)) {
            throw new InvalidOperationException(message: $"A program binds one glyph atlas for all its glyphs, and this builder already emitted glyphs of atlas {m_glyphAtlas}; a glyph of atlas {atlas} would sample the other atlas's pixels. Pack both fonts into one catalog (PackedFontAtlasCatalog).");
        }
        m_glyphAtlas = atlas;
    }

    private static float GlyphSamplingCorrection(FontAtlas atlas, Vector2 uvMin, Vector2 uvMax, float halfWidth, float halfHeight, float distanceScale) {
        if (
            (atlas.ImageData is { } image) &&
            ((image.Width != atlas.Width) || (image.Height != atlas.Height))
        ) {
            throw new ArgumentException(
                message: "Glyph atlas pixels must match its declared dimensions.",
                paramName: nameof(atlas)
            );
        }
        var gradient = (atlas.ImageData?.AlphaGradientBound ?? Vector2.One);
        var first = BitConverter.SingleToUInt32Bits(value: PackUv(uv: uvMin));
        var last = BitConverter.SingleToUInt32Bits(value: PackUv(uv: uvMax));
        // Decode the actual unorm16 endpoints. Two float ULPs at one cover their shader decode/subtract rounding;
        // the final relative guard covers the mapping arithmetic. Round the reciprocal downward as well.
        const double DecodeSlack = (2d / 8388608);
        var spanX = ((Math.Abs(value: (((long)(first & 65535)) - (last & 65535))) / 65535d) + DecodeSlack);
        var spanY = ((Math.Abs(value: (((long)(first >> 16)) - (last >> 16))) / 65535d) + DecodeSlack);
        var x = ((((gradient.X * ((double)atlas.Width)) * spanX) * distanceScale) / (2d * halfWidth));
        var y = ((((gradient.Y * ((double)atlas.Height)) * spanY) * distanceScale) / (2d * halfHeight));
        var bound = Math.Max(
            val1: 1d,
            val2: (Math.Sqrt(d: ((x * x) + (y * y))) * 1.00001d)
        );
        var correction = MathF.BitDecrement(x: ((float)(1d / bound)));

        if (
            !float.IsFinite(f: correction) ||
            (correction <= 0)
        ) {
            throw new ArgumentException(
                message: "The glyph sampling scale cannot be represented safely.",
                paramName: nameof(atlas)
            );
        }
        return correction;
    }

    /// <summary>A regular weight's thinnest stroke, in ems: the width <see cref="GlyphDetail"/> takes a run's letters'
    /// thinnest strokes to be. A sans face's horizontals are thinner than its stems (Inter Regular's are 168 and 190 of
    /// 2048 units, 0.082 and 0.093 em), so a letter's arms are the strokes its bake must hold.</summary>
    public const float GlyphStrokeEms = 0.08f;

    /// <summary>The fine cells <see cref="GlyphDetail"/> asks across a run's thinnest stroke, and the cells the mesh bake's
    /// lettering stroke rule thickens a stroke to where its group's tier cell is coarser than that
    /// (<see cref="SdfProgram.GlyphStroke"/>).</summary>
    public const float GlyphStrokeCells = 3f;

    /// <summary>Returns the thinnest stroke an atlas's glyphs are taken to have, in units of a glyph's distance scale as
    /// <see cref="Text(FontAtlas, string, Vector3, Vector3, Vector3, float, int, SdfBlendOp, float, float, TextLayoutOptions, int?, TextLayoutResult?)"/>
    /// lays it out (the atlas's <see cref="FontAtlas.DistanceRange"/> texels of the world size of one texel, one em being
    /// <see cref="FontAtlas.Size"/> texels): <see cref="GlyphStrokeEms"/> ems.</summary>
    /// <param name="atlas">The atlas.</param>
    /// <returns>The stroke over the distance scale; infinity for an atlas with no positive size or distance range,
    /// whose glyphs are never thickened.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="atlas"/> is <see langword="null"/>.</exception>
    public static float GlyphStrokeScale(FontAtlas atlas) {
        ArgumentNullException.ThrowIfNull(atlas);

        var scale = ((GlyphStrokeEms * atlas.Size) / atlas.DistanceRange);

        return ((float.IsFinite(f: scale) && (scale > 0f)) ? scale : float.PositiveInfinity);
    }

    /// <summary>Returns the spacing a run's glyphs ask of the mesh bake: the lesser of the half slab each glyph extrudes
    /// and a third of its thinnest stroke (<see cref="GlyphStrokeEms"/>, <see cref="GlyphStrokeCells"/>). A plate's two
    /// faces need only a sample between them, but a stroke is a ribbon whose crossings the contour joins along its
    /// length: a stroke two cells across breaks into pieces wherever the lattice falls on both its edges, and one three
    /// cells across keeps a run of inside samples down its length.</summary>
    /// <param name="worldEmHeight">The world height of one em.</param>
    /// <param name="extrudeHalfDepth">The half depth each glyph extrudes along its plane's normal.</param>
    /// <returns>The spacing, in world units.</returns>
    public static float GlyphDetail(float worldEmHeight, float extrudeHalfDepth) =>
        MathF.Min(x: MathF.Abs(x: extrudeHalfDepth), y: ((GlyphStrokeEms / GlyphStrokeCells) * worldEmHeight));

    /// <summary>Lays <paramref name="text"/> out against <paramref name="atlas"/> and emits one <see cref="Glyph"/> cell
    /// per drawn character, positioned on the plane spanned by <paramref name="right"/>/<paramref name="up"/> at
    /// <paramref name="origin"/> (the first line's baseline pen). Each glyph is a self-contained
    /// <see cref="ResetPoint"/> + transform + <see cref="Glyph"/> segment, so a whole string is a multi-segment run the
    /// caller wraps in one <see cref="BeginInstance"/>/<see cref="EndInstance"/> with a bound covering the block. The
    /// atlas must be uploaded to the engine (<c>Puck.SdfVm.SdfWorldTables.SetGlyphAtlas</c>) for the letters to resolve;
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
        var orientation = VectorFunctions.FromBasis(forward: forward, right: unitRight, up: unitUp);
        var laidOut = (precomputedLayout ?? new TextLayout().Layout(
            atlas: atlas,
            options: (layout ?? TextLayoutOptions.Default),
            text: text,
            scale: worldEmHeight
        ));
        var atlasWidth = ((float)atlas.Width);
        var atlasHeight = ((float)atlas.Height);
        // The box the run's glyph cells span in the frame the chains ride, which an instance of text alone is fitted to
        // (SdfInstanceRange.Fitted): each cell's rectangle on the text plane, extruded by the relief and a smooth
        // blend's radius.
        var firstInstruction = m_instructions.Count;
        var spanLow = new Vector3(value: float.PositiveInfinity);
        var spanHigh = new Vector3(value: float.NegativeInfinity);
        var relief = (MathF.Max(x: extrudeHalfDepth, y: 0f) + MathF.Max(x: smooth, y: 0f));

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
            var reach = (((Vector3.Abs(value: unitRight) * MathF.Abs(x: halfWidth)) + (Vector3.Abs(value: unitUp) * MathF.Abs(x: halfHeight))) + (Vector3.Abs(value: forward) * relief));

            spanLow = Vector3.Min(value1: spanLow, value2: (worldCentre - reach));
            spanHigh = Vector3.Max(value1: spanHigh, value2: (worldCentre + reach));
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

        if ((m_openInstanceFirst >= 0) && (spanLow.X <= spanHigh.X)) {
            var frame = (dynamicSlot ?? -1);
            var emitted = (m_instructions.Count - firstInstruction);
            var detail = GlyphDetail(worldEmHeight: worldEmHeight, extrudeHalfDepth: extrudeHalfDepth);

            m_openInstanceGlyphs = ((m_openInstanceGlyphs is { } glyphs)
                ? (Vector3.Min(value1: glyphs.Low, value2: spanLow), Vector3.Max(value1: glyphs.High, value2: spanHigh), ((glyphs.Slot == frame) ? frame : GlyphFramesMixed), (glyphs.Instructions + emitted), MathF.Min(x: glyphs.Detail, y: detail))
                : (spanLow, spanHigh, frame, emitted, detail));
        }

        return this;
    }
}
