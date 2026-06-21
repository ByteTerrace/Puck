namespace Puck.Input.Output;

/// <summary>
/// The canonical per-player indicator colors, shared so a controller's light bar and anything that mirrors it
/// (an on-screen player cursor, a scoreboard swatch) stay in agreement. Four distinct hues cycle by player slot.
/// </summary>
public static class GamepadPlayerColors
{
    private static readonly LedColor[] Palette = [
        new(Red: 0x00, Green: 0x00, Blue: 0x40), // player 1 — blue
        new(Red: 0x40, Green: 0x00, Blue: 0x00), // player 2 — red
        new(Red: 0x00, Green: 0x40, Blue: 0x00), // player 3 — green
        new(Red: 0x20, Green: 0x00, Blue: 0x20), // player 4 — purple
    ];

    /// <summary>Gets the indicator color for a player slot (wrapping every four players).</summary>
    /// <param name="playerIndex">The zero-based player slot.</param>
    /// <returns>The player's indicator color.</returns>
    public static LedColor ForPlayer(int playerIndex) {
        return Palette[playerIndex & 3];
    }
}
