namespace Puck.Overlays;

/// <summary>One annotation outline: an ellipse in normalized frame space (origin top-left, Y down), so the same numbers
/// place it on any extent the overlay draws at.</summary>
/// <param name="CenterX">The center x, as a fraction of the frame width.</param>
/// <param name="CenterY">The center y, as a fraction of the frame height.</param>
/// <param name="RadiusX">The horizontal radius, as a fraction of the frame width.</param>
/// <param name="RadiusY">The vertical radius, as a fraction of the frame height.</param>
public readonly record struct OverlayAnnotationCircle(
    float CenterX,
    float CenterY,
    float RadiusX,
    float RadiusY
);
/// <summary>One frame's annotations: the kept outlines in the order they were drawn, the outline a drag in progress
/// would keep, and whether annotate mode is on (which shows the mode banner).</summary>
/// <param name="Circles">The kept outlines, oldest first; each is labelled with its one-based index.</param>
/// <param name="Drag">The outline the drag in progress would keep, or <see langword="null"/> when none is.</param>
/// <param name="Active">Whether annotate mode is on.</param>
public readonly record struct OverlayAnnotationFrame(
    ReadOnlyMemory<OverlayAnnotationCircle> Circles,
    OverlayAnnotationCircle? Drag,
    bool Active
);
/// <summary>The read seam <see cref="AnnotationWriter"/> consumes; the host's annotation state is the writer.</summary>
public interface IAnnotationSource {
    /// <summary>Copies the current annotations. The returned memory stays valid until the next call.</summary>
    /// <param name="frame">The annotations to draw.</param>
    /// <returns><see langword="true"/> when there is anything to draw.</returns>
    bool TrySnapshot(out OverlayAnnotationFrame frame);
}
/// <summary>
/// Draws the annotation outlines a person circles on screen to point something out: each kept outline as a magenta
/// stroke over a dark halo, so it reads on any scene, with its one-based index beside it; the outline a drag in
/// progress would keep; and, while annotate mode is on, a one-line banner. Unclipped and outside the replace-band
/// suppression, so an outline can circle any part of the frame, a HUD panel included. A presentation instrument like
/// the inspector, so its look is the writer's own.
/// </summary>
/// <param name="source">The annotation state.</param>
/// <param name="theme">The live overlay theme (the label and banner type size).</param>
public sealed class AnnotationWriter(IAnnotationSource source, OverlayThemeStore theme) {
    /// <summary>The most outlines one frame keeps; the host refuses another past it.</summary>
    public const int MaxCircles = 16;
    /// <summary>The characters of one outline's index label.</summary>
    public const int LabelChars = 2;
    /// <summary>The banner's character clamp.</summary>
    public const int BannerChars = 56;
    /// <summary>The element records the channel may write: two strokes and a label per kept outline, two strokes for
    /// the drag in progress, and the banner's plate and text.</summary>
    public const int MaxElements = ((MaxCircles * 3) + 2 + 2);
    /// <summary>The glyph-code words the channel may write: every label and the banner.</summary>
    public const int MaxTextWords = ((MaxCircles * LabelChars) + BannerChars);

    private const string Banner = "ANNOTATE  drag: circle  click: dot  right-click: clear";
    // The stroke: a 2.5 px magenta line centred on a 5 px dark halo.
    private const float HaloAlpha = 0.6f;
    private const float HaloHalfWidth = 2.5f;
    private const float StrokeHalfWidth = 1.25f;
    private static readonly RgbaColor Halo = new(R: 0.02f, G: 0f, B: 0.02f, A: 1f);
    private static readonly RgbaColor Stroke = new(R: 1f, G: 0.1f, B: 0.85f, A: 1f);

    private readonly char[] m_label = new char[LabelChars];

    private static void WriteOutline(OverlayFrameBuilder builder, in OverlayAnnotationCircle circle, float alpha) {
        var centerX = (circle.CenterX * builder.Width);
        var centerY = (circle.CenterY * builder.Height);
        var radiusX = MathF.Max(x: (circle.RadiusX * builder.Width), y: 1f);
        var radiusY = MathF.Max(x: (circle.RadiusY * builder.Height), y: 1f);

        builder.WriteEllipse(
            alpha: (HaloAlpha * alpha),
            centerX: centerX,
            centerY: centerY,
            color: Halo,
            halfWidth: HaloHalfWidth,
            radiusX: radiusX,
            radiusY: radiusY
        );
        builder.WriteEllipse(
            alpha: alpha,
            centerX: centerX,
            centerY: centerY,
            color: Stroke,
            halfWidth: StrokeHalfWidth,
            radiusX: radiusX,
            radiusY: radiusY
        );
    }

    /// <summary>Emits this frame's annotation records.</summary>
    /// <param name="builder">The frame builder, inside the <see cref="OverlayChannel.Annotation"/> scope.</param>
    public void Emit(OverlayFrameBuilder builder) {
        ArgumentNullException.ThrowIfNull(argument: builder);

        if (!source.TrySnapshot(frame: out var frame)) {
            return;
        }

        var circles = frame.Circles.Span;
        var count = Math.Min(val1: circles.Length, val2: MaxCircles);
        var cellHeight = OverlayFrameBuilder.CellHeight(sizePx: theme.Current.Type.MicroSize);

        if (count < circles.Length) {
            builder.NoteRefused(
                elements: ((circles.Length - count) * 3),
                textWords: ((circles.Length - count) * LabelChars)
            );
        }

        for (var index = 0; (index < count); index++) {
            ref readonly var circle = ref circles[index];

            WriteOutline(alpha: 1f, builder: builder, circle: in circle);

            // The index sits just outside the outline's upper-left, where list numbers it.
            var number = (index + 1);
            var length = ((number < 10) ? 1 : 2);

            if (length == 1) {
                m_label[0] = ((char)('0' + number));
            } else {
                m_label[0] = ((char)('0' + (number / 10)));
                m_label[1] = ((char)('0' + (number % 10)));
            }

            var labelX = (((circle.CenterX - (circle.RadiusX * 0.7071f)) * builder.Width) - builder.TextWidth(chars: length, cellHeight: cellHeight) - 4f);
            var labelY = (((circle.CenterY - (circle.RadiusY * 0.7071f)) * builder.Height) - cellHeight);

            builder.WriteText(
                alpha: 1f,
                cellHeight: cellHeight,
                maxChars: LabelChars,
                role: OverlayColorRole.TextPrimary,
                text: m_label.AsSpan(start: 0, length: length),
                x: MathF.Max(x: labelX, y: 0f),
                y: MathF.Max(x: labelY, y: 0f)
            );
        }

        if (frame.Drag is { } drag) {
            WriteOutline(alpha: 0.6f, builder: builder, circle: in drag);
        }

        if (frame.Active) {
            var bannerWidth = builder.TextWidth(chars: Banner.Length, cellHeight: cellHeight);
            var x = MathF.Max(x: ((builder.Width - bannerWidth) * 0.5f), y: 0f);
            const float Y = 8f;

            builder.WriteRect(
                alpha: 0.85f,
                h: (cellHeight + 8f),
                radius: 4f,
                role: OverlayColorRole.SurfaceRaised,
                w: (bannerWidth + 16f),
                x: (x - 8f),
                y: (Y - 4f)
            );
            builder.WriteText(
                alpha: 1f,
                cellHeight: cellHeight,
                maxChars: BannerChars,
                role: OverlayColorRole.Danger,
                text: Banner,
                x: x,
                y: Y
            );
        }
    }
}
