using Puck.Hosting;

namespace Puck.Overlays;

/// <summary>
/// The toast writer: renders the latest <see cref="IOverlayToastSource"/> echo as a transient Tier-1 chip
/// (mid-right) — scrim-chip fill, bloom ring + halo and a 2px state rail in the [OK]/[ERR] hue, an inset icon
/// square, and the message WORD-WRAPPED across up to <see cref="MaxMessageLines"/> lines, the chip growing
/// vertically to hold them. Expires on the CONTENT tick (~3 seconds equivalent, never wall clock) with a trailing
/// <c>dur.med</c> opacity-only fade (text never translates — motion stays calm per the token spec). Pure record
/// emission; no GPU types.
/// </summary>
/// <remarks>A rejection's REASON is the actionable half of the message, so the chip wraps rather than clipping at
/// one line. Past <see cref="MaxMessageChars"/> × <see cref="MaxMessageLines"/> the tail drops — the whole text
/// always survives on stderr and in the console panel.</remarks>
public sealed class ToastWriter {
    /// <summary>The characters one wrapped line holds.</summary>
    public const int MaxMessageChars = 44;
    /// <summary>The wrapped lines the chip grows to.</summary>
    public const int MaxMessageLines = 4;
    // Lifetime and fade in DETERMINISTIC engine ticks (content tick, never wall clock).
    private static readonly ulong DurationTicks = (3UL * EngineTicks.PerSecond);
    private static readonly ulong FadeTicks = (ulong)((DesignTokens.Motion.DurMed / 1000f) * EngineTicks.PerSecond);

    private readonly IOverlayToastSource m_source;
    private ulong m_firstTicks;
    private int m_sequenceSeen;

    /// <summary>Initializes a new instance of the <see cref="ToastWriter"/> class.</summary>
    /// <param name="source">The toast snapshot source.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public ToastWriter(IOverlayToastSource source) {
        ArgumentNullException.ThrowIfNull(argument: source);

        m_source = source;
    }

    /// <summary>Emits this frame's toast, when one is live.</summary>
    /// <param name="builder">The frame builder.</param>
    /// <param name="renderTicks">The frame's continuous content clock (<c>FrameContext.RenderTicks</c>).</param>
    public void Emit(OverlayFrameBuilder builder, ulong renderTicks) {
        ArgumentNullException.ThrowIfNull(argument: builder);

        if (!m_source.TrySnapshot(frame: out var toast) || (toast.Message.Length == 0)) {
            return;
        }

        if (toast.Sequence != m_sequenceSeen) {
            m_sequenceSeen = toast.Sequence;
            m_firstTicks = renderTicks;
        }

        var age = (renderTicks - m_firstTicks);

        if (age >= DurationTicks) {
            return;
        }

        // Opacity-only exit: full until the trailing dur.med window, then a linear fade.
        var remaining = (DurationTicks - age);
        var alpha = ((remaining >= FadeTicks) ? 1f : ((float)remaining / FadeTicks));
        var stateRole = (toast.IsError ? OverlayColorRole.Danger : OverlayColorRole.Positive);
        var monoCell = OverlayFrameBuilder.CellHeight(sizePx: DesignTokens.Type.TypeMonoSize);
        var microCell = OverlayFrameBuilder.CellHeight(sizePx: DesignTokens.Type.TypeMicroSize);
        var message = toast.Message.AsSpan(start: 0, length: FirstLineLength(text: toast.Message));
        Span<Range> wrapped = stackalloc Range[MaxMessageLines];
        var lineCount = Wrap(text: message, lines: wrapped);
        var messageChars = 0;

        for (var index = 0; (index < lineCount); index++) {
            var (_, length) = wrapped[index].GetOffsetAndLength(length: message.Length);

            messageChars = Math.Max(val1: messageChars, val2: length);
        }

        var icon = DesignTokens.Space.HeightBadge;
        var textHeight = (lineCount * monoCell);
        var panelHeight = MathF.Max(x: DesignTokens.Space.HeightChip, y: (textHeight + (2f * DesignTokens.Space.Space2)));
        var panelWidth = ((((DesignTokens.Space.Space3 + icon) + DesignTokens.Space.Space2) + builder.TextWidth(chars: messageChars, cellHeight: monoCell)) + DesignTokens.Space.Space3);
        var x = ((builder.Width - panelWidth) - DesignTokens.Space.Space8);
        var y = ((builder.Height * 0.5f) - (panelHeight * 0.5f));

        builder.WritePanel(alpha: alpha, bandHeight: 0f, h: panelHeight, ringRole: stateRole, style: OverlayPanelStyle.Chip, titleBand: false, w: panelWidth, x: x, y: y);
        // The 2px state rail hugging the left edge, inset past the corner radius (the edge-width law's third
        // sanctioned 2px signal).
        builder.WriteRect(
            alpha: alpha,
            h: (panelHeight - (2f * DesignTokens.Radius.Radius2)),
            radius: 0f,
            role: stateRole,
            w: DesignTokens.Elevation.RingStatusWidth,
            x: x,
            y: (y + DesignTokens.Radius.Radius2)
        );

        var iconX = (x + DesignTokens.Space.Space3);
        var iconY = (y + ((panelHeight - icon) * 0.5f));

        builder.WriteRect(alpha: alpha, h: icon, radius: DesignTokens.Radius.Radius1, role: OverlayColorRole.SurfaceInset, w: icon, x: iconX, y: iconY);

        var label = (toast.IsError ? "ER" : "OK");

        builder.WriteText(
            alpha: alpha,
            cellHeight: microCell,
            role: stateRole,
            text: label,
            x: (iconX + ((icon - builder.TextWidth(chars: label.Length, cellHeight: microCell)) * 0.5f)),
            y: (iconY + ((icon - microCell) * 0.5f))
        );
        var textX = ((iconX + icon) + DesignTokens.Space.Space2);
        var textY = (y + ((panelHeight - textHeight) * 0.5f));

        for (var index = 0; (index < lineCount); index++) {
            builder.WriteText(
                alpha: alpha,
                cellHeight: monoCell,
                maxChars: MaxMessageChars,
                role: OverlayColorRole.TextPrimary,
                text: message[wrapped[index]],
                x: textX,
                y: (textY + (index * monoCell))
            );
        }
    }

    // The message's first-line length (a multi-line echo shows its head; no substring allocation).
    private static int FirstLineLength(string text) {
        var newline = text.AsSpan().IndexOfAny(value0: '\r', value1: '\n');

        return ((newline >= 0) ? newline : text.Length);
    }

    // Greedy word wrap into at most `lines.Length` ranges of MaxMessageChars: break at the last space that fits,
    // hard-break a token longer than a line. Returns the line count; the tail past the last line is dropped.
    private static int Wrap(ReadOnlySpan<char> text, Span<Range> lines) {
        var count = 0;
        var start = 0;

        while ((start < text.Length) && (count < lines.Length)) {
            var remaining = (text.Length - start);

            if (remaining <= MaxMessageChars) {
                lines[count++] = new Range(start: start, end: text.Length);

                return count;
            }

            var window = text.Slice(start: start, length: (MaxMessageChars + 1));
            var space = window.LastIndexOf(value: ' ');
            var take = ((space > 0) ? space : MaxMessageChars);

            lines[count++] = new Range(start: start, end: (start + take));
            start += take;

            while ((start < text.Length) && (text[start] == ' ')) {
                start++;
            }
        }

        return count;
    }
}
