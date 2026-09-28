using System.Numerics;
using Puck.Maths;

namespace Puck.Commands;

/// <summary>
/// The one rule for which pane a display point falls on when panes overlap: panes are listed in drawing order, so the
/// topmost is the last whose face holds the point. A face holds a point whether the point lands on the source, on a
/// letterbox bar, on a bezel or on a face whose warp declares no inverse; each of those still covers what is drawn
/// beneath it. Only a point outside a pane's face falls through to the panes below. World-surface placements are never
/// hit by a display point, since a ray maps them (<see cref="SourceMapping.MapRay"/>).
/// </summary>
public static class SourcePanes {
    /// <summary>Finds the topmost pane whose face holds a display point.</summary>
    /// <param name="panes">The mappings the display shows, in drawing order; only <see cref="SourcePlacement.Pane"/>
    /// placements are read.</param>
    /// <param name="point">The point, in display pixels from the display's top-left corner.</param>
    /// <param name="displayWidth">The display's width, in pixels; positive.</param>
    /// <param name="displayHeight">The display's height, in pixels; positive.</param>
    /// <param name="hit">The point mapped onto that pane when this returns a position; default otherwise.</param>
    /// <returns>The pane's position in <paramref name="panes"/>, or -1 when no pane's face holds the point.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="panes"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="displayWidth"/> or <paramref name="displayHeight"/>
    /// is not positive.</exception>
    public static int Topmost(IReadOnlyList<SourceMapping> panes, FixedVector2 point, int displayWidth, int displayHeight, out SourceHit hit) {
        ArgumentNullException.ThrowIfNull(argument: panes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: displayWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: displayHeight);

        for (var index = (panes.Count - 1); (index >= 0); index--) {
            if (panes[index].Placement is not SourcePlacement.Pane) {
                continue;
            }

            var candidate = panes[index].MapDisplayPoint(
                displayHeight: displayHeight,
                displayWidth: displayWidth,
                point: point
            );

            if (candidate.Outcome != SourceHitOutcome.OutsidePlacement) {
                hit = candidate;

                return index;
            }
        }

        hit = default;

        return -1;
    }
}
/// <summary>
/// The presentation destination's CPU picker: it answers <see cref="ISourcePicker"/> from the pane mappings the
/// display last published, through <see cref="SourcePanes.Topmost"/>, so hover and highlight follow the same topmost
/// rule the hit walk continues through. A point whose topmost pane holds it off its source, on a letterbox bar or a
/// bezel, picks nothing rather than the pane beneath. Nothing it answers reaches state.
/// </summary>
public sealed class SourcePanePicker : ISourcePicker {
    private IReadOnlyList<SourceMapping> m_panes = [];
    private int m_displayHeight = 1;
    private int m_displayWidth = 1;

    /// <summary>Publishes the panes the display shows and its extent; the next pick reads them.</summary>
    /// <param name="panes">The mappings, in drawing order.</param>
    /// <param name="displayWidth">The display's width, in pixels; positive.</param>
    /// <param name="displayHeight">The display's height, in pixels; positive.</param>
    /// <exception cref="ArgumentNullException"><paramref name="panes"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="displayWidth"/> or <paramref name="displayHeight"/>
    /// is not positive.</exception>
    public void Publish(IReadOnlyList<SourceMapping> panes, int displayWidth, int displayHeight) {
        ArgumentNullException.ThrowIfNull(argument: panes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: displayWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: displayHeight);

        m_panes = panes;
        m_displayWidth = displayWidth;
        m_displayHeight = displayHeight;
    }
    /// <inheritdoc/>
    public bool TryPick(Vector2 point, out SourcePick pick) {
        var index = SourcePanes.Topmost(
            displayHeight: m_displayHeight,
            displayWidth: m_displayWidth,
            hit: out var hit,
            panes: m_panes,
            point: new FixedVector2(
                X: FixedQ4816.FromDouble(value: point.X),
                Y: FixedQ4816.FromDouble(value: point.Y)
            )
        );

        if (
            (index < 0) ||
            !hit.IsOnSource
        ) {
            pick = default;

            return false;
        }

        pick = new SourcePick(
            Hit: hit,
            Source: m_panes[index].Source
        );

        return true;
    }
}
