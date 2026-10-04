namespace Puck.World;

/// <summary>Where a seat's pointer lies along the in-session history scrubber row its view draws — the presentation
/// fact <c>world.history.drag</c> turns into a seek. The host that draws the row answers it from the same rectangle it
/// draws, so the place a press lands and the bar the seat sees are one; it attaches itself as
/// <see cref="WorldHistory.Pointer"/>.</summary>
public interface IWorldHistoryPointer {
    /// <summary>Returns where the seat's pointer lies along its drawn row, 0 at the window's oldest tick and 1 at its
    /// head, or reports that the pointer is not over a row the seat draws.</summary>
    /// <param name="slot">The zero-based local seat.</param>
    /// <param name="fraction">The fraction along the row, on success.</param>
    /// <returns><see langword="true"/> when the seat's pointer is over its drawn row.</returns>
    bool TryFraction(int slot, out double fraction);
}
