using Puck.Bench;
using Puck.Demo.Overworld;

namespace Puck.Demo.Bench;

/// <summary>
/// The <c>room.active</c> engine-bench scene: the revealed room with FOUR cabinets booted and emulating — it measures
/// the world render + four live SM83 emulator steps + four lit CRT screens + the screen lights they spill. Setup
/// reveals the room, sets a FIXED four-cart set (four distinct hand-authored games — volley/brickfall/chroma/solitaire,
/// cart types 4/5/6/7, all built at boot so every cabinet reliably boots into a running game with a lit CRT), boots all
/// four, and settles. <see cref="IsReady"/> holds warming until all four report booted. The camera is pinned to the ONE
/// pose of record — <see cref="RoomBenchScene"/>'s internal-static overview pin (reused, not forked) — through the frame
/// source's one-frame-sticky bench-camera seam.
/// </summary>
internal sealed class ActiveRoomBenchScene : IBenchSceneController {
    // The four booted cabinets — the fixed benchmark set. Distinct, meaningful, and always-available (cart types 4-7
    // are hand-authored SM83 games the render node builds at boot, unlike the lazily-forged types).
    private const int BootCount = 4;

    private readonly Func<OverworldFrameSource?> m_frameSource;

    /// <summary>Creates the active-room scene over a lazy frame-source resolver.</summary>
    /// <param name="frameSource">Resolves the overworld frame source (cabinet boot/poll + the bench-camera pin seam) —
    /// null until the node's first frame, by which point every bench run has started.</param>
    public ActiveRoomBenchScene(Func<OverworldFrameSource?> frameSource) {
        ArgumentNullException.ThrowIfNull(argument: frameSource);

        m_frameSource = frameSource;
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> SetupScript => [
        "reveal",
        "cart 0 4",   // volley
        "cart 1 5",   // brickfall
        "cart 2 6",   // chroma
        "cart 3 7",   // solitaire
        "boot 0",
        "boot 1",
        "boot 2",
        "boot 3",
        "settle",
    ];

    // Eject all four cabinets the setup booted so the room returns to zero-booted — otherwise the NEXT run (or the next
    // sweep leg) would find four cabinets still running and its room.flythrough, calibrated on a zero-cabinet room,
    // would measure a polluted scene. The `eject` verb is the exact reverse of the `boot` verbs above (idempotent on an
    // already-empty cabinet), and it drains the same console pipe the setup script does.
    /// <inheritdoc/>
    public IReadOnlyList<string> TeardownScript => [
        "eject 0",
        "eject 1",
        "eject 2",
        "eject 3",
    ];

    /// <inheritdoc/>
    public void OnFrame(int frameIndex) =>
        m_frameSource()?.ArmBenchCamera(
            distance: RoomBenchScene.PinDistance,
            fullscreen: true,
            pitch: RoomBenchScene.PinPitch,
            target: RoomBenchScene.PinTarget,
            yaw: RoomBenchScene.PinYaw
        );

    /// <inheritdoc/>
    public bool IsReady() =>
        ((m_frameSource() is { } frameSource) && (frameSource.BenchBootedConsoleCount >= Math.Min(val1: BootCount, val2: frameSource.BenchConsoleCount)));
}
