using System.Numerics;
using Puck.Demo.Forge;

namespace Puck.Demo.Overworld;

/// <summary>
/// The overworld half of the world→machine membrane: maps a room player's normalized position into the
/// <see cref="WorldLensState"/> a world-lens cartridge reads from its sensor page. Dropped into a cabinet, the
/// world-lens cart turns the brick into a live lens back onto the room it stands in — its on-screen sprite tracks
/// whoever is walking the overworld. <c>OverworldRenderNode</c> feeds each world-lens brick this every frame.
/// </summary>
internal static class OverworldWorldLens {
    // Tile-coordinate window keeping the 8×16 sprite fully on the 20×18-tile screen: column in [1,17], row in [1,15].
    // (The ROM places OAM X = tileX*8 + 8 and Y = tileY*8 + 16, so these bounds hold the whole sprite inside the frame.)
    private const int TileOriginX = 1;
    private const int TileOriginY = 1;
    private const int TileSpanX = 16;
    private const int TileSpanY = 14;

    /// <summary>Projects a room player's normalized position (X,Z each in [0,1], as
    /// <see cref="OverworldWorld.PlayerRoomFraction"/> returns) into the sensor-page tile coordinates the world-lens ROM
    /// reads. A null fraction (no active player) centers the sprite.</summary>
    /// <param name="roomFraction">The normalized room position, or <see langword="null"/> for the room center.</param>
    /// <param name="gameHasControl">The authority baton: <see langword="true"/> donates control to the game (a
    /// post-reveal takeover — the ROM reads the joypad), <see langword="false"/> keeps the world driving (mirror).</param>
    /// <returns>The world slice to write into the machine's sensor page.</returns>
    public static WorldLensState LensStateFor(Vector2? roomFraction, bool gameHasControl = false) {
        var fraction = (roomFraction ?? new Vector2(x: 0.5f, y: 0.5f));
        var tileX = (byte)(TileOriginX + (int)MathF.Round(x: (Math.Clamp(value: fraction.X, min: 0f, max: 1f) * TileSpanX)));
        var tileY = (byte)(TileOriginY + (int)MathF.Round(x: (Math.Clamp(value: fraction.Y, min: 0f, max: 1f) * TileSpanY)));

        return new WorldLensState(PlayerTileX: tileX, PlayerTileY: tileY, Facing: 0, GameHasControl: gameHasControl);
    }

    /// <summary>The inverse of the position→tile mapping: a game-driven sprite tile (as
    /// <see cref="GamingBrickChildNode.WorldLensGameTile"/> reports) back to a normalized room fraction, so a driving
    /// player's presentation avatar can follow their brick sprite around the room (the machine→world half).</summary>
    /// <param name="tileX">The game sprite's tile column.</param>
    /// <param name="tileY">The game sprite's tile row.</param>
    /// <returns>The normalized room position (X,Z each in [0,1]).</returns>
    public static Vector2 RoomFractionForTile(byte tileX, byte tileY) {
        var fractionX = ((tileX - TileOriginX) / (float)TileSpanX);
        var fractionY = ((tileY - TileOriginY) / (float)TileSpanY);

        return new Vector2(x: Math.Clamp(value: fractionX, min: 0f, max: 1f), y: Math.Clamp(value: fractionY, min: 0f, max: 1f));
    }
}
