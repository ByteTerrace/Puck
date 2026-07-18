namespace Puck.Abstractions.Gpu;

/// <summary>
/// The LIVE GPU-timing arming control — the <c>PresentPacingControl</c> idiom for per-pass GPU
/// timestamp capture. A small mutable holder the render engine reads per frame: an ARMED control writes the per-pass
/// timing marks (and the launcher publishes its CPU frame buckets), a DISARMED one skips the timestamp writes and
/// reads entirely at near-zero cost, so timing can be turned on and off mid-session with no restart. This is a
/// PRESENTATION/diagnostic concern only — the fixed-step simulation is never touched, so arming or disarming timing
/// never affects determinism.
/// <para>
/// The <see cref="Shared"/> instance is the single arming authority every scattered timing toggle collapsed onto.
/// Precedence, highest first: a PROGRAMMATIC <see cref="SetArmed"/> (the bench harness arms it at suite start and
/// restores the prior state at suite end, and the LIVE console switches — the demo's gpu.timing / Puck.World's
/// world.timing — flip it mid-session) &gt; the run document's <c>host.timing</c> field (seeded at composition) &gt;
/// the engine node's construction seed (the lowest tier — a <see cref="TrySeed"/> that claims the control only when
/// nothing above has). A monotonically increasing <see cref="Version"/> — bumped whenever the armed state actually
/// changes — lets a reader re-resolve derived state only on an actual change.
/// </para>
/// </summary>
public sealed class GpuTimingControl {
    private uint m_armed;
    private uint m_seeded;
    private uint m_version;

    /// <summary>The process-wide arming authority (see the type remarks).</summary>
    public static GpuTimingControl Shared { get; } = new();

    /// <summary>Whether GPU timing is currently armed.</summary>
    public bool Armed => (Volatile.Read(location: ref m_armed) != 0U);
    /// <summary>A monotonically increasing counter bumped whenever the armed state changes, so a reader re-resolves
    /// derived state only on an actual change.</summary>
    public uint Version => Volatile.Read(location: ref m_version);

    /// <summary>Sets the armed state PROGRAMMATICALLY — the highest-precedence source (the bench harness). Also claims
    /// the control so a later <see cref="TrySeed"/> (a lower-precedence composition/env seed) is a no-op. A no-op (no
    /// version bump) when the value is unchanged.</summary>
    /// <param name="armed">Whether to arm GPU timing.</param>
    public void SetArmed(bool armed) {
        Volatile.Write(location: ref m_seeded, value: 1U);

        var desired = (armed ? 1U : 0U);

        if (Volatile.Read(location: ref m_armed) == desired) {
            return;
        }

        Volatile.Write(location: ref m_armed, value: desired);
        Volatile.Write(location: ref m_version, value: (Volatile.Read(location: ref m_version) + 1U));
    }

    /// <summary>Seeds the armed state from a lower-precedence source (run document <c>host.timing</c>, then the engine
    /// node's construction seed) — applied ONLY if nothing has claimed the control yet, so the FIRST seed in precedence
    /// order wins and a programmatic <see cref="SetArmed"/> always overrides. Idempotent and thread-safe: every seed
    /// site may call it, the first claim sticks.</summary>
    /// <param name="armed">The armed state this source requests.</param>
    /// <returns><see langword="true"/> when this call claimed the control; <see langword="false"/> when a
    /// higher-or-equal-precedence source already spoke.</returns>
    public bool TrySeed(bool armed) {
        if (Interlocked.CompareExchange(location1: ref m_seeded, value: 1U, comparand: 0U) != 0U) {
            return false;
        }

        if (armed) {
            Volatile.Write(location: ref m_armed, value: 1U);
            Volatile.Write(location: ref m_version, value: (Volatile.Read(location: ref m_version) + 1U));
        }

        return true;
    }
}
