using System.Numerics;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.Text;
using Puck.World.Authoring;

namespace Puck.World.Client;

/// <summary>
/// The glyphs one world's text paints draw (<see cref="PalettePaintDocument.Text"/>): for every surface text slot of
/// the document the mirror holds (<see cref="WorldPresentationManifest.SurfaceTexts"/>), the quads of the paint's
/// template with its placeholders read from the mirror, laid out in the paint's box through the world's own font
/// catalog (<see cref="TextLayout"/>) and, for marked-up text, moved, sized, faded and tinted a glyph at a time
/// (<see cref="TextGlyphChannel"/>).
/// <para>A slot's layout is kept while the text it resolves to, the document and the catalog hold, so a frame on which
/// no placeholder's cell moved lays out nothing; a text whose markup animates packs its quads again each frame from the
/// kept layout. <see cref="Invalidate"/> lays every slot out again.</para>
/// </summary>
/// <remarks>Presentation only, used on the thread that presents frames. The surface text tier draws a glyph's offset,
/// scale, coverage and tint; it has no weight, so a <c>weight</c> effect draws as plain text.</remarks>
public sealed class WorldSurfaceTexts {
    // The content clock's resolution: a text effect's motion is a function of this tick alone.
    private const float TicksPerSecond = 1000f;
    // An em's share one unit of an effect's amplitude moves a glyph by, for a text that authors no effectUnit.
    private const float DefaultEffectUnitEms = (1f / 32f);

    private readonly Func<PackedFontAtlasCatalog?> m_catalog;
    private readonly WorldBakedColors m_reads;
    private readonly WorldStateMirror m_mirror;
    private readonly Slot[] m_slots = new Slot[SdfSurfaceTextLayout.MaxTexts];
    private readonly TextLayout m_layout = new();

    private WorldPresentationManifest? m_manifest;
    private bool m_resolved;
    private double m_seconds;

    /// <summary>Initializes a new instance of the <see cref="WorldSurfaceTexts"/> class.</summary>
    /// <param name="mirror">The mirror every placeholder reads through, whose installed document's manifest names the
    /// slots.</param>
    /// <param name="catalog">The world's resolved font catalog, or <see langword="null"/> while it has none.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldSurfaceTexts(WorldStateMirror mirror, Func<PackedFontAtlasCatalog?> catalog) {
        ArgumentNullException.ThrowIfNull(argument: mirror);
        ArgumentNullException.ThrowIfNull(argument: catalog);

