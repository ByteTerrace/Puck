using Puck.Hosting;

namespace Puck.Overlays;

/// <summary>
/// The toast writer: renders the latest <see cref="IOverlayToastSource"/> echo as a transient Tier-1 chip
/// (mid-right) — scrim-chip fill, bloom ring + halo and a 2px state rail in the [OK]/[ERR] hue, an inset icon
/// square, one line of text. Expires on the CONTENT tick (~3 seconds equivalent, never wall clock) with a trailing
/// <c>dur.med</c> opacity-only fade (text never translates — motion stays calm per the token spec). Pure record
/// emission; no GPU types.
/// </summary>
public sealed class ToastWriter {
    private const int MaxMessageChars = 44;
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
        var messageChars = Math.Min(val1: FirstLineLength(text: toast.Message), val2: MaxMessageChars);
        var icon = DesignTokens.Space.HeightBadge;
        var panelHeight = DesignTokens.Space.HeightChip;
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
        builder.WriteText(
            alpha: alpha,
            cellHeight: monoCell,
            maxChars: messageChars,
            role: OverlayColorRole.TextPrimary,
            text: toast.Message,
            x: ((iconX + icon) + DesignTokens.Space.Space2),
            y: (y + ((panelHeight - monoCell) * 0.5f))
        );
    }

    // The message's first-line length (a multi-line echo shows its head; no substring allocation).
    private static int FirstLineLength(string text) {
        var newline = text.AsSpan().IndexOfAny(value0: '\r', value1: '\n');

        return ((newline >= 0) ? newline : text.Length);
    }
}
