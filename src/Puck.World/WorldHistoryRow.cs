using Puck.Abstractions.Presentation;
using Puck.Overlays;
using Puck.World.Client;

namespace Puck.World;

/// <summary>The in-session history's scrubber row for each building seat: the editor overlay draws it
/// (<see cref="HistoryRowWriter"/>) from what the history last published (<see cref="WorldHistory.TryReadRow"/>, the
/// read <c>world.history row</c> echoes), and the seat's pointer reads it back as a place on the window
/// (<see cref="IWorldHistoryPointer"/>), so <c>world.history.drag</c> seeks to the tick drawn under the pointer. Both
/// sides use the one rectangle the writer draws (<see cref="HistoryRowWriter.Rect"/>) inside the seat's view. A seat
/// draws a row while it builds and the history holds a window. Presentation only: the seek it leads to is a command
/// under the seat's principal.</summary>
/// <param name="history">The boot world's history.</param>
/// <param name="bindings">Which seats build.</param>
/// <param name="viewports">Each seat's view for the frame just dressed.</param>
/// <param name="pointer">The pointer store, or <see langword="null"/> for a host without one.</param>
public sealed class WorldHistoryRow(WorldHistory history, WorldSeatBindings bindings, WorldSeatViewports viewports, WorldPointer? pointer = null) : IHistoryRowSource, IWorldHistoryPointer {
    // How far above and below the bar a press still lands on the row, in bar heights: the cursor mark and the forks
    // drawn above it belong to the row.
    private const float ReachBars = 1f;

    // Sized to what the history publishes, which the writer draws whole (HistoryRowWriter.MaxKeyframes and MaxForks
    // equal WorldHistory.RowKeyframes and RowForks), so the drawn row is the row world.history row echoes.
    private readonly WorldHistoryRowFork[] m_forks = new WorldHistoryRowFork[WorldHistory.RowForks];

    // Whether a seat's view can carry a row now; the history decides whether there is one.
    private bool Draws(int slot) => (
        bindings.IsBuilding(slot: slot) &&
        viewports.Seat(slot: slot).Present
    );

    /// <inheritdoc/>
    public bool TryRead(int slot, out NormalizedRect viewport, out ulong oldest, out ulong head, out ulong cursor, Span<ulong> keyframes, out int keyframeCount, Span<HistoryRowFork> forks, out int forkCount) {
        viewport = viewports.Seat(slot: slot).Region;
        oldest = 0UL;
        head = 0UL;
        cursor = 0UL;
        keyframeCount = 0;
        forkCount = 0;

        if (
            !Draws(slot: slot) ||
            !history.TryReadRow(
                forkCount: out var published,
                forks: m_forks,
                keyframeCount: out keyframeCount,
                keyframes: keyframes[..Math.Min(val1: keyframes.Length, val2: WorldHistory.RowKeyframes)],
                window: out var window
            )
        ) {
            return false;
        }

        oldest = window.Oldest;
        head = window.Head;
        cursor = window.Cursor;
        forkCount = Math.Min(val1: published, val2: forks.Length);

        for (var index = 0; (index < forkCount); index++) {
            forks[index] = new HistoryRowFork(Fork: m_forks[index].Fork, Head: m_forks[index].Head);
        }

        return true;
    }
    /// <inheritdoc/>
    public bool TryFraction(int slot, out double fraction) {
        fraction = 0d;

        if (
            (pointer is not { } store) ||
            !store.HasPosition(slot: slot) ||
            !Draws(slot: slot) ||
            !history.TryTickAt(fraction: 0d, tick: out _)
        ) {
            return false;
        }

        var view = viewports.Seat(slot: slot);

        if (viewports.Locate(
            framePosition: out _,
            local: out var local,
            position: store.Position(slot: slot),
            view: in view
        ) != WorldSeatPointerPlace.Inside) {
            return false;
        }

        return TryFractionAt(fraction: out fraction, local: local);
    }
    /// <summary>Returns where a point in a seat's view lies along the drawn row: the place a press lands.</summary>
    /// <param name="local">The point, normalized to the seat's viewport (origin top-left, Y down).</param>
    /// <param name="fraction">The fraction along the row, 0 at its left end and 1 at its right, on success.</param>
    /// <returns><see langword="true"/> when the point is on the row.</returns>
    public static bool TryFractionAt(System.Numerics.Vector2 local, out double fraction) {
        var rect = HistoryRowWriter.Rect;
        var reach = (rect.Height * ReachBars);

        fraction = 0d;

        if (
            (local.X < rect.X) ||
            (local.X > (rect.X + rect.Width)) ||
            (local.Y < (rect.Y - reach)) ||
            (local.Y > ((rect.Y + rect.Height) + reach))
        ) {
            return false;
        }

        fraction = ((local.X - rect.X) / rect.Width);

        return true;
    }
}
