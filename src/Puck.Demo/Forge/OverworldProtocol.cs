namespace Puck.Demo.Forge;

/// <summary>
/// The work-RAM contract of the forged OVERWORLD cartridge (the top-down RPG-style walker): the addresses the ROM
/// keeps its player state at, the reachable pixel window, and the sprite-sheet pose layout. The ROM's SM83 routine
/// (<see cref="HgbCartridge.BuildOverworld"/>) writes these every frame; the forge's self-verification
/// (<c>RomForge.VerifyOverworld</c>) reads them back on a real emulator to prove movement, facing, the walk animation,
/// and the boundary clamp — so this is the ONE place the two sides agree on the layout.
/// </summary>
internal static class OverworldProtocol {
    // Player state (WRAM, always host-readable via ISystemBus.ReadByte).
    public const ushort PlayerXAddress = 0xC000;
    public const ushort PlayerYAddress = 0xC001;
    public const ushort FacingAddress = 0xC002;
    public const ushort AnimTimerAddress = 0xC003;
    public const ushort MovingAddress = 0xC004;
    public const ushort TileScratchAddress = 0xC005; // the pose's tile base, recomputed each frame

    // The reachable window for the 16×16 sprite's top-left (screen pixels), and the spawn point.
    public const byte MinX = 16;
    public const byte MaxX = 128;
    public const byte MinY = 16;
    public const byte MaxY = 104;
    public const byte StartX = 72;
    public const byte StartY = 56;
    public const byte WalkSpeed = 2; // pixels per frame

    // The sprite-sheet facing order (KEEP IN SYNC with AvatarForge's pose layout) and pose geometry.
    public const byte FacingDown = 0;
    public const byte FacingUp = 1;
    public const byte FacingLeft = 2;
    public const byte FacingRight = 3;
    public const int FramesPerFacing = 3; // idle, step A, step B
    public const int TilesPerPose = 4;    // 2×2 metasprite
}
