using System.Buffers.Binary;
using System.Numerics;

namespace Puck.Input;

/// <summary>Provides pure normalization and decoding operations shared by gamepad drivers.</summary>
public static class GamepadNormalization {
    /// <summary>Applies a radial deadzone and rescales the remaining unit-disc magnitude to zero through one.</summary>
    /// <param name="stick">The centered stick vector.</param>
    /// <param name="deadzone">The radial deadzone in normalized units, from zero inclusive to one exclusive.</param>
    /// <returns>The deadzone-normalized stick vector.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="deadzone"/> is not finite or is outside the range zero inclusive to one exclusive.</exception>
    public static Vector2 ApplyRadialDeadzone(Vector2 stick, float deadzone) {
        if (!float.IsFinite(f: deadzone) || (deadzone < 0f) || (deadzone >= 1f)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(deadzone), actualValue: deadzone, message: "The deadzone must be finite and in the range [0, 1).");
        }

        var magnitude = stick.Length();

        if (magnitude <= deadzone) {
            return Vector2.Zero;
        }

        var scaled = ((MathF.Min(x: magnitude, y: 1f) - deadzone) / (1f - deadzone));

        return ((stick / magnitude) * scaled);
    }

    /// <summary>Maps a linear trigger value below a threshold to zero and rescales the remaining range.</summary>
    /// <param name="raw">The raw trigger value.</param>
    /// <param name="threshold">The inclusive resting threshold.</param>
    /// <param name="range">The raw value representing a full pull.</param>
    /// <returns>The normalized trigger value.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="raw"/>, <paramref name="threshold"/>, or <paramref name="range"/> is not finite, or <paramref name="range"/> is not greater than <paramref name="threshold"/>.</exception>
    public static float NormalizeTrigger(float raw, float threshold, float range) {
        if (!float.IsFinite(f: raw)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(raw), actualValue: raw, message: "The raw trigger value must be finite.");
        }

        if (!float.IsFinite(f: threshold)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(threshold), actualValue: threshold, message: "The trigger threshold must be finite.");
        }

        if (!float.IsFinite(f: range) || (range <= threshold)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(range), actualValue: range, message: "The trigger range must be finite and greater than the threshold.");
        }

        return ((raw <= threshold) ? 0f : Math.Clamp(value: ((raw - threshold) / (range - threshold)), min: 0f, max: 1f));
    }

    /// <summary>Decodes three consecutive little-endian signed 16-bit axes and applies a scale.</summary>
    /// <param name="source">The source report.</param>
    /// <param name="offset">The first axis offset.</param>
    /// <param name="scale">The scale applied to every axis.</param>
    /// <returns>The decoded vector.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is negative or <paramref name="scale"/> is not finite.</exception>
    /// <exception cref="ArgumentException"><paramref name="source"/> does not contain six bytes beginning at <paramref name="offset"/>.</exception>
    public static Vector3 ReadVector3Int16(ReadOnlySpan<byte> source, int offset, float scale) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: offset);

        if (!float.IsFinite(f: scale)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(scale), actualValue: scale, message: "The vector scale must be finite.");
        }

        if ((source.Length - offset) < 6) {
            throw new ArgumentException(message: "The source does not contain a complete three-axis Int16 vector at the requested offset.", paramName: nameof(source));
        }

        return new Vector3(
            x: (BinaryPrimitives.ReadInt16LittleEndian(source: source[offset..]) * scale),
            y: (BinaryPrimitives.ReadInt16LittleEndian(source: source[(offset + 2)..]) * scale),
            z: (BinaryPrimitives.ReadInt16LittleEndian(source: source[(offset + 4)..]) * scale)
        );
    }
}
