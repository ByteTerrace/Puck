using System.Numerics;

namespace Puck.Text;

/// <summary>How <see cref="MathLayout"/> sets lines of math markup.</summary>
/// <param name="MaxLineWidth">The widest a line may run, in layout units; a wider line is set smaller until it fits, to
/// no less than half its size. <see langword="null"/> sets every line at full size.</param>
/// <param name="LineSpacing">The line pitch as a multiple of the font's line height.</param>
/// <param name="FixedPitch">Whether every line stands exactly one pitch below the last whatever it holds, so lines fall
/// on a list's rows; otherwise a line taller than the pitch (a fraction) pushes the next one down.</param>
/// <param name="Highlight">The name of the term drawn in <paramref name="HighlightTint"/>, or <see langword="null"/>.</param>
/// <param name="HighlightTint">The highlighted term's display colour, straight alpha.</param>
public sealed record MathLayoutOptions(
    float? MaxLineWidth = null,
    float LineSpacing = 1f,
    bool FixedPitch = false,
    string? Highlight = null,
    Vector4? HighlightTint = null
);

/// <summary>
/// Sets lines of math markup (<see cref="MathMarkup"/>) as glyph placements in one font atlas: characters at their
/// advances, fractions with their parts a size smaller over a rule on the math axis, roots under a rule, absolute values
/// and norms between bars, scripts raised and lowered a size smaller, and parentheses stretched over what they enclose.
/// A rule or a bar is a glyph too: the interior of the hyphen's or the vertical bar's atlas cell, where the glyph's
/// field changes across the stroke alone, stretched along it, so it draws through the same text tier as every glyph.
/// </summary>
/// <remarks>The result stands its first line's top at zero and runs down (y up, lines at negative y); x runs right from
/// zero. A line that does not parse is set as its plain characters.</remarks>
public sealed class MathLayout {
    private const int Hyphen = '-';
    private const int Bar = '|';
    private const int Radical = 0x221A;
    private const int OpenParenthesis = '(';
    private const int CloseParenthesis = ')';
    // Proportions of the current size: the script and fraction steps, the math axis (where a fraction's rule and a minus
    // sign stand), the strut every character stands on, and the gaps about rules and bars.
    private const float ScriptScale = 0.7f;
    private const float FractionScale = 0.8f;
    private const float SmallestScale = 0.45f;
    private const float AxisHeight = 0.27f;
    private const float StrutDescent = 0.2f;
    private const float RuleGap = 0.1f;
    private const float FractionPad = 0.08f;
    private const float FenceGap = 0.06f;
    private const float RootGap = 0.08f;
    private const float LineGap = 0.15f;
    // The share of a straight glyph's atlas cell, about its middle, a rule samples.
    private const float RuleCrop = 0.1f;

    /// <summary>Sets lines of math markup.</summary>
    /// <param name="atlas">The font atlas the glyphs come from.</param>
    /// <param name="lines">The lines, one line of markup each.</param>
    /// <param name="options">How the lines are set.</param>
    /// <param name="scale">The size of one em, in layout units.</param>
    /// <returns>The placements, the widest line's width and the lines' whole height, in layout units.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scale"/> is not a positive finite number.</exception>
    public TextLayoutResult Layout(FontAtlas atlas, IReadOnlyList<string> lines, MathLayoutOptions options, float scale) {
        ArgumentNullException.ThrowIfNull(argument: atlas);
        ArgumentNullException.ThrowIfNull(argument: lines);
        ArgumentNullException.ThrowIfNull(argument: options);
        if (!float.IsFinite(f: scale) || !(scale > 0f)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(scale), actualValue: scale, message: "The em size must be a positive finite number.");
        }

        var setter = new Setter(atlas: atlas, options: options);
        var placements = new List<TextGlyphPlacement>();
        var pitch = (atlas.Metrics.LineHeight * scale * options.LineSpacing);
        var strutAscent = setter.StrutAscent(size: scale);
        var top = 0f;
        var width = 0f;

        for (var index = 0; (index < lines.Count); index++) {
            var line = (lines[index] ?? string.Empty);
            var row = (MathMarkup.TryParse(error: out _, markup: line, root: out var parsed) ? parsed : Plain(text: line));
            var box = setter.Build(node: row, size: scale, tint: null);

            if ((options.MaxLineWidth is float most) && (most > 0f) && (box.Width > most)) {
                box = setter.Build(node: row, size: (scale * MathF.Max(x: 0.5f, y: (most / box.Width))), tint: null);
            }

            var baseline = (options.FixedPitch
                ? -(strutAscent + (index * pitch))
                : (top - MathF.Max(x: box.Ascent, y: strutAscent)));

            foreach (var item in box.Items) {
                placements.Add(item: item.Placed(atlas: atlas, x: 0f, y: baseline));
            }
            width = MathF.Max(x: width, y: box.Width);
            top = (options.FixedPitch
                ? (baseline - (pitch - strutAscent))
                : MathF.Min(x: (top - pitch), y: (baseline - box.Descent - (LineGap * scale))));
        }

