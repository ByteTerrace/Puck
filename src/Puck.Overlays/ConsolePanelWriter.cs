using Puck.Hosting;

namespace Puck.Overlays;

/// <summary>
/// The console-panel writer: renders an <see cref="IConsoleTapeSource"/> snapshot as one titled scrim panel
/// (top-left, stage-margin inset) holding the trailing output lines that fit plus the live prompt row — all through
/// the unified record vocabulary (one panel + one text run per row), no bespoke grid shader. Pure record emission;
/// no GPU types.
/// </summary>
public sealed class ConsolePanelWriter {
    private const string PromptPrefix = "> ";
    private const string Title = "CONSOLE";

    /// <summary>The visible-column cap one row is clipped to.</summary>
    public const int MaxColumns = 120;
    /// <summary>The visible-row cap — enough scrollback to read a verb exchange. The panel's whole element and text
    /// reservation is derived from this and <see cref="MaxColumns"/>.</summary>
    public const int MaxRows = 16;
    /// <summary>The <see cref="Title"/> literal's character count — the ONE source <see cref="OverlayChannelLeases"/>
    /// reads for its text-word reservation. Every <c>WriteText</c> call for <see cref="Title"/> clamps to this
    /// constant, so an edit to <see cref="Title"/> that forgets to update it truncates the drawn title (reported,
    /// never silent — see <see cref="OverlayFrameBuilder.Refused"/>) instead of quietly overrunning the reservation.</summary>
    public const int TitleChars = 7;

    private readonly OverlayThemeStore m_theme;
    private readonly IConsoleTapeSource m_source;

    static ConsolePanelWriter() {
        System.Diagnostics.Debug.Assert(
            condition: (Title.Length == TitleChars),
            message: "ConsolePanelWriter.Title's length drifted from TitleChars — update TitleChars (and OverlayChannelLeases' ConsoleTextWords, which reads it) to match."
        );
    }

    /// <summary>Initializes a new instance of the <see cref="ConsolePanelWriter"/> class.</summary>
    /// <param name="source">The console-tape snapshot source.</param>
    /// <param name="theme">The live resolved theme.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="theme"/> is
    /// <see langword="null"/>.</exception>
    public ConsolePanelWriter(IConsoleTapeSource source, OverlayThemeStore theme) {
        ArgumentNullException.ThrowIfNull(argument: source);
        ArgumentNullException.ThrowIfNull(argument: theme);

        m_source = source;
        m_theme = theme;
    }

    /// <summary>Emits this frame's console panel, when one is visible.</summary>
    /// <param name="builder">The frame builder.</param>
    public void Emit(OverlayFrameBuilder builder) {
        ArgumentNullException.ThrowIfNull(argument: builder);

        if (
            !m_source.TrySnapshot(frame: out var frame) ||
            !frame.Visible
        ) {
            return;
        }

        var theme = m_theme.Current;
        var margin = theme.Space.Space8;
        var pad = theme.Space.Space3;
        var bandHeight = theme.Space.HeightConsoleHead;
        var cellHeight = OverlayFrameBuilder.CellHeight(sizePx: theme.Type.MonoSize);
        var cellWidth = builder.CellWidth(cellHeight: cellHeight);
        var microCell = OverlayFrameBuilder.CellHeight(sizePx: theme.Type.MicroSize);

        // The grid fills the top-left without overrunning: cols across, rows up to ~55% of the height, then the
        // panel's outer rect wraps the padded grid + title band.
        var availableWidth = ((builder.Width - (2f * margin)) - (2f * pad));
        var availableHeight = (((builder.Height * 0.55f) - bandHeight) - (2f * pad));
        var cols = Math.Clamp(
            max: MaxColumns,
            min: 8,
            value: ((int)(availableWidth / cellWidth))
        );
        var rows = Math.Clamp(
            max: MaxRows,
            min: 4,
            value: ((int)(availableHeight / cellHeight))
        );
        var panelWidth = ((cols * cellWidth) + (2f * pad));
        var panelHeight = ((bandHeight + (2f * pad)) + (rows * cellHeight));

        builder.WritePanel(
            alpha: 1f,
            bandHeight: bandHeight,
            h: panelHeight,
            ringRole: null,
            style: OverlayPanelStyle.Panel,
            titleBand: true,
            w: panelWidth,
            x: margin,
            y: margin
        );
        builder.WriteText(
            alpha: 1f,
            cellHeight: microCell,
            maxChars: TitleChars,
            role: OverlayColorRole.TextDim,
            text: Title,
            x: (margin + pad),
            y: (margin + ((bandHeight - microCell) * 0.5f))
        );

        // Trailing history above the prompt row; the echoed input lines ("> ...") keep the sanctioned phosphor voice.
        var lines = frame.Lines;
        var historyRows = (rows - 1);
        var firstShown = Math.Max(
            val1: 0,
            val2: (lines.Count - historyRows)
        );
        var contentX = (margin + pad);
        var contentY = ((margin + bandHeight) + pad);

        for (var row = 0; ((row < historyRows) && ((firstShown + row) < lines.Count)); row++) {
            var line = lines[(firstShown + row)];
            var isEcho = line.Text.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: PromptPrefix
            );

            // A refused row takes the same danger hue the toast channel uses for a rejection, so the panel and the toast
            // agree on what a refusal looks like.
            builder.WriteText(
                alpha: 1f,
                cellHeight: cellHeight,
                maxChars: cols,
                role: (line.Refused
                ? OverlayColorRole.Danger
                : (isEcho
                    ? OverlayColorRole.Phosphor
                    : OverlayColorRole.TextPrimary)),
                text: line.Text,
                x: contentX,
                y: (contentY + (row * cellHeight))
            );
        }

        // The live prompt on the bottom row: the fixed prefix then the in-progress input.
        var promptY = (contentY + ((rows - 1) * cellHeight));

        builder.WriteText(
            alpha: 1f,
            cellHeight: cellHeight,
            maxChars: cols,
            role: OverlayColorRole.Phosphor,
            text: PromptPrefix,
            x: contentX,
            y: promptY
        );

        var inputX = (contentX + builder.TextWidth(
            chars: PromptPrefix.Length,
            cellHeight: cellHeight
        ));

        if (frame.Selected) {
            // A subtle highlight behind the whole input line stands in for the caret glyph while Ctrl+A's
            // all-or-nothing selection is active — clamped to the remaining row width like the text run below.
            var highlightWidth = Math.Min(
                val1: builder.TextWidth(
                    chars: frame.Input.Length,
                    cellHeight: cellHeight
                ),
                val2: ((contentX + (cols * cellWidth)) - inputX)
            );

            builder.WriteRect(
                alpha: theme.Chrome.DimQuietAlpha,
                h: cellHeight,
                radius: 0f,
                role: OverlayColorRole.TextDim,
                w: highlightWidth,
                x: inputX,
                y: promptY
            );
        }

        builder.WriteText(
            alpha: 1f,
            cellHeight: cellHeight,
            maxChars: (cols - PromptPrefix.Length),
            role: OverlayColorRole.TextPrimary,
            text: frame.Input,
            x: inputX,
            y: promptY
        );
    }
}
