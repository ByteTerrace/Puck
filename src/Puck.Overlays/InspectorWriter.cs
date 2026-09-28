using Puck.Abstractions.Presentation;

namespace Puck.Overlays;

/// <summary>A presentation inspector's already-formatted text. The source owns the memory until its next frame.</summary>
public interface IInspectorSource {
    /// <summary>Reads one seat's panel; empty text means the inspector is disabled.</summary>
    /// <param name="slot">The zero-based local seat.</param>
    /// <param name="viewport">The seat's normalized clip rectangle.</param>
    /// <returns>The text shared with the host's inspector command.</returns>
    ReadOnlySpan<char> Read(int slot, out NormalizedRect viewport);
    /// <summary>Returns whether this seat's formatter refused text beyond the editor channel's reservation.</summary>
    /// <param name="slot">The zero-based local seat.</param>
    /// <returns>Whether the writer must attribute an explicit editor refusal.</returns>
    bool Refused(int slot) => false;
}
/// <summary>Draws bounded inspector text on the existing unified overlay, above authored panels.</summary>
/// <param name="source">The host's shared inspector formatter output.</param>
/// <param name="theme">The live overlay theme.</param>
public sealed class InspectorWriter(IInspectorSource source, OverlayThemeStore theme) {
    /// <summary>The maximum displayed lines per seat, used by the editor channel's reservation.</summary>
    public const int MaxLines = 32;
    /// <summary>The maximum displayed characters per line, also used by the reservation.</summary>
    public const int MaxLineChars = 96;

    /// <summary>Emits each enabled inspector, clipped to its own seat.</summary>
    /// <param name="builder">The existing unified overlay builder.</param>
    public void Emit(OverlayFrameBuilder builder) {
        for (var slot = 0; (slot < builder.Leases.MaxSeats); slot++) {
            var text = source.Read(slot: slot, viewport: out var viewport);

            if (text.IsEmpty || ((viewport.Width * builder.Width) <= 16) || (viewport.Height <= 0)) { continue; }
            if (source.Refused(slot: slot)) { builder.NoteRefused(elements: 0, textWords: 1); }
            var x = ((viewport.X * builder.Width) + 8);
            var y = ((viewport.Y * builder.Height) + 8);
            var height = OverlayFrameBuilder.CellHeight(sizePx: theme.Current.Type.MicroSize);

            builder.BeginClip(x: (viewport.X * builder.Width), y: (viewport.Y * builder.Height),
                w: (viewport.Width * builder.Width), h: (viewport.Height * builder.Height));
            builder.WriteRect(x: x, y: y, w: MathF.Min(x: 680, y: ((viewport.Width * builder.Width) - 16)),
                h: ((MaxLines * height) + 12), radius: 4, alpha: 0.85f, role: OverlayColorRole.SurfaceRaised);
            var line = 0;

            foreach (var range in text.Split(separator: '\n')) {
                if (line == MaxLines) {
                    builder.NoteRefused(elements: 1, textWords: text[range].Length);
                    continue;
                }
                builder.WriteText(text: text[range], x: (x + 6), y: ((y + 6) + (line++ * height)), cellHeight: height,
                    maxChars: MaxLineChars, role: OverlayColorRole.TextPrimary, alpha: 1);
            }
            builder.EndClip();
        }
    }
}