        return new TextLayoutResult(height: -top, placements: placements, width: width);
    }

    private static MathNode.Row Plain(string text) {
        var items = new List<MathNode>();

        foreach (var rune in text.EnumerateRunes()) { items.Add(item: new MathNode.Glyph(Unicode: rune.Value)); }

        return new MathNode.Row(Items: items);
    }

    // One placed glyph of a box: its plane rectangle about the box's origin (its baseline's left end) and the atlas cell
    // it samples, which a rule crops.
    private readonly record struct Item(FontAtlasGlyph Glyph, FontAtlasBounds Plane, FontAtlasBounds Cell, Vector4? Tint) {
        public Item Moved(float x, float y) => this with { Plane = new FontAtlasBounds(Bottom: (Plane.Bottom + y), Left: (Plane.Left + x), Right: (Plane.Right + x), Top: (Plane.Top + y)) };
        public TextGlyphPlacement Placed(FontAtlas atlas, float x, float y) {
            var moved = Moved(x: x, y: y);

            return new TextGlyphPlacement(
                Atlas: atlas,
                AtlasBounds: moved.Cell,
                BaselineOrigin: new Vector2(x: moved.Plane.Left, y: y),
                Effect: ((Tint is { } tint) ? TextEffect.None with { Kind = TextEffectKind.Color, TintColor = tint } : TextEffect.None),
                Glyph: Glyph,
                PlaneBounds: moved.Plane,
                Unicode: Glyph.Unicode
            );
        }
    }
    // A laid-out node: its width, how far it reaches above and below its baseline, and its glyphs about its origin.
    private sealed class Box {
        public float Ascent;
        public float Descent;
        public readonly List<Item> Items = [];
        public int Parenthesis;
        public float Width;

        public void Add(Box child, float x, float y) {
            foreach (var item in child.Items) { Items.Add(item: item.Moved(x: x, y: y)); }
            Ascent = MathF.Max(x: Ascent, y: (child.Ascent + y));
            Descent = MathF.Max(x: Descent, y: (child.Descent - y));
        }
    }

    private sealed class Setter(FontAtlas atlas, MathLayoutOptions options) {
        public float StrutAscent(float size) => ((atlas.Metrics.EffectiveCapHeight + 0.08f) * size);

        public Box Build(MathNode node, float size, Vector4? tint) => node switch {
            MathNode.Row row => Row(row: row, size: size, tint: tint),
            MathNode.Glyph glyph => Character(size: size, tint: tint, unicode: glyph.Unicode),
            MathNode.Space space => new Box { Width = (space.Ems * size) },
            MathNode.Fraction fraction => Fraction(fraction: fraction, size: size, tint: tint),
            MathNode.Root root => Root(root: root, size: size, tint: tint),
            MathNode.Fence fence => Fence(fence: fence, size: size, tint: tint),
            MathNode.Scripts scripts => Scripts(scripts: scripts, size: size, tint: tint),
            MathNode.Term term => Build(node: term.Body, size: size, tint: ((string.Equals(a: term.Name, b: options.Highlight, comparisonType: StringComparison.Ordinal) && (options.HighlightTint is { } highlight)) ? highlight : tint)),
            MathNode.Tint colored => Build(node: colored.Body, size: size, tint: colored.Rgba),
            _ => new Box(),
        };

        private Box Character(int unicode, float size, Vector4? tint) {
            var box = new Box { Ascent = StrutAscent(size: size), Descent = (StrutDescent * size), Parenthesis = unicode switch { OpenParenthesis => 1, CloseParenthesis => -1, _ => 0 } };

            if (!atlas.TryGetGlyph(glyph: out var glyph, unicode: unicode)) {
                box.Width = (0.5f * size);

                return box;
            }

            box.Width = (glyph.Advance * size);
            if ((glyph.PlaneBounds is { } plane) && (glyph.AtlasBounds is { } cell)) {
                box.Items.Add(item: new Item(Cell: cell, Glyph: glyph, Plane: Scaled(bounds: plane, size: size), Tint: tint));
            }

            return box;
        }
        // Lays children side by side, then stretches each matched pair of parentheses over what lies between them.
        private Box Row(MathNode.Row row, float size, Vector4? tint) {
            var children = row.Items.Select(selector: item => Build(node: item, size: size, tint: tint)).ToArray();
            var opened = new Stack<int>();

            for (var index = 0; (index < children.Length); index++) {
                if (children[index].Parenthesis > 0) {
                    opened.Push(item: index);
                } else if ((children[index].Parenthesis < 0) && (opened.Count > 0)) {
                    var open = opened.Pop();
                    var ascent = 0f;
                    var descent = 0f;

                    for (var inner = (open + 1); (inner < index); inner++) {
                        ascent = MathF.Max(x: ascent, y: children[inner].Ascent);
                        descent = MathF.Max(x: descent, y: children[inner].Descent);
                    }
                    if ((ascent > children[open].Ascent) || (descent > children[open].Descent)) {
                        children[open] = Stretched(box: children[open], ascent: ascent, descent: descent);
                        children[index] = Stretched(box: children[index], ascent: ascent, descent: descent);
                    }
                }
            }

            var box = new Box();
            var x = 0f;

            foreach (var child in children) {
                box.Add(child: child, x: x, y: 0f);
                x += child.Width;
            }
            box.Width = x;

            return box;
        }
        private static Box Stretched(Box box, float ascent, float descent) {
            var stretched = new Box { Ascent = MathF.Max(x: box.Ascent, y: ascent), Descent = MathF.Max(x: box.Descent, y: descent), Width = box.Width };

            foreach (var item in box.Items) {
                var height = (item.Plane.Top - item.Plane.Bottom);
                var from = (box.Ascent + box.Descent);
                var to = (stretched.Ascent + stretched.Descent);
                var reach = ((height * to) / MathF.Max(x: from, y: 1e-6f));
                var middle = (0.5f * (stretched.Ascent - stretched.Descent));

                stretched.Items.Add(item: item with { Plane = new FontAtlasBounds(Bottom: (middle - (0.5f * reach)), Left: item.Plane.Left, Right: item.Plane.Right, Top: (middle + (0.5f * reach))) });
            }

            return stretched;
        }
        private Box Fraction(MathNode.Fraction fraction, float size, Vector4? tint) {
            var inner = MathF.Max(x: (size * FractionScale), y: (SmallestScale * size));
            var numerator = Build(node: fraction.Numerator, size: inner, tint: tint);
            var denominator = Build(node: fraction.Denominator, size: inner, tint: tint);
            var pad = (FractionPad * size);
            var width = (MathF.Max(x: numerator.Width, y: denominator.Width) + (2f * pad));
            var axis = (AxisHeight * size);
            var gap = (RuleGap * size);
            var box = new Box { Width = width };

            box.Add(child: numerator, x: (0.5f * (width - numerator.Width)), y: (axis + gap + numerator.Descent));
            box.Add(child: denominator, x: (0.5f * (width - denominator.Width)), y: (axis - gap - denominator.Ascent));
            if (Rule(x0: 0f, x1: width, y: axis, size: size, tint: tint) is { } rule) { box.Items.Add(item: rule); }

            return box;
        }
        private Box Root(MathNode.Root root, float size, Vector4? tint) {
            var radicand = Build(node: root.Radicand, size: size, tint: tint);
            var gap = (RootGap * size);
            var top = (radicand.Ascent + gap);
            var bottom = -radicand.Descent;
            var box = new Box();
            var x = 0f;

            if (atlas.TryGetGlyph(glyph: out var sign, unicode: Radical) && (sign.PlaneBounds is { } plane) && (sign.AtlasBounds is { } cell)) {
                var scaled = Scaled(bounds: plane, size: size);
                var stretch = MathF.Max(x: 1f, y: ((top - bottom) / MathF.Max(x: (scaled.Top - scaled.Bottom), y: 1e-6f)));
                var reach = ((scaled.Top - scaled.Bottom) * stretch);

                box.Items.Add(item: new Item(Cell: cell, Glyph: sign, Plane: new FontAtlasBounds(Bottom: (top - reach), Left: scaled.Left, Right: scaled.Right, Top: top), Tint: tint));
                x = (sign.Advance * size);
            }

            box.Add(child: radicand, x: (x + gap), y: 0f);
            box.Width = (x + radicand.Width + (2f * gap));
            box.Ascent = MathF.Max(x: box.Ascent, y: (top + gap));
            if (Rule(x0: (x - (0.05f * size)), x1: box.Width, y: top, size: size, tint: tint) is { } rule) { box.Items.Add(item: rule); }

            return box;
        }
        private Box Fence(MathNode.Fence fence, float size, Vector4? tint) {
            var body = Build(node: fence.Body, size: size, tint: tint);
            var gap = (FenceGap * size);
            var top = (body.Ascent + gap);
            var bottom = -(body.Descent + gap);
            var box = new Box();
            var x = 0f;

            for (var side = 0; (side < 2); side++) {
                for (var bar = 0; (bar < fence.Bars); bar++) {
                    if (Upright(x: x, y0: bottom, y1: top, size: size, tint: tint, advance: out var advance) is { } item) { box.Items.Add(item: item); }
                    x += (advance * 0.7f);
                }
                if (side == 0) {
                    x += gap;
                    box.Add(child: body, x: x, y: 0f);
                    x += (body.Width + gap);
                }
            }
            box.Width = (x + gap);
            box.Ascent = MathF.Max(x: box.Ascent, y: top);
            box.Descent = MathF.Max(x: box.Descent, y: -bottom);

            return box;
        }
        private Box Scripts(MathNode.Scripts scripts, float size, Vector4? tint) {
            var core = Build(node: scripts.Base, size: size, tint: tint);
            var inner = MathF.Max(x: (size * ScriptScale), y: (SmallestScale * size));
            var box = new Box();
            var reach = 0f;

            box.Add(child: core, x: 0f, y: 0f);
            if (scripts.Superscript is { } superscript) {
                var raised = Build(node: superscript, size: inner, tint: tint);

                box.Add(child: raised, x: core.Width, y: MathF.Max(x: (0.45f * size), y: (core.Ascent - (0.5f * raised.Ascent))));
                reach = MathF.Max(x: reach, y: raised.Width);
            }
            if (scripts.Subscript is { } subscript) {
                var lowered = Build(node: subscript, size: inner, tint: tint);

                box.Add(child: lowered, x: core.Width, y: -MathF.Max(x: (0.18f * size), y: (core.Descent * 0.6f)));
                reach = MathF.Max(x: reach, y: lowered.Width);
            }
            box.Width = (core.Width + reach + (0.04f * size));

            return box;
        }
        // A horizontal rule from x0 to x1 centred at y: the middle of the hyphen's cell, as thick as the hyphen's stroke.
        private Item? Rule(float x0, float x1, float y, float size, Vector4? tint) =>
            (x1 > x0) ? Stroke(along: new Vector2(x: x0, y: x1), across: y, horizontal: true, size: size, tint: tint, unicode: Hyphen, advance: out _) : null;
        // A vertical bar at x from y0 to y1: the middle of the vertical bar's cell, as wide as its stroke.
        private Item? Upright(float x, float y0, float y1, float size, Vector4? tint, out float advance) =>
            Stroke(along: new Vector2(x: y0, y: y1), across: x, horizontal: false, size: size, tint: tint, unicode: Bar, advance: out advance);
        // A straight glyph's stroke stretched from along.X to along.Y: the glyph's cell cropped to its middle along the
        // stroke, where its field changes across the stroke alone, placed with its own plane extent across the stroke
        // (centred on `across` for a horizontal stroke, from `across` as its origin for a vertical one).
        private Item? Stroke(Vector2 along, float across, bool horizontal, float size, Vector4? tint, int unicode, out float advance) {
            advance = (0.25f * size);
            if (!atlas.TryGetGlyph(glyph: out var glyph, unicode: unicode) || (glyph.PlaneBounds is not { } plane) || (glyph.AtlasBounds is not { } cell)) {
                return null;
            }

            advance = (glyph.Advance * size);
            if (horizontal) {
                var half = (0.5f * (plane.Top - plane.Bottom) * size);
                var middle = (0.5f * (cell.Left + cell.Right));
                var crop = (0.5f * RuleCrop * (cell.Right - cell.Left));

                return new Item(
                    Cell: new FontAtlasBounds(Bottom: cell.Bottom, Left: (middle - crop), Right: (middle + crop), Top: cell.Top),
                    Glyph: glyph,
                    Plane: new FontAtlasBounds(Bottom: (across - half), Left: along.X, Right: along.Y, Top: (across + half)),
                    Tint: tint
                );
            } else {
                var middle = (0.5f * (cell.Bottom + cell.Top));
                var crop = (0.5f * RuleCrop * (cell.Top - cell.Bottom));

                return new Item(
                    Cell: new FontAtlasBounds(Bottom: (middle - crop), Left: cell.Left, Right: cell.Right, Top: (middle + crop)),
                    Glyph: glyph,
                    Plane: new FontAtlasBounds(Bottom: along.X, Left: (across + (plane.Left * size)), Right: (across + (plane.Right * size)), Top: along.Y),
                    Tint: tint
                );
            }
        }
        private static FontAtlasBounds Scaled(FontAtlasBounds bounds, float size) =>
            new(Bottom: (bounds.Bottom * size), Left: (bounds.Left * size), Right: (bounds.Right * size), Top: (bounds.Top * size));
    }
}
