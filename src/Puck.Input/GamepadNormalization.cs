using System.Buffers.Binary;
using System.Numerics;

namespace Puck.Input;

/// <summary>Provides pure normalization and decoding operations shared by gamepad drivers.</summary>
public static class GamepadNormalization {
    /// <summary>Applies a radial deadzone and rescales the remaining unit-disc magnitude to zero through one.</summary>
    /// <param name="stick">The centered stick vector.</param>
    /// <param name="deadzone">The radial deadzone in normalized units.</param>
    /// <returns>The deadzone-normalized stick vector.</returns>
    public static Vector2 ApplyRadialDeadzone(Vector2 stick, float deadzone) {
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
    public static float NormalizeTrigger(float raw, float threshold, float range) =>
        ((raw <= threshold) ? 0f : MathF.Min(x: ((raw - threshold) / (range - threshold)), y: 1f));

    /// <summary>Decodes three consecutive little-endian signed 16-bit axes and applies a scale.</summary>
    /// <param name="source">The source report.</param>
    /// <param name="offset">The first axis offset.</param>
    /// <param name="scale">The scale applied to every axis.</param>
    /// <returns>The decoded vector.</returns>
    public static Vector3 ReadVector3Int16(ReadOnlySpan<byte> source, int offset, float scale) =>
        new(
            x: (BinaryPrimitives.ReadInt16LittleEndian(source: source[offset..]) * scale),
            y: (BinaryPrimitives.ReadInt16LittleEndian(source: source[(offset + 2)..]) * scale),
            z: (BinaryPrimitives.ReadInt16LittleEndian(source: source[(offset + 4)..]) * scale)
        );
}
