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
/// The fence this type enforces is interim and positional: today it is enforced by every door calling here rather
/// than by a type that makes a float impossible to hold below the door. A future command-channel retrofit (the
/// typed router-lane split) supersedes this with a structural fence; until then, this is the reminder that the
/// positional fence is not the end state.
/// </remarks>
public static class CommandValueQuantization {
    /// <summary>Quantizes a two-dimensional axis sample to fixed point, componentwise, once — the door every stick
    /// router (move, look) takes rather than re-deriving the conversion.</summary>
    /// <param name="value">The physical axis sample, each component conventionally in <c>[-1, 1]</c>.</param>
    /// <returns>The componentwise nearest, ties-to-even <see cref="FixedVector2"/>.</returns>
    public static FixedVector2 QuantizeAxis(Vector2 value) {
        return new FixedVector2(
            X: FixedQ4816.FromDouble(value: value.X),
            Y: FixedQ4816.FromDouble(value: value.Y)
        );
    }
    /// <summary>Quantizes a three-dimensional axis sample to fixed point, componentwise, once — the door a source
    /// pointer's ray takes into the simulation (<see cref="SourcePointerCommands"/>).</summary>
    /// <param name="value">The axis sample, such as a world-space ray origin or direction.</param>
    /// <returns>The componentwise nearest, ties-to-even <see cref="FixedVector3"/>, through
    /// <see cref="FixedVector3.FromVector3"/>.</returns>
    public static FixedVector3 QuantizeAxis3D(Vector3 value) => FixedVector3.FromVector3(value: value);
}
