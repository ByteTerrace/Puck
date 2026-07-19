using Puck.Maths;

namespace Puck.World.Audio;

/// <summary>Discriminates how an emitter occupies space: a <see cref="Point"/> pans by listener-relative azimuth
/// and attenuates over its radius band; a <see cref="Bed"/> is a region presence — center-panned, its gain an
/// envelope of the listener's distance from the extent center, its coefficient slew bounded by
/// <see cref="WorldAudioEmitter.FadeFrames"/>.</summary>
public enum WorldAudioEmitterKind {
    /// <summary>A positioned point source (a speaker, an emission facet, a creature voice).</summary>
    Point,
    /// <summary>An ambient region (wind in a valley, a creek's reach) — presence, not position.</summary>
    Bed,
}

/// <summary>Selects which channel of a stereo source an emitter taps. Mono sources (the synth) degenerate every
/// selector to <see cref="Mix"/> — documented, never rejected.</summary>
public enum WorldAudioChannel {
    /// <summary>The average of both channels.</summary>
    Mix,
    /// <summary>The left channel only.</summary>
    Left,
    /// <summary>The right channel only.</summary>
    Right,
}

/// <summary>The signal kinds an emitter can bind (sources are shared identities, never inline
/// payloads — two emitters naming the same source share ONE per-block pull).</summary>
public enum WorldAudioSourceKind {
    /// <summary>Honest silence — the emitter holds its place with no signal.</summary>
    None,
    /// <summary>A live screen-hosted machine, identified by screen slot.</summary>
    Machine,
    /// <summary>A tune asset played through a headless machine host, identified by tune id.</summary>
    Tune,
    /// <summary>The world voice synth, identified by patch id; voices arrive via <see cref="WorldSynthTrigger"/>.</summary>
    Synth,
}

/// <summary>One source identity — the key the mixer's per-block single-pull dedupes on and the seam live hosting
/// binds to. <see cref="Slot"/> carries machine identity; <see cref="Id"/> carries tune/patch identity;
/// the unused member is zero/null so value equality is exact.</summary>
/// <param name="Kind">The source kind.</param>
/// <param name="Slot">The screen slot for <see cref="WorldAudioSourceKind.Machine"/>; 0 otherwise.</param>
/// <param name="Id">The tune/patch id for <see cref="WorldAudioSourceKind.Tune"/>/<see cref="WorldAudioSourceKind.Synth"/>; null otherwise.</param>
public readonly record struct WorldAudioSourceKey(WorldAudioSourceKind Kind, int Slot, string? Id) {
    /// <summary>Creates a machine-source key.</summary>
    /// <param name="slot">The screen slot hosting the machine.</param>
    /// <returns>The key.</returns>
    public static WorldAudioSourceKey Machine(int slot) => new(Kind: WorldAudioSourceKind.Machine, Slot: slot, Id: null);
    /// <summary>Creates a tune-source key.</summary>
    /// <param name="id">The tune id.</param>
    /// <returns>The key.</returns>
    public static WorldAudioSourceKey Tune(string id) => new(Kind: WorldAudioSourceKind.Tune, Slot: 0, Id: id);
    /// <summary>Creates a synth-source key.</summary>
    /// <param name="patchId">The patch id voices on this emitter default to.</param>
    /// <returns>The key.</returns>
    public static WorldAudioSourceKey Synth(string patchId) => new(Kind: WorldAudioSourceKind.Synth, Slot: 0, Id: patchId);
    /// <summary>The silent source.</summary>
    public static WorldAudioSourceKey None => new(Kind: WorldAudioSourceKind.None, Slot: 0, Id: null);
}

/// <summary>The listener pose the mixer spatializes against: a world position plus a yaw rotor. The mixer's frame
/// convention: the ground plane is world X/Z with Y up; <paramref name="Yaw"/> rotates LISTENER-LOCAL (X = right,
/// Y = forward) into world (X, Z), so <c>Yaw.Conjugate().Rotate(worldDelta)</c> yields the local direction pan
/// derives azimuth from. Elevation is ignored for pan and included in distance.</summary>
/// <param name="Position">The listener's world position.</param>
/// <param name="Yaw">The unit rotor taking listener-local (right, forward) into world (X, Z).</param>
public readonly record struct WorldAudioListener(FixedVector3 Position, FixedComplex Yaw);

/// <summary>One row of the snapshot's emitter table — everything the mixer needs to spatialize one feed, already
/// resolved to world space (anchors resolve producer-side; the mixer never queries the world).</summary>
/// <param name="Id">The stable emitter id — the key the mixer's coefficient-ramp state carries across blocks. An
/// id that leaves the table drops its ramp state and re-enters from silence.</param>
/// <param name="Kind">Point or bed.</param>
/// <param name="Position">The world position (a bed's extent center).</param>
/// <param name="MinRadius">Full-gain support: inside this distance attenuation is 1 (a bed's inner radius).</param>
/// <param name="MaxRadius">The finite support edge: at or beyond this distance the emitter is CULLED — finite
/// support IS the cull. Must exceed <paramref name="MinRadius"/>.</param>
/// <param name="FadeFrames">Bed presence slew bound: the per-block coefficient change is limited to full scale per
/// this many frames (0 = unbounded). Points ignore it — the block ramp already bounds their slew.</param>
/// <param name="GainQ16">The base gain, Q16 (65536 = unity).</param>
/// <param name="Channel">The stereo channel this emitter taps from its source.</param>
/// <param name="Source">The signal identity.</param>
public readonly record struct WorldAudioEmitter(
    int Id,
    WorldAudioEmitterKind Kind,
    FixedVector3 Position,
    FixedQ4816 MinRadius,
    FixedQ4816 MaxRadius,
    int FadeFrames,
    int GainQ16,
    WorldAudioChannel Channel,
    WorldAudioSourceKey Source
);

