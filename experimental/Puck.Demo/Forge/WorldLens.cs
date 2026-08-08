using Puck.HumbleGamingBrick;

namespace Puck.Demo.Forge;

/// <summary>
/// The world→machine membrane: the fourth wall's OTHER direction. The demo already crosses machine→world (a brick's
/// framebuffer onto a diegetic screen; its work-RAM polled to break the wall). This is the reserved mirror — a
/// per-frame host feed that writes into the machine BEFORE it steps — that lets a brick cartridge become a live
/// LENS onto the very world it sits in, rather than a sealed game-within-a-game.
/// </summary>
internal static class WorldLensProtocol {
    /// <summary>The sensor page lives at the base of work RAM (0xC000), the same 0xC000–0xDFFF region the fourth-wall
    /// EXIT condition reads — the membrane's two directions share one page. Both the host peripheral
    /// (<see cref="SensorPagePeripheral"/>) and the forged ROM reference these addresses, so the contract is one truth.</summary>
    public const ushort SensorPageBase = 0xC000;
    public const ushort FacingAddress = 0xC002;
    public const ushort HeartbeatAddress = 0xC003;
    public const ushort PlayerTileXAddress = 0xC000;
    public const ushort PlayerTileYAddress = 0xC001;

    /// <summary>The WIN flag: the ROM writes <see cref="WinMagic"/> here the frame the player's sprite reaches the goal
    /// tile. The host polls it (the exit-condition seam) to break the fourth wall — the player who reaches their goal
    /// first triggers the reveal. GAME-owned (only the ROM writes; the host reads), so it is honest game state, not a
    /// host-planted result.</summary>
    public const ushort WinFlagAddress = 0xC004;

    /// <summary>The BATON: which side drives the sprite this frame (host-written). <see cref="AuthorityWorld"/> = the
    /// world drives (the sprite mirrors the room sensor position — walking); <see cref="AuthorityGame"/> = the GAME
    /// drives (the ROM reads the joypad and integrates its own position). The overworld donates control (game) when a
    /// player takes over the cabinet post-reveal, and reclaims it (world) otherwise — the proximity-takeover IS the
    /// baton.</summary>
    public const ushort AuthorityAddress = 0xC005;

    /// <summary>The GAME's current sprite tile (game-owned: the ROM writes it every frame, whichever side drives). The
    /// host reads it back to move the driving player's presentation avatar in the room — the machine→world half of the
    /// membrane. In world-auth the ROM keeps it equal to the sensor position, so a hand-off is seamless. Clamped to the
    /// reachable window (<see cref="MinTile"/>..<see cref="MaxTileX"/>/<see cref="MaxTileY"/>) so it never overflows a
    /// byte and wraps — an unclamped integration teleports the followed avatar edge to edge.</summary>
    public const ushort GameTileXAddress = 0xC006;
    public const ushort GameTileYAddress = 0xC007;

    /// <summary>Game-owned scratch: a per-frame counter the game-auth path advances the sprite ONE tile every
    /// <see cref="MoveCooldownFrames"/> frames, so a driven sprite (and the avatar following it) walks at a sane pace
    /// rather than crossing the screen in a blink (~60 tiles/s if it stepped every frame).</summary>
    public const ushort MoveCooldownAddress = 0xC008;

    /// <summary>The reachable game-tile window (matches <c>OverworldWorldLens</c>'s sensor mapping: X in [1,17], Y in
    /// [1,15]); the ROM clamps the integrated game tile here so it stays on-screen and never wraps.</summary>
    public const byte MinTile = 1;
    public const byte MaxTileX = 17;
    public const byte MaxTileY = 15;

    /// <summary>Frames between game-auth sprite steps (a power of two so the ROM gates on a cheap AND).</summary>
    public const byte MoveCooldownFrames = 8;

    /// <summary>Written to <see cref="HeartbeatAddress"/> so a ROM can tell a live sensor from an unconnected one
    /// (a standalone physical cartridge with no feed reads 0x00 here and can fall back to a default).</summary>
    public const byte HeartbeatMagic = 0xA5;

    /// <summary>Written to <see cref="WinFlagAddress"/> when the goal is reached.</summary>
    public const byte WinMagic = 0x01;

