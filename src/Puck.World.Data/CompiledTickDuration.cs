namespace Puck.World;

/// <summary>
/// A compiled simulation-tick duration, or NEVER — the rate-0 "never" case a raw tick count cannot carry without a
/// caller-side convention. At a positive simulation rate every authored duration compiles to a finite, non-negative
/// tick count (see <see cref="WorldSimulationTickConversion.DurationTicks(float, uint)"/>), including exactly zero
/// for an authored-DISABLED duration (e.g. <c>population.reconnectGraceSeconds == 0</c> disables the reconnect
/// grace window outright — a real, distinct meaning from NEVER, not the same zero read two ways depending on who is
/// asking). At <see cref="WorldSimulationDefaults.RateHz"/> 0 — a resident, non-stepping world — a duration compiled
/// from a POSITIVE authored seconds value has no tick mapping at all: it NEVER elapses, which is neither zero nor
/// "already expired".
/// <para>A raw <see langword="int"/> cannot hold both "zero, disabled" and "unreachable" without every call site
/// re-deriving which one a particular zero means — exactly the dominant defect class this codebase names:
/// an invariant held by convention rather than refused at the type. This type makes the distinction explicit:
/// there is no tick value that means NEVER, so a consumer MUST branch on <see cref="IsNever"/> before it can read
/// <see cref="Ticks"/> at all.</para>
/// </summary>
public readonly struct CompiledTickDuration : IEquatable<CompiledTickDuration> {
    private readonly int m_ticks;
    private readonly bool m_isNever;

    private CompiledTickDuration(int ticks, bool isNever) {
        m_ticks = ticks;
        m_isNever = isNever;
    }

    /// <summary>The permanently-unreachable duration — a positive authored duration compiled against simulation
    /// rate 0, which has no tick mapping to compile to.</summary>
    public static readonly CompiledTickDuration Never = new(ticks: 0, isNever: true);

    /// <summary>Wraps a finite, non-negative simulation-tick count — the ordinary positive-rate compiled shape, or
    /// an authored-DISABLED zero (a real value, distinct from <see cref="Never"/>).</summary>
    /// <param name="ticks">The compiled tick count. Must not be negative.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ticks"/> is negative.</exception>
    public static CompiledTickDuration FromTicks(int ticks) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: ticks);

        return new CompiledTickDuration(ticks: ticks, isNever: false);
    }

    /// <summary>Whether this duration never elapses (see this type's own remarks). When <see langword="true"/>,
    /// <see cref="Ticks"/> throws — there is no numeric stand-in for NEVER.</summary>
    public bool IsNever => m_isNever;

    /// <summary>Whether this duration is a compiled, authored-DISABLED zero — distinct from <see cref="IsNever"/>.
    /// <see langword="false"/> for <see cref="Never"/>, exactly like every other finite tick count.</summary>
    public bool IsZero => (!m_isNever && (m_ticks == 0));

    /// <summary>The compiled tick count. Every caller must check <see cref="IsNever"/> first — there is no sentinel
    /// tick value that would let a caller skip the branch and read a plausible-but-wrong number instead.</summary>
    /// <exception cref="InvalidOperationException">This duration <see cref="IsNever"/>.</exception>
    public int Ticks => (m_isNever
        ? throw new InvalidOperationException(message: "CompiledTickDuration.Never has no tick count — check IsNever before reading Ticks.")
        : m_ticks);

    /// <inheritdoc/>
    public bool Equals(CompiledTickDuration other) => (m_isNever ? other.m_isNever : (!other.m_isNever && (m_ticks == other.m_ticks)));

    /// <inheritdoc/>
    public override bool Equals(object? obj) => ((obj is CompiledTickDuration other) && Equals(other: other));

    /// <inheritdoc/>
    public override int GetHashCode() => (m_isNever ? -1 : m_ticks);

    /// <inheritdoc/>
    public override string ToString() => (m_isNever ? "never" : $"{m_ticks} ticks");

    /// <summary>Equality operator — see <see cref="Equals(CompiledTickDuration)"/>.</summary>
    public static bool operator ==(CompiledTickDuration left, CompiledTickDuration right) => left.Equals(other: right);

    /// <summary>Inequality operator — see <see cref="Equals(CompiledTickDuration)"/>.</summary>
    public static bool operator !=(CompiledTickDuration left, CompiledTickDuration right) => !left.Equals(other: right);
}
