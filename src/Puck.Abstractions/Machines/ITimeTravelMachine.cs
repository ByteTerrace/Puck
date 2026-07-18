namespace Puck.Abstractions.Machines;

/// <summary>A one-instant read of a machine's time-travel state — the depth and footprint of the rewind ring plus the
/// live runahead and fast-forward settings. Presentation/diagnostic only: none of it is simulation state, so reading it
/// can never perturb the emulated timeline.</summary>
/// <param name="RewindEnabled">Whether the rewind ring is armed and capturing history.</param>
/// <param name="DepthFrames">The number of captured frames currently held in the ring.</param>
/// <param name="SegmentCount">The number of keyframe-anchored spans the ring holds.</param>
/// <param name="ByteFootprint">The ring's approximate memory footprint in bytes (keyframes plus reserved delta buffers).</param>
/// <param name="SpanSeconds">The captured history's approximate wall span in emulated seconds.</param>
/// <param name="RunaheadFrames">The number of frames the persistent lookahead is kept ahead, or 0 when runahead is off.</param>
/// <param name="RunaheadLeadFrames">The MEASURED native-frame lead of the live lookahead over the authoritative machine
/// — the lookahead's OWN native-frame index minus the authority's, read from the fork itself rather than a synthetic
/// counter. It holds at <paramref name="RunaheadFrames"/> within one native frame once primed (the host advances the
/// lookahead until its own index reaches the authority's plus N, so the lead never drifts under a mismatched host/native
/// cadence or fast-forward; it reads N+1 only in the instant an instruction's overshoot carries the boundary-reaching
/// frame one past the target, self-correcting the next submission); 0 when runahead is off or not yet primed.</param>
/// <param name="FastForwardFactor">The host-level cycle-budget multiplier (1 = realtime).</param>
public readonly record struct TimeTravelStatus(
    bool RewindEnabled,
    int DepthFrames,
    int SegmentCount,
    long ByteFootprint,
    double SpanSeconds,
    int RunaheadFrames,
    int RunaheadLeadFrames,
    int FastForwardFactor
);

/// <summary>
/// Optional time-travel capability for an <see cref="IScreenMachine"/> — machine-neutral rewind, runahead, and
/// fast-forward built once over the snapshot surface every deterministic core already exposes, mirroring
/// <see cref="IQueuedScreenMachine"/>'s and <see cref="IAudioMachine"/>'s optional-capability precedent. Rewind restores
/// the nearest keyframe and deterministically replays recorded input to land on any frame in a bounded ring; runahead
/// keeps one persistent lookahead fork advanced ahead of the authoritative machine on predicted input; fast-forward
/// multiplies the per-frame cycle budget with presentation frames skipped — a host-level knob, never a timing hack
/// inside the core. Every member is host-facing and single-producer: a machine that runs an internal worker marshals
/// these onto it, so rewind/runahead never manipulate machine state cross-thread.
/// </summary>
public interface ITimeTravelMachine {
    /// <summary>Arms or disarms the rewind ring. While armed, each stepped frame is captured (a keyframe every N frames,
    /// deltas between); disarming clears the captured history.</summary>
    /// <param name="enabled">Whether to capture rewind history.</param>
    void SetRewindEnabled(bool enabled);

    /// <summary>Rewinds the authoritative machine backward by up to <paramref name="frames"/> native frames, clamped to
    /// the oldest captured frame, then resumes forward from there. A no-op when the ring is empty or disarmed.</summary>
    /// <param name="frames">The number of native frames to move backward.</param>
    /// <returns>The number of native frames actually rewound (0 when nothing was captured).</returns>
    int RewindBy(int frames);

    /// <summary>Arms (or, with 0, disarms) runahead: a persistent lookahead fork kept <paramref name="frames"/> frames
    /// ahead of the authoritative machine on predicted input, whose framebuffer the machine presents while the real
    /// machine stays the tick-locked authority and the sole audio source.</summary>
    /// <param name="frames">The number of frames to run ahead (clamped), or 0 to disarm.</param>
    void SetRunahead(int frames);

    /// <summary>Sets the fast-forward factor — the per-presented-frame cycle-budget multiplier, clamped to at least 1.
    /// A factor of N advances the machine N times faster in emulated time while presenting one frame per submit.</summary>
    /// <param name="factor">The cycle-budget multiplier (1 = realtime).</param>
    void SetFastForward(int factor);

    /// <summary>Gets a one-instant read of the ring depth/footprint and the live runahead/fast-forward settings.</summary>
    TimeTravelStatus TimeTravelStatus { get; }
}