        m_mirror = mirror;
        m_catalog = catalog;
        m_reads = new WorldBakedColors(mirror: mirror);
        Provider = Resolve;
    }

    /// <summary>Gets what answers a surface text slot's glyphs for the frame being produced
    /// (<see cref="ISdfFrameSource.SurfaceTexts"/>): <see langword="null"/> for a slot no text paint holds, a world with
    /// no font catalog, or a text that resolves to no glyph.</summary>
    public Func<int, SdfSurfaceTextFrame?> Provider { get; }

    /// <summary>Sets the presentation time the next frame's text effects are read at.</summary>
    /// <param name="seconds">The presentation clock, in seconds.</param>
    public void Advance(double seconds) => m_seconds = seconds;
    /// <summary>Lays every slot out again at its next frame: a delivery may have changed a paint or the fonts.</summary>
    public void Invalidate() {
        Array.Clear(array: m_slots);
        m_resolved = false;
    }

    private SdfSurfaceTextFrame? Resolve(int slot) {
        var manifest = m_mirror.Manifest;

        if (!ReferenceEquals(objA: manifest, objB: m_manifest)) {
            m_manifest = manifest;
            Invalidate();
        }
        if (m_resolved && m_reads.TryTakeMove()) {
            m_resolved = false;
        }
        // The first slot asked after a move resolves every template again, so the reads recorded are the whole set.
        if (!m_resolved) {
            m_reads.Begin();
            for (var index = 0; (index < m_slots.Length); index++) {
                m_slots[index].Text = ((index < manifest.SurfaceTexts.Length)
                    ? m_reads.ResolveText(line: (manifest.SurfaceTexts[index].Text!.Template ?? string.Empty))
                    : null);
            }
            m_resolved = true;
        }

        ref var held = ref m_slots[slot];

        if (
            (held.Text is not { Length: > 0 } text) ||
            (m_catalog() is not { } catalog)
        ) {
            held.Frame = null;
            held.Laid = null;

            return null;
        }

        var paint = manifest.SurfaceTexts[slot];

        if (
            !string.Equals(a: held.Laid, b: text, comparisonType: StringComparison.Ordinal) ||
            !ReferenceEquals(objA: held.Catalog, objB: catalog)
        ) {
            held.Laid = text;
            held.Catalog = catalog;
            held.Layout = Lay(atlas: catalog.Resolve(name: paint.Text!.Font), paint: paint, text: text);
            held.Frame = null;
        }
        if ((held.Frame is null) || held.Layout.Moves) {
            held.Frame = Pack(atlas: catalog.Resolve(name: paint.Text!.Font), layout: in held.Layout, text: paint.Text);
        }

        return held.Frame;
    }
    // Lays a paint's resolved text out in its box: the block centred on the box's height, placed across its width by the
    // text's alignment, and shrunk to the box when the text asks to fit. Every length is in the paint frame's units,
    // about the box's centre.
    private Laid Lay(PalettePaintDocument paint, string text, FontAtlas atlas) {
        var authored = paint.Text!;
        var bound = paint.ToPaint(resolve: static _ => Vector3.Zero).Shape.Bound;
        var half = new Vector2(x: bound.HalfExtents.X, y: bound.HalfExtents.Y);
        var options = authored.LayoutOptions(boxWidth: (2f * half.X));
        var layout = (authored.Markup
            ? m_layout.Layout(atlas: atlas, options: options, runes: BbCodeTextMarkup.EnrichRunes(markup: text), scale: authored.EmHeight)
            : m_layout.Layout(atlas: atlas, options: options, scale: authored.EmHeight, text: text));
        var fit = 1f;

        if (authored.Fit && (layout.Width > 0f) && (layout.Height > 0f)) {
            fit = MathF.Min(x: 1f, y: MathF.Min(x: ((2f * half.X) / layout.Width), y: ((2f * half.Y) / layout.Height)));
        }

        var width = (layout.Width * fit);
        var left = (authored.Alignment switch {
            TextAlignment.Left => -half.X,
            TextAlignment.Right => (half.X - width),
            _ => (-0.5f * width),
        });
        // The first baseline stands at zero and the block runs from the ascender above it down its whole height.
        var top = (atlas.Metrics.Ascender * authored.EmHeight);
        var lift = -((top - (0.5f * layout.Height)) * fit);
        var moves = false;
        var spans = new int[layout.Placements.Count];

        for (var index = 0; (index < spans.Length); index++) {
            var effect = layout.Placements[index].Effect;

            spans[index] = (((index > 0) && (effect == layout.Placements[(index - 1)].Effect)) ? (spans[(index - 1)] + 1) : 0);
            moves |= effect.IsMotion;
        }

        return new Laid(Fit: fit, Half: half, Layout: layout, Moves: moves, Origin: new Vector2(x: left, y: lift), Spans: spans);
    }
    // Packs a laid-out text's glyph quads (SdfSurfaceTextLayout): each glyph's atlas cell about its laid-out centre,
    // then its effect's channel at the current content tick.
    private SdfSurfaceTextFrame? Pack(PalettePaintTextDocument text, in Laid layout, FontAtlas atlas) {
        var placements = layout.Layout.Placements;
        var count = Math.Min(val1: placements.Count, val2: SdfSurfaceTextLayout.MaxGlyphs);

        if ((count == 0) || !(layout.Half.X > 0f) || !(layout.Half.Y > 0f)) {
            return null;
        }

        var words = new uint[(count * SdfSurfaceTextLayout.WordsPerGlyph)];
        var unitPerTexel = ((text.EmHeight / atlas.Size) * layout.Fit);
        var effectUnit = ((text.EffectUnit ?? (text.EmHeight * DefaultEffectUnitEms)) * layout.Fit);
        var tick = ((long)(m_seconds * TicksPerSecond));

        for (var index = 0; (index < count); index++) {
            var placement = placements[index];
            var channel = TextGlyphChannel.Resolve(
                contentTick: tick,
                effect: placement.Effect,
                glyphIndex: layout.Spans[index],
                glyphPhase: placement.BaselineOrigin.X,
                ticksPerSecond: TicksPerSecond
            );
            var plane = placement.PlaneBounds;
            var cell = placement.AtlasBounds;
            var centre = ((new Vector2(x: (0.5f * (plane.Left + plane.Right)), y: (0.5f * (plane.Bottom + plane.Top))) * layout.Fit) + layout.Origin + (channel.Offset * effectUnit));
            var reach = (new Vector2(x: MathF.Abs(x: (cell.Right - cell.Left)), y: MathF.Abs(x: (cell.Bottom - cell.Top))) * (0.5f * unitPerTexel * MathF.Max(x: channel.Scale, y: 0f)));
            var low = ((centre - reach) / layout.Half);
            var high = ((centre + reach) / layout.Half);
            var (topLeft, bottomRight) = WorldScreenTextDecal.PackCell(atlas: atlas, bounds: cell);
            var ink = (channel.HasTint
                ? new DisplayColor(Rgba: channel.Tint).ToScene().Linear
                : Vector3.One);
            var at = (index * SdfSurfaceTextLayout.WordsPerGlyph);

            words[(at + 0)] = BitConverter.SingleToUInt32Bits(value: low.X);
            words[(at + 1)] = BitConverter.SingleToUInt32Bits(value: low.Y);
            words[(at + 2)] = BitConverter.SingleToUInt32Bits(value: high.X);
            words[(at + 3)] = BitConverter.SingleToUInt32Bits(value: high.Y);
            words[(at + 4)] = topLeft;
            words[(at + 5)] = bottomRight;
            words[(at + 6)] = VectorFunctions.PackRgba8(rgba: new Vector4(value: ink, w: (Math.Clamp(value: channel.Coverage, min: 0f, max: 1f) * (channel.HasTint ? Math.Clamp(value: channel.Tint.W, min: 0f, max: 1f) : 1f))));
            words[(at + 7)] = (channel.HasTint ? SdfSurfaceTextLayout.TintFlag : 0u);
        }

        return new SdfSurfaceTextFrame(DistanceRange: atlas.DistanceRange, Glyphs: words);
    }

    // A slot's kept work: the text its template last resolved to, the layout of that text, and the frame packed from it.
    private struct Slot {
        public PackedFontAtlasCatalog? Catalog;
        public SdfSurfaceTextFrame? Frame;
        public string? Laid;
        public Laid Layout;
        public string? Text;
    }
    // A text laid out in its paint's box: the layout in the paint frame's units before the fit, the box's half-extents,
    // the fitted block's origin (its first baseline's left end) about the box's centre, each glyph's ordinal within its
    // effect's span, and whether any effect moves.
    private readonly record struct Laid(TextLayoutResult Layout, Vector2 Half, Vector2 Origin, float Fit, int[] Spans, bool Moves);
}
