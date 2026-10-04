using System.Globalization;
using Puck.Abstractions.Presentation;

namespace Puck.Overlays;

/// <summary>One kept branch as the scrubber row draws it: the tick it leaves the timeline after and the tick its recorded
/// future ends at.</summary>
/// <param name="Fork">The tick the branch leaves the timeline after.</param>
/// <param name="Head">The tick the branch's recorded future ends at.</param>
public readonly record struct HistoryRowFork(ulong Fork, ulong Head);
/// <summary>A seat's in-session history scrubber row, as its source publishes it: the window, the cursor, the keyframes
/// and the kept branches, read once per frame into the caller's buffers.</summary>
public interface IHistoryRowSource {
    /// <summary>Reads one seat's row, or reports that the seat draws none (it is not building, or the history holds no
    /// window).</summary>
    /// <param name="slot">The zero-based local seat.</param>
    /// <param name="viewport">The seat's normalized clip rectangle.</param>
    /// <param name="oldest">The oldest tick the window reaches.</param>
    /// <param name="head">The newest recorded tick.</param>
    /// <param name="cursor">The tick the live world sits at.</param>
    /// <param name="keyframes">Receives the keyframe ticks to draw, at most its length.</param>
    /// <param name="keyframeCount">The keyframe ticks written.</param>
    /// <param name="forks">Receives the kept branches to draw, at most its length.</param>
    /// <param name="forkCount">The branches written.</param>
    /// <returns><see langword="true"/> when the seat draws a row.</returns>
    bool TryRead(int slot, out NormalizedRect viewport, out ulong oldest, out ulong head, out ulong cursor, Span<ulong> keyframes, out int keyframeCount, Span<HistoryRowFork> forks, out int forkCount);
}
/// <summary>Draws a building seat's in-session history as a scrubber row on the editor channel: the window as a bar,
/// each keyframe as a tick on it, each kept branch as a fork raised above it from its fork tick, and the cursor as a
/// taller mark labelled with its tick. The row's place in the seat's view is <see cref="Rect"/>, which the host's
/// pointer reads to turn a press on the row into a seek, so the drawn bar and the place a drag lands are the same
/// rectangle.</summary>
/// <param name="source">The host's per-seat history row.</param>
/// <param name="theme">The live overlay theme.</param>
public sealed class HistoryRowWriter(IHistoryRowSource source, OverlayThemeStore theme) {
    /// <summary>The most keyframe ticks a row draws; a longer window is sampled evenly, first and last included.</summary>
    public const int MaxKeyframes = 32;
    /// <summary>The most kept branches a row draws.</summary>
    public const int MaxForks = 8;
    /// <summary>The most characters the cursor label writes.</summary>
    public const int MaxLabelChars = 24;
    /// <summary>The element records one seat's row writes: the bar, its keyframes, its forks, the cursor and its label.</summary>
    public const int ElementsPerSeat = ((((1 + MaxKeyframes) + MaxForks) + 1) + 1);

    private readonly ulong[] m_keyframes = new ulong[MaxKeyframes];
    private readonly HistoryRowFork[] m_forks = new HistoryRowFork[MaxForks];
    private readonly char[] m_label = new char[MaxLabelChars];

    /// <summary>Gets the row's rectangle in the seat's view, normalized to the seat's viewport (origin top-left, Y
    /// down): a bar across the lower edge, clear of the binding bar's plates.</summary>
    public static NormalizedRect Rect { get; } = new(Height: 0.024f, Width: 0.8f, X: 0.1f, Y: 0.86f);

    /// <summary>Returns where a tick lies along the row, 0 at <paramref name="oldest"/> and 1 at
    /// <paramref name="head"/>, clamped to the bar.</summary>
    /// <param name="tick">The tick.</param>
    /// <param name="oldest">The window's oldest tick.</param>
    /// <param name="head">The window's newest tick.</param>
    /// <returns>The fraction along the bar.</returns>
    public static float FractionOf(ulong tick, ulong oldest, ulong head) {
        if (head <= oldest) {
            return 1f;
        }

        var clamped = Math.Clamp(max: head, min: oldest, value: tick);

        return ((float)(((double)(clamped - oldest)) / (head - oldest)));
    }
    /// <summary>Emits each building seat's row, clipped to its own view.</summary>
    /// <param name="builder">The unified overlay builder.</param>
    public void Emit(OverlayFrameBuilder builder) {
        for (var slot = 0; (slot < builder.Leases.MaxSeats); slot++) {
            if (!source.TryRead(
                cursor: out var cursor,
                forkCount: out var forkCount,
                forks: m_forks,
                head: out var head,
                keyframeCount: out var keyframeCount,
                keyframes: m_keyframes,
                oldest: out var oldest,
                slot: slot,
                viewport: out var viewport
            )) {
                continue;
            }

            var viewX = (viewport.X * builder.Width);
            var viewY = (viewport.Y * builder.Height);
            var viewW = (viewport.Width * builder.Width);
            var viewH = (viewport.Height * builder.Height);

            if ((viewW <= 16f) || (viewH <= 16f)) {
                continue;
            }

            var x = (viewX + (Rect.X * viewW));
            var y = (viewY + (Rect.Y * viewH));
            var w = (Rect.Width * viewW);
            var h = MathF.Max(x: 6f, y: (Rect.Height * viewH));

            builder.BeginClip(h: viewH, w: viewW, x: viewX, y: viewY);
            builder.WriteRect(alpha: 0.85f, h: h, radius: 3f, role: OverlayColorRole.SurfaceRaised, w: w, x: x, y: y);

            for (var index = 0; (index < Math.Min(val1: keyframeCount, val2: MaxKeyframes)); index++) {
                var at = (x + (FractionOf(head: head, oldest: oldest, tick: m_keyframes[index]) * w));

                builder.WriteRect(alpha: 0.9f, h: h, radius: 0f, role: OverlayColorRole.TextDim, w: 2f, x: (at - 1f), y: y);
            }

            for (var index = 0; (index < Math.Min(val1: forkCount, val2: MaxForks)); index++) {
                var fork = m_forks[index];
                var from = (x + (FractionOf(head: head, oldest: oldest, tick: fork.Fork) * w));
                var to = (x + (FractionOf(head: head, oldest: oldest, tick: fork.Head) * w));

                builder.WriteRect(alpha: 0.9f, h: 3f, radius: 1f, role: OverlayColorRole.Warning, w: MathF.Max(x: 3f, y: (to - from)), x: from, y: (y - (4f + (index * 4f))));
            }

            var mark = (x + (FractionOf(head: head, oldest: oldest, tick: cursor) * w));

            builder.WriteRect(alpha: 1f, h: (h + 8f), radius: 1f, role: OverlayColorRole.Accent, w: 3f, x: (mark - 1.5f), y: (y - 4f));

            var cellHeight = OverlayFrameBuilder.CellHeight(sizePx: theme.Current.Type.MicroSize);

            if (cursor.TryFormat(destination: m_label, charsWritten: out var written, provider: CultureInfo.InvariantCulture)) {
                builder.WriteText(alpha: 1f, cellHeight: cellHeight, maxChars: MaxLabelChars, role: OverlayColorRole.TextPrimary,
                    text: m_label.AsSpan(length: written, start: 0), x: (mark + 4f), y: ((y + h) + 2f));
            }

            builder.EndClip();
        }
    }
}