/// <summary>One seeded synth trigger event. Sequence numbers make triggers once-only under snapshot hold: the
/// producer assigns strictly increasing values, the mixer fires only sequences above its high-water mark — a
/// snapshot mixed for several blocks (or a skipped snapshot) can never double- or drop-fire.</summary>
/// <param name="Sequence">The producer-assigned strictly increasing event number (start at 1).</param>
/// <param name="PatchId">The registered patch to voice.</param>
/// <param name="Seed">The noise seed — the same seed reproduces the voice bit for bit.</param>
/// <param name="GainQ16">The voice gain, Q16 (65536 = unity).</param>
/// <param name="EmitterId">The emitter the voice spatializes through (its source must be
/// <see cref="WorldAudioSourceKind.Synth"/>).</param>
public readonly record struct WorldSynthTrigger(ulong Sequence, string PatchId, ulong Seed, int GainQ16, int EmitterId);

/// <summary>
/// The immutable per-frame record the mixer consumes: listener pose, a fixed-capacity emitter table, and seeded
/// synth trigger events. Built for the <c>PublishBuffer&lt;WorldAudioSnapshot&gt;</c> slab-rotation contract:
/// the producer preallocates a rotation of ≥4 instances, fills one via <see cref="Reset"/> +
/// <see cref="TryAddEmitter"/>/<see cref="TryAddTrigger"/>, publishes it, and must not touch it again until the
/// rotation laps — consumers treat a published snapshot as immutable. Zero steady-state allocation: capacity is
/// fixed at construction and adds past it are refused (returning <see langword="false"/>), never grown.
/// <see cref="Client.WorldAudioDirector"/> is the live publisher; the offline proof scripts this builder directly.
/// </summary>
public sealed class WorldAudioSnapshot {
    /// <summary>The default emitter-table capacity.</summary>
    public const int DefaultMaxEmitters = 32;
    /// <summary>The default per-snapshot trigger capacity.</summary>
    public const int DefaultMaxTriggers = 16;

    private readonly WorldAudioEmitter[] m_emitters;
    private readonly WorldSynthTrigger[] m_triggers;
    private int m_emitterCount;
    private int m_triggerCount;

    /// <summary>Initializes an empty snapshot slab.</summary>
    /// <param name="maxEmitters">The emitter-table capacity.</param>
    /// <param name="maxTriggers">The trigger capacity.</param>
    public WorldAudioSnapshot(int maxEmitters = DefaultMaxEmitters, int maxTriggers = DefaultMaxTriggers) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: maxEmitters);
        ArgumentOutOfRangeException.ThrowIfNegative(value: maxTriggers);
        m_emitters = new WorldAudioEmitter[maxEmitters];
        m_triggers = new WorldSynthTrigger[maxTriggers];
    }

    /// <summary>Gets the listener pose.</summary>
    public WorldAudioListener Listener { get; private set; }
    /// <summary>Gets the emitter table.</summary>
    public ReadOnlySpan<WorldAudioEmitter> Emitters => m_emitters.AsSpan(start: 0, length: m_emitterCount);
    /// <summary>Gets the trigger events.</summary>
    public ReadOnlySpan<WorldSynthTrigger> Triggers => m_triggers.AsSpan(start: 0, length: m_triggerCount);

    /// <summary>Clears the tables and sets the listener — the start of one produce pass.</summary>
    /// <param name="listener">The listener pose for this frame.</param>
    public void Reset(in WorldAudioListener listener) {
        Listener = listener;
        m_emitterCount = 0;
        m_triggerCount = 0;
    }

    /// <summary>Appends an emitter row; refuses (never grows) past capacity.</summary>
    /// <param name="emitter">The row to append.</param>
    /// <returns><see langword="true"/> when the row was appended.</returns>
    public bool TryAddEmitter(in WorldAudioEmitter emitter) {
        if (m_emitterCount >= m_emitters.Length) {
            return false;
        }

        m_emitters[m_emitterCount++] = emitter;

        return true;
    }

    /// <summary>Appends a trigger event; refuses (never grows) past capacity.</summary>
    /// <param name="trigger">The event to append.</param>
    /// <returns><see langword="true"/> when the event was appended.</returns>
    public bool TryAddTrigger(in WorldSynthTrigger trigger) {
        if (m_triggerCount >= m_triggers.Length) {
            return false;
        }

        m_triggers[m_triggerCount++] = trigger;

        return true;
    }
}
