using System.Numerics;
using Puck.SdfVm;

namespace Puck.Demo.Overworld;

/// <summary>
/// The AGB-DEBUG mode's frame-source surface: the native GBA scene renders as ONE fullscreen diegetic
/// <see cref="SdfProgramBuilder.ScreenSlab"/> that samples the live ARM7TDMI framebuffer (published by the DI-held
/// <c>AgbDebugService</c> and pushed in each produced frame by the render node), framed head-on so it fills the view —
/// the raster sibling of the SDF-debug takeover. This partial holds ONLY PRIMITIVE state (a bool, a native handle, a
/// colour) and emits the slab, so composing it adds no facade TYPE to this source, which sits at its exact analyzer
/// coupling ceiling. Presentation only — the deterministic simulation never learns the AGB scene exists.
/// </summary>
public sealed partial class OverworldFrameSource {
    /// <summary>The diegetic screen-surface slot the fullscreen AGB slab samples — a headroom slot (cabinets use 0–3,
    /// the easel borrows 3), matched by the render node's screen-source table when the scene is active.</summary>
    public const int AgbDebugScreenSlot = 4;

    // The fullscreen slab's authored size + the head-on camera distance that makes it fill the vertical FoV (the
    // director's own FieldOfViewRadians = 50°, so tan(25°)). The GBA panel is 3:2 (240×160), wider than the GB slab.
    private const float AgbSlabHalfHeight = 1.0f;
    private const float AgbSlabAspect = (240f / 160f);
    private const float AgbSlabDepth = 0.05f;
    private static readonly float s_agbCameraDistance = (AgbSlabHalfHeight / MathF.Tan(x: (25f * (MathF.PI / 180f))));

    private bool m_agbActive;
    private nint m_agbScreenHandle;
    private Vector3 m_agbScreenGlow;

    /// <summary>Whether the fullscreen native AGB scene is taking over this frame (the render node sets it each produced
    /// frame from the DI service's live state).</summary>
    public bool AgbDebugActive => m_agbActive;

    /// <summary>The AGB framebuffer's shader-readable view handle for this frame (0 when the scene is down) — the render
    /// node's screen-source table returns it for <see cref="AgbDebugScreenSlot"/>.</summary>
    public nint AgbDebugScreenHandle => m_agbScreenHandle;

    /// <summary>The room glow the AGB screen emits (zero when down) — the node's screen-light table returns it for
    /// <see cref="AgbDebugScreenSlot"/>.</summary>
    public Vector3 AgbDebugScreenGlow => m_agbScreenGlow;

    /// <summary>Pushes this frame's native AGB scene state (active + the live framebuffer handle + glow) from the render
    /// node, which owns the machine's per-frame step + GPU upload. A change in the active flag forces a program rebuild
    /// (the fullscreen slab replaces the room, and the room must return on exit) — exactly as a boot does.</summary>
    /// <param name="active">Whether the AGB scene is active this frame.</param>
    /// <param name="handle">The live framebuffer view handle (0 when inactive).</param>
    /// <param name="glow">The screen's room glow (zero when inactive).</param>
    public void SetAgbDebugScreen(bool active, nint handle, Vector3 glow) {
        if (m_agbActive != active) {
            m_agbActive = active;
            m_program = null; // enter/exit swaps the fullscreen slab for the room (and back) — rebuild like a boot
        }

        m_agbScreenHandle = handle;
        m_agbScreenGlow = glow;
    }

    // The head-on framing of the fullscreen AGB slab (sprite intent = +Z, zero pitch), at the distance that fills the
    // vertical FoV. Null when the scene is down (the CreatorCameraSource chain then falls through to the other modes).
    private (Vector3 Target, float Yaw, float Pitch, float Distance, bool Sprite)? AgbDebugCameraFrame() =>
        (m_agbActive ? (Vector3.Zero, 0f, 0f, s_agbCameraDistance, true) : ((Vector3 Target, float Yaw, float Pitch, float Distance, bool Sprite)?)null);

    // The AGB takeover program: ONE fullscreen diegetic slab at the origin, its front face (+Z, toward the head-on
    // camera) sampling the AGB framebuffer through AgbDebugScreenSlot. A strict subset of the room's worst-case
    // envelope (one slab << the full room), so the frozen capacity probe already covers it — no probe addition needed.
    private static SdfProgram BuildAgbDebugProgram() {
        var builder = new SdfProgramBuilder();
        var halfExtents = new Vector3((AgbSlabHalfHeight * AgbSlabAspect), AgbSlabHalfHeight, AgbSlabDepth);

        _ = builder.ResetPoint().ScreenSlab(
            halfExtents: halfExtents,
            round: 0.02f,
            worldOrigin: new Vector3(0f, 0f, AgbSlabDepth),
            worldRight: Vector3.UnitX,
            worldUp: Vector3.UnitY,
            screenIndex: AgbDebugScreenSlot
        );

        return builder.Build();
    }
}
