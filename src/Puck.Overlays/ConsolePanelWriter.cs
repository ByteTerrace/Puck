namespace Puck.Overlays;

/// <summary>
/// The console-panel writer: renders an <see cref="IConsolePanelSource"/> snapshot as one titled scrim panel
/// (top-left, stage-margin inset) holding the trailing output lines that fit plus the live prompt row — all through
/// the unified record vocabulary (one panel + one text run per row), no bespoke grid shader. Pure record emission;
/// no GPU types.
/// </summary>
public sealed class ConsolePanelWriter {
    private const string PromptPrefix = "> ";
    private const string Title = "CONSOLE";
    // The visible-row cap: enough scrollback to read a verb exchange without eating the element budget
    // (rows + prompt + title = at most 18 of the frame's 192 elements).
    private const int MaxRows = 16;

    private readonly IConsolePanelSource m_source;

    /// <summary>Initializes a new instance of the <see cref="ConsolePanelWriter"/> class.</summary>
    /// <param name="source">The console snapshot source.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public ConsolePanelWriter(IConsolePanelSource source) {
        ArgumentNullException.ThrowIfNull(argument: source);

        m_source = source;
    }

    /// <summary>Emits this frame's console panel, when one is visible.</summary>
    /// <param name="builder">The frame builder.</param>
    public void Emit(OverlayFrameBuilder builder) {
        ArgumentNullException.ThrowIfNull(argument: builder);

        if (!m_source.TrySnapshot(frame: out var frame) || !frame.Visible) {
            return;
        }

        var margin = DesignTokens.Space.Space8;
        var pad = DesignTokens.Space.Space3;
        var bandHeight = DesignTokens.Space.HeightConsoleHead;
        var cellHeight = OverlayFrameBuilder.CellHeight(sizePx: DesignTokens.Type.TypeMonoSize);
        var cellWidth = builder.CellWidth(cellHeight: cellHeight);
        var microCell = OverlayFrameBuilder.CellHeight(sizePx: DesignTokens.Type.TypeMicroSize);

        // The grid fills the top-left without overrunning: cols across, rows up to ~55% of the height, then the
        // panel's outer rect wraps the padded grid + title band.
        var availableWidth = ((builder.Width - (2f * margin)) - (2f * pad));
        var availableHeight = (((builder.Height * 0.55f) - bandHeight) - (2f * pad));
        var cols = Math.Clamp(value: (int)(availableWidth / cellWidth), min: 8, max: 120);
        var rows = Math.Clamp(value: (int)(availableHeight / cellHeight), min: 4, max: MaxRows);
        var panelWidth = ((cols * cellWidth) + (2f * pad));
        var panelHeight = ((bandHeight + (2f * pad)) + (rows * cellHeight));

        builder.WritePanel(alpha: 1f, bandHeight: bandHeight, h: panelHeight, ringRole: null, style: OverlayPanelStyle.Panel, titleBand: true, w: panelWidth, x: margin, y: margin);
        builder.WriteText(
            alpha: 1f,
            cellHeight: microCell,
            role: OverlayColorRole.TextDim,
            text: Title,
            x: (margin + pad),
            y: (margin + ((bandHeight - microCell) * 0.5f))
        );

        // Trailing history above the prompt row; the echoed input lines ("> ...") keep the sanctioned phosphor voice.
        var lines = frame.Lines;
        var historyRows = (rows - 1);
        var firstShown = Math.Max(val1: 0, val2: (lines.Count - historyRows));
        var contentX = (margin + pad);
        var contentY = ((margin + bandHeight) + pad);

        for (var row = 0; ((row < historyRows) && ((firstShown + row) < lines.Count)); row++) {
            var line = lines[(firstShown + row)];
            var isEcho = line.StartsWith(value: PromptPrefix, comparisonType: StringComparison.Ordinal);

            builder.WriteText(
                alpha: 1f,
                cellHeight: cellHeight,
                maxChars: cols,
                role: (isEcho ? OverlayColorRole.Phosphor : OverlayColorRole.TextPrimary),
                text: line,
                x: contentX,
                y: (contentY + (row * cellHeight))
            );
        }

        // The live prompt on the bottom row: the fixed prefix then the in-progress input.
        var promptY = (contentY + ((rows - 1) * cellHeight));

        builder.WriteText(alpha: 1f, cellHeight: cellHeight, maxChars: cols, role: OverlayColorRole.Phosphor, text: PromptPrefix, x: contentX, y: promptY);
        builder.WriteText(
            alpha: 1f,
            cellHeight: cellHeight,
            maxChars: (cols - PromptPrefix.Length),
            role: OverlayColorRole.TextPrimary,
            text: frame.Input,
            x: (contentX + builder.TextWidth(chars: PromptPrefix.Length, cellHeight: cellHeight)),
            y: promptY
        );
    }
}
