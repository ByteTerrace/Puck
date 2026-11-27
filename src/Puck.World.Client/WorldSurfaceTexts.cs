using System.Globalization;
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
/// catalog (<see cref="TextLayout"/>, or <see cref="MathLayout"/> for a math text) and, for marked-up text, moved,
/// sized, faded and tinted a glyph at a time (<see cref="TextGlyphChannel"/>).
/// <para>A slot's layout is kept while the text it resolves to, the document and the catalog hold, so a frame on which
/// no placeholder's cell moved lays out nothing; a text whose markup animates packs its quads again each frame from the
/// kept layout, and a math text whose scroll moved packs again from its kept layout. <see cref="Invalidate"/> lays
/// every slot out again.</para>
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
    private readonly MathLayout m_mathLayout = new();

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
    /// <summary>Describes every math text slot as the latest frame laid it out: its slot, the formulas it brings in, the
    /// term it highlights, the live values it shows, its scroll (the share asked and the offset shown, in the paint
    /// frame's units, against the most it can scroll), its lines and its glyphs.</summary>
    /// <returns>One line a math text slot, in slot order; none while no math text has been laid out.</returns>
    public IReadOnlyList<string> DescribeMath() {
        var lines = new List<string>();

        for (var slot = 0; (slot < m_slots.Length); slot++) {
            ref var held = ref m_slots[slot];

            if (held.Layout.Expansion is not { } expansion) { continue; }

            var overflow = MathF.Max(x: 0f, y: (held.Layout.Layout.Height - (2f * held.Layout.Half.Y)));

            lines.Add(item: string.Create(provider: CultureInfo.InvariantCulture, handler: $"slot={slot} formulas=[{string.Join(separator: ",", values: expansion.Formulas)}] highlight={expansion.Highlight ?? "none"} values=[{string.Join(separator: ",", values: expansion.Values.Select(selector: static value => $"{value.Key}={value.Value}"))}] scroll={held.Scroll:0.###} shown={held.Offset:0.####}/{overflow:0.####} lines={expansion.Lines.Count} height={held.Layout.Layout.Height:0.####} box={(2f * held.Layout.Half.Y):0.####} glyphs={held.Frame?.Glyphs.Length / SdfSurfaceTextLayout.WordsPerGlyph ?? 0}"));
        }

        return lines;
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
                var authored = ((index < manifest.SurfaceTexts.Length) ? manifest.SurfaceTexts[index].Text : null);

                m_slots[index].Text = ((authored is null) ? null : m_reads.ResolveText(line: (authored.Template ?? string.Empty)));
                m_slots[index].Scroll = ((authored?.Scroll is { } scroll) ? ScrollShare(text: m_reads.ResolveText(line: scroll)) : 0f);
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
            held.Layout = default;

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

        // A math text scrolls its share of the overflow past the box up out of it; the box's top edge clips it.
        var offset = (held.Layout.Expansion is null
            ? 0f
            : (held.Scroll * MathF.Max(x: 0f, y: (held.Layout.Layout.Height - (2f * held.Layout.Half.Y)))));

        if ((held.Frame is null) || held.Layout.Moves || (offset != held.Offset)) {
            held.Offset = offset;
            held.Frame = Pack(atlas: catalog.Resolve(name: paint.Text!.Font), layout: in held.Layout, offset: offset, text: paint.Text);
        }

        return held.Frame;
    }
    private static float ScrollShare(string text) => ((float.TryParse(s: text, style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out var share) && float.IsFinite(f: share))
        ? Math.Clamp(value: share, min: 0f, max: 1f)
        : 0f);
    // Lays a paint's resolved text out in its box: the anchored line (or, with no anchor, the whole block) on the box's
    // middle, placed across its width by the text's alignment, and shrunk to the box when the text asks to fit; a math
    // text from the box's top-left corner. Every length is in the paint frame's units, about the box's centre.
    private Laid Lay(PalettePaintDocument paint, string text, FontAtlas atlas) {
        var authored = paint.Text!;
        var bound = paint.ToPaint(resolve: static _ => Vector3.Zero).Shape.Bound;
        var half = new Vector2(x: bound.HalfExtents.X, y: bound.HalfExtents.Y);

        if (authored.Math) {
            var expansion = WorldMathText.Expand(text: text);
            var mathLayout = m_mathLayout.Layout(
                atlas: atlas,
                lines: expansion.Lines,
                options: new MathLayoutOptions(
                    FixedPitch: authored.Pitch,
                    Highlight: expansion.Highlight,
                    HighlightTint: expansion.HighlightTint,
                    LineSpacing: (authored.LineSpacing ?? 1f),
                    MaxLineWidth: (2f * half.X)
                ),
                scale: authored.EmHeight
            );

            return new Laid(Expansion: expansion, Fit: 1f, Half: half, Layout: mathLayout, Moves: false, Origin: new Vector2(x: -half.X, y: half.Y), Spans: Spans(layout: mathLayout, moves: out _));
        }

        var options = authored.LayoutOptions(boxWidth: (2f * half.X));
        var layout = (authored.Markup
            ? m_layout.Layout(atlas: atlas, options: options, runes: BbCodeTextMarkup.EnrichRunes(markup: text), scale: authored.EmHeight)
            : m_layout.Layout(atlas: atlas, options: options, scale: authored.EmHeight, text: text));
        var fit = 1f;

        if (authored.Fit && (layout.Width > 0f) && (layout.Height > 0f)) {
            fit = MathF.Min(x: 1f, y: MathF.Min(x: ((2f * half.X) / layout.Width), y: ((2f * half.Y) / layout.Height)));
        }

        // The layout's x origin is its alignment's anchor: a left-aligned line starts at it, a centred line is centred
        // on it and a right-aligned line ends at it (TextLayout), so the anchor goes to the box's matching edge or middle.
        var anchor = (authored.Alignment switch {
            TextAlignment.Left => -half.X,
            TextAlignment.Right => half.X,
            _ => 0f,
        });
        // An anchored layout already stands its anchor line at zero (TextLayout). Otherwise the first baseline stands at
        // zero and the block runs from the ascender above it down its whole height.
        var top = (atlas.Metrics.Ascender * authored.EmHeight);
        var lift = ((authored.Anchor is null) ? -((top - (0.5f * layout.Height)) * fit) : 0f);
        var spans = Spans(layout: layout, moves: out var moves);

        return new Laid(Expansion: null, Fit: fit, Half: half, Layout: layout, Moves: moves, Origin: new Vector2(x: anchor, y: lift), Spans: spans);
    }
    // Each glyph's ordinal within its run of one effect, and whether any effect moves.
    private static int[] Spans(TextLayoutResult layout, out bool moves) {
        var spans = new int[layout.Placements.Count];

        moves = false;
        for (var index = 0; (index < spans.Length); index++) {
            var effect = layout.Placements[index].Effect;

            spans[index] = (((index > 0) && (effect == layout.Placements[(index - 1)].Effect)) ? (spans[(index - 1)] + 1) : 0);
            moves |= effect.IsMotion;
        }

        return spans;
    }
    // Packs a laid-out text's glyph quads (SdfSurfaceTextLayout): each glyph's plane rectangle, raised by the scroll
    // offset, then its effect's channel at the current content tick. A glyph wholly outside the box draws nothing and
    // packs nothing.
    private SdfSurfaceTextFrame? Pack(PalettePaintTextDocument text, in Laid layout, FontAtlas atlas, float offset) {
        var placements = layout.Layout.Placements;

        if ((placements.Count == 0) || !(layout.Half.X > 0f) || !(layout.Half.Y > 0f)) {
            return null;
        }

        var words = new List<uint>(capacity: (Math.Min(val1: placements.Count, val2: SdfSurfaceTextLayout.MaxGlyphs) * SdfSurfaceTextLayout.WordsPerGlyph));
        var effectUnit = ((text.EffectUnit ?? (text.EmHeight * DefaultEffectUnitEms)) * layout.Fit);
        var tick = ((long)(m_seconds * TicksPerSecond));
        var origin = (layout.Origin + new Vector2(x: 0f, y: offset));

        for (var index = 0; ((index < placements.Count) && (words.Count < (SdfSurfaceTextLayout.MaxGlyphs * SdfSurfaceTextLayout.WordsPerGlyph))); index++) {
            var placement = placements[index];
            var channel = TextGlyphChannel.Resolve(
                contentTick: tick,
                effect: placement.Effect,
                glyphIndex: layout.Spans[index],
                glyphPhase: placement.BaselineOrigin.X,
                ticksPerSecond: TicksPerSecond
            );
            var plane = placement.PlaneBounds;
            var centre = ((new Vector2(x: (0.5f * (plane.Left + plane.Right)), y: (0.5f * (plane.Bottom + plane.Top))) * layout.Fit) + origin + (channel.Offset * effectUnit));
            var reach = (new Vector2(x: MathF.Abs(x: (plane.Right - plane.Left)), y: MathF.Abs(x: (plane.Top - plane.Bottom))) * (0.5f * layout.Fit * MathF.Max(x: channel.Scale, y: 0f)));
            var low = ((centre - reach) / layout.Half);
            var high = ((centre + reach) / layout.Half);

            if ((high.X < -1f) || (low.X > 1f) || (high.Y < -1f) || (low.Y > 1f)) {
                continue;
            }

            var (topLeft, bottomRight) = WorldScreenTextDecal.PackCell(atlas: atlas, bounds: placement.AtlasBounds);
            var ink = (channel.HasTint
                ? new DisplayColor(Rgba: channel.Tint).ToScene().Linear
                : Vector3.One);

            words.Add(item: BitConverter.SingleToUInt32Bits(value: low.X));
            words.Add(item: BitConverter.SingleToUInt32Bits(value: low.Y));
            words.Add(item: BitConverter.SingleToUInt32Bits(value: high.X));
            words.Add(item: BitConverter.SingleToUInt32Bits(value: high.Y));
            words.Add(item: topLeft);
            words.Add(item: bottomRight);
            words.Add(item: VectorFunctions.PackRgba8(rgba: new Vector4(value: ink, w: (Math.Clamp(value: channel.Coverage, min: 0f, max: 1f) * (channel.HasTint ? Math.Clamp(value: channel.Tint.W, min: 0f, max: 1f) : 1f)))));
            words.Add(item: (channel.HasTint ? SdfSurfaceTextLayout.TintFlag : 0u));
        }

        return ((words.Count == 0) ? null : new SdfSurfaceTextFrame(DistanceRange: atlas.DistanceRange, Glyphs: words.ToArray()));
    }

    // A slot's kept work: the text its template last resolved to, its scroll share, the layout of that text, the scroll
    // offset it was last packed at, and the frame packed from it.
    private struct Slot {
        public PackedFontAtlasCatalog? Catalog;
        public SdfSurfaceTextFrame? Frame;
        public string? Laid;
        public Laid Layout;
        public float Offset;
        public float Scroll;
        public string? Text;
    }
    // A text laid out in its paint's box: the layout in the paint frame's units before the fit, the box's half-extents,
    // where the fitted layout's origin (its alignment's anchor on the first baseline, or a math text's top-left corner)
    // stands about the box's centre, each glyph's ordinal within its effect's span, whether any effect moves, and a math
    // text's expansion (null for any other text).
    private readonly record struct Laid(TextLayoutResult Layout, Vector2 Half, Vector2 Origin, float Fit, int[] Spans, bool Moves, WorldMathTextExpansion? Expansion);
}
