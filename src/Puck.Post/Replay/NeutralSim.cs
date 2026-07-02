using Puck.Maths;

namespace Puck.Post;

/// <summary>One tick's projected input for the <see cref="NeutralSim"/>: a 2D move axis and a jump press edge.</summary>
/// <param name="MoveX">The horizontal move axis.</param>
/// <param name="MoveY">The vertical move axis.</param>
/// <param name="JumpPressed">Whether a jump press edge occurred this tick.</param>
internal readonly record struct NeutralIntent(FixedQ4816 MoveX, FixedQ4816 MoveY, bool JumpPressed);

/// <summary>
/// A minimal, deterministic fixed-point simulation used purely as a NEUTRAL vehicle for the Tier-A determinism/replay
/// proof. It stands in for the (deferred) game world so the POST proves the engine's command → snapshot → record →
/// replay seam without depending on any game code. One entity integrates a 2D move axis and a one-shot vertical jump
/// impulse in <see cref="FixedQ4816"/> fixed point and advances a small LCG each tick; <see cref="StateHash"/> folds
/// the whole state into a stable per-tick digest, so two runs of the same input must hash identically.
/// </summary>
internal sealed class NeutralSim {
    private static readonly FixedQ4816 Friction = FixedQ4816.FromDouble(value: 0.88d);
    private static readonly FixedQ4816 JumpImpulse = FixedQ4816.FromDouble(value: 6d);
    private static readonly FixedQ4816 MoveAcceleration = FixedQ4816.FromDouble(value: 24d);

    private readonly FixedQ4816 m_tickSeconds;
    private FixedVector2 m_position;
    private uint m_rng;
    private ulong m_tick;
    private FixedVector2 m_velocity;

    /// <summary>Initializes a new instance of the <see cref="NeutralSim"/> class.</summary>
    /// <param name="seed">The simulation seed (folded into the state hash, so the seed provably matters).</param>
    /// <param name="tickSeconds">The fixed-step duration of one tick, in seconds.</param>
    public NeutralSim(uint seed, FixedQ4816 tickSeconds) {
        m_rng = seed;
        m_tickSeconds = tickSeconds;
    }

    /// <summary>Advances the simulation by one fixed step under the tick's intent.</summary>
    /// <param name="intent">The tick's projected input.</param>
    public void Advance(in NeutralIntent intent) {
        // Accelerate from the move axis, add a one-shot vertical impulse on a jump press, damp with friction, integrate.
        m_velocity += (new FixedVector2(X: intent.MoveX, Y: intent.MoveY) * (MoveAcceleration * m_tickSeconds));

        if (intent.JumpPressed) {
            m_velocity = new FixedVector2(X: m_velocity.X, Y: (m_velocity.Y + JumpImpulse));
        }

        m_velocity *= Friction;
        m_position += (m_velocity * m_tickSeconds);
        m_rng = unchecked((m_rng * 1664525u) + 1013904223u);
        ++m_tick;
    }

    /// <summary>Folds the full simulation state into a stable 64-bit digest.</summary>
    /// <returns>The state hash.</returns>
    public ulong StateHash() {
        var hash = Fnv1aHash.Create();

        hash.Add(value: m_tick);
        hash.Add(value: m_rng);
        hash.Add(value: m_position.X.Value);
        hash.Add(value: m_position.Y.Value);
        hash.Add(value: m_velocity.X.Value);
        hash.Add(value: m_velocity.Y.Value);

        return hash.Value;
    }
}
