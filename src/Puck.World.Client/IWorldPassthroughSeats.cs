namespace Puck.World.Client;

/// <summary>What the passthrough door asks of the local seats framing an engaged window: the pointer feel the framed
/// screen's route authors, and the seat's leaving when the local user presses the reserved chord. The composition root
/// answers it from the delivered document and the seat's own control application.</summary>
public interface IWorldPassthroughSeats {
    /// <summary>Reads the pointer feel a screen's route authors for a pad driving its framed window.</summary>
    /// <param name="screen">The screen's index.</param>
    /// <returns>The route's <see cref="WorldScreenPadPointer"/>, or <see langword="null"/> when the screen authors none
    /// or the document declares no such screen.</returns>
    WorldScreenPadPointer? PadPointerOf(int screen);
    /// <summary>Leaves the engagement a local seat holds on the screen it frames, as its context button does, so its
    /// camera eases back to its body.</summary>
    /// <param name="slot">The 0-based local seat.</param>
    void Leave(int slot);
}