    /// <summary>The authority baton values written to <see cref="AuthorityAddress"/>.</summary>
    public const byte AuthorityWorld = 0x00;
    public const byte AuthorityGame = 0x01;
}

/// <summary>The slice of world state a lens projects into the machine. Deliberately tiny and engine-neutral; it grows a
/// field at a time as lenses need more (facing is already reserved; entities/hidden-layer flags are the next additions).
/// This is the SHARED world model both the 3D SDF render and the GB render observe — the "true connection".
/// <para><see cref="GameHasControl"/> is the authority baton: false = the world drives the sprite (it mirrors this
/// position — walking); true = the overworld has DONATED control to the game (a post-reveal cabinet takeover), so the ROM
/// reads the joypad and drives its own position, which the host reads back to move the player's presentation avatar.</para></summary>
internal readonly record struct WorldLensState(byte PlayerTileX, byte PlayerTileY, byte Facing, bool GameHasControl = false);

/// <summary>
/// A per-machine-frame host→machine feed, run BEFORE the machine steps. This is the seam the roadmap reserved
/// ("engine→machine sensor feed"): both realizations of the world-lens fit it —
/// <list type="bullet">
/// <item><b>Plan A</b> (<see cref="SensorPagePeripheral"/>): write a compact SENSOR PAGE to work RAM; a real, portable
/// ROM reads it and renders with its own tile engine. The honest membrane, and physical-cartridge friendly.</item>
/// <item><b>Plan B</b> (not built): write VRAM (tiles/map/OAM) directly — the host forges the GB frame live and the ROM
/// is a thin display. Streaming peripherals implement the same interface.</item>
/// </list>
/// It is handed the whole <see cref="MachineInstance"/> so any realization can reach whatever memory it needs (Plan A:
/// <see cref="SystemMemory"/>; Plan B: the video RAM) without widening the seam.
/// </summary>
internal interface IBrickPeripheral {
    /// <summary>Writes this frame's host→machine data into <paramref name="machine"/> before it is stepped.</summary>
    void BeforeStep(in WorldLensState world, MachineInstance machine);
}

/// <summary>Plan A: projects <see cref="WorldLensState"/> into the machine's work-RAM sensor page (host→machine) and
/// reads the game-driven position back out (machine→host) — the full bidirectional membrane. The machine stays a
/// self-contained program — it just receives telemetry and publishes its position, exactly like a homebrew reading the
/// link port — so the same cartridge is valid on real hardware fed by a real sensor.</summary>
internal sealed class SensorPagePeripheral : IBrickPeripheral {
    private SystemMemory? m_memory;

    public void BeforeStep(in WorldLensState world, MachineInstance machine) {
        ArgumentNullException.ThrowIfNull(machine);

        // Resolve once; the same memory instance survives the machine's lifetime.
        m_memory ??= machine.GetRequiredService<SystemMemory>();

        m_memory.WriteWorkRam(address: WorldLensProtocol.PlayerTileXAddress, value: world.PlayerTileX);
        m_memory.WriteWorkRam(address: WorldLensProtocol.PlayerTileYAddress, value: world.PlayerTileY);
        m_memory.WriteWorkRam(address: WorldLensProtocol.FacingAddress, value: world.Facing);
        m_memory.WriteWorkRam(address: WorldLensProtocol.HeartbeatAddress, value: WorldLensProtocol.HeartbeatMagic);
        // The baton: donate control to the game (a post-reveal takeover) or keep the world driving.
        m_memory.WriteWorkRam(address: WorldLensProtocol.AuthorityAddress, value: (world.GameHasControl ? WorldLensProtocol.AuthorityGame : WorldLensProtocol.AuthorityWorld));
    }

    /// <summary>Reads the game-driven sprite tile the ROM published this frame (machine→world) — the host maps it back
    /// to a room position to move the driving player's presentation avatar. Meaningful under game authority; under world
    /// authority it equals the sensor position the host just wrote.</summary>
    public (byte TileX, byte TileY) ReadGameTile(MachineInstance machine) {
        ArgumentNullException.ThrowIfNull(machine);

        var memory = (m_memory ??= machine.GetRequiredService<SystemMemory>());

        return (memory.ReadWorkRam(address: WorldLensProtocol.GameTileXAddress), memory.ReadWorkRam(address: WorldLensProtocol.GameTileYAddress));
    }
}
