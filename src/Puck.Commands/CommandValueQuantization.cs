using System.Numerics;
using Puck.Maths;

namespace Puck.Commands;

/// <summary>
/// The single conversion authority for turning a continuous <see cref="CommandValue"/> axis sample into fixed
/// point, once, at the router seam where a physical float first becomes a command value. A future door quantizing
/// a continuous axis calls this rather than re-deriving its own <see cref="FixedQ4816.FromDouble(double)"/> call, so
/// the rounding rule (nearest, ties to even) has exactly one definition site.
/// </summary>
/// <remarks>
/// The fence this type enforces is INTERIM and POSITIONAL: today it is enforced by every door calling here rather
/// than by a type that makes a float impossible to hold below the door. Unit 6's command-channel retrofit (the
/// typed router-lane split) supersedes this with a STRUCTURAL fence; until then, this is the reminder that the
/// positional fence is not the end state.
/// </remarks>
public static class CommandValueQuantization {
    /// <summary>Quantizes a two-dimensional axis sample to fixed point, componentwise, once. One of exactly two
    /// call sites today (the move and look stick routers); a third door reuses this rather than re-deriving the
    /// conversion.</summary>
    /// <param name="value">The physical axis sample, each component conventionally in <c>[-1, 1]</c>.</param>
    /// <returns>The componentwise nearest, ties-to-even <see cref="FixedVector2"/>.</returns>
    public static FixedVector2 QuantizeAxis(Vector2 value) {
        return new FixedVector2(
            X: FixedQ4816.FromDouble(value: value.X),
            Y: FixedQ4816.FromDouble(value: value.Y)
        );
    }
}
