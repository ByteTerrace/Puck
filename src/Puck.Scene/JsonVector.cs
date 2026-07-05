using System.Numerics;

namespace Puck.Scene;

/// <summary>
/// Converts the JSON document's coordinate arrays (e.g. <c>[x, y, z]</c>) into the engine's <see cref="Vector3"/> and
/// <see cref="Quaternion"/> values. The component count is asserted by
/// <c>RunDocumentValidator</c> BEFORE any build runs, so the conversions here are total: a build only ever sees
/// already-validated arrays. The duplicated guards are a defensive backstop, not the primary gate.
/// </summary>
internal static class JsonVector {
    public static Vector2 ToVector2(IReadOnlyList<float> components) {
        Require(components: components, length: 2);

        return new Vector2(
            x: components[0],
            y: components[1]
        );
    }
    public static Vector3 ToVector3(IReadOnlyList<float> components) {
        Require(components: components, length: 3);

        return new Vector3(
            x: components[0],
            y: components[1],
            z: components[2]
        );
    }
    public static Quaternion ToQuaternion(IReadOnlyList<float> components) {
        Require(components: components, length: 4);

        return new Quaternion(
            w: components[3],
            x: components[0],
            y: components[1],
            z: components[2]
        );
    }

    /// <summary>Whether <paramref name="components"/> is non-null with exactly <paramref name="length"/> finite entries.</summary>
    public static bool IsValid(IReadOnlyList<float>? components, int length) {
        if ((components is null) || (components.Count != length)) {
            return false;
        }

        for (var index = 0; (index < length); index++) {
            if (!float.IsFinite(f: components[index])) {
                return false;
            }
        }

        return true;
    }

    private static void Require(IReadOnlyList<float> components, int length) {
        if (!IsValid(components: components, length: length)) {
            throw new ArgumentException(message: $"Expected {length} finite components.", paramName: nameof(components));
        }
    }
}
