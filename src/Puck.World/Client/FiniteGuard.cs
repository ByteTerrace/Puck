using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.World.Client;

/// <summary>The editor state-setter finite guard (UIE-2): the console twins reject non-finite input at parse time,
/// and these throw at the public setters so a non-console caller can never poison camera, snap, or preview state
/// (a NaN center rebuilds the SDF program; a NaN pitch slides past ordinary range clamps).</summary>
internal static class FiniteGuard {
    /// <summary>Throws when <paramref name="value"/> is NaN or infinite.</summary>
    /// <param name="value">The value to check.</param>
    /// <param name="name">The caller argument expression (auto-captured).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is not finite.</exception>
    public static void ThrowIfNonFinite(float value, [CallerArgumentExpression(parameterName: nameof(value))] string? name = null) {
        if (!float.IsFinite(f: value)) {
            throw new ArgumentOutOfRangeException(paramName: name, actualValue: value, message: "The value must be finite.");
        }
    }

    /// <summary>Throws when any component of <paramref name="value"/> is NaN or infinite.</summary>
    /// <param name="value">The vector to check.</param>
    /// <param name="name">The caller argument expression (auto-captured).</param>
    /// <exception cref="ArgumentOutOfRangeException">A component is not finite.</exception>
    public static void ThrowIfNonFinite(Vector3 value, [CallerArgumentExpression(parameterName: nameof(value))] string? name = null) {
        if (!float.IsFinite(f: value.X) || !float.IsFinite(f: value.Y) || !float.IsFinite(f: value.Z)) {
            throw new ArgumentOutOfRangeException(paramName: name, actualValue: value, message: "Every component must be finite.");
        }
    }
}
