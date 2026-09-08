using System.Numerics;
using Puck.SdfVm.Views;

namespace Puck.World.Client;

/// <summary>Pure camera math shared by the seat-owned local and traveling view paths.</summary>
internal static class WorldSeatCameraResolver {
    /// <summary>The shortest arc carrying <paramref name="from"/> to <paramref name="to"/>, both unit vectors.</summary>
    /// <remarks>Used to TRANSPORT a held rotation by a small step, where the two are near each other and the arc is
    /// always well conditioned — never to rebuild one from a fixed reference, which is exactly the case that has no
    /// answer when the two are opposite.</remarks>
    /// <param name="from">The unit axis to carry from.</param>
    /// <param name="to">The unit axis to carry to.</param>
    /// <returns>The carrying rotation.</returns>
    public static Quaternion ShortestArc(Vector3 from, Vector3 to) {
        var dot = Vector3.Dot(
            vector1: from,
            vector2: to
        );

        if (dot >= (1f - 1e-9f)) {
            return Quaternion.Identity;
        }

        if (dot <= (-1f + 1e-9f)) {
            var fallback = Vector3.Cross(
                vector1: from,
                vector2: Vector3.UnitY
            );

            if (fallback.LengthSquared() <= 1e-12f) {
                fallback = Vector3.Cross(
                    vector1: from,
                    vector2: Vector3.UnitX
                );
            }

            fallback = Vector3.Normalize(value: fallback);

            return new Quaternion(
                w: 0f,
                x: fallback.X,
                y: fallback.Y,
                z: fallback.Z
            );
        }

        var axis = Vector3.Cross(
            vector1: from,
            vector2: to
        );

        return Quaternion.Normalize(value: new Quaternion(
            w: (1f + dot),
            x: axis.X,
            y: axis.Y,
            z: axis.Z
        ));
    }
    /// <summary>The shortest arc carrying world up to <paramref name="up"/>, or identity when they already agree.</summary>
    /// <remarks>Both the seat camera's boom and the movement composition ride this, so what the player pushes and
    /// what the player sees are laid onto the surface by the SAME rotation and cannot disagree.</remarks>
    /// <param name="up">A unit up axis.</param>
    /// <returns>The aligning rotation.</returns>
    public static Quaternion AlignUp(Vector3 up) {
        var dot = up.Y;

        if (dot >= (1f - 1e-6f)) {
            return Quaternion.Identity;
        }

        if (dot <= (-1f + 1e-6f)) {
            // Exactly opposite: the arc has no defined axis, so name one rather than let rounding choose.
            return new Quaternion(w: 0f, x: 1f, y: 0f, z: 0f);
        }

        var axis = Vector3.Cross(
            vector1: Vector3.UnitY,
            vector2: up
        );

        return Quaternion.Normalize(value: new Quaternion(
            w: (1f + dot),
            x: axis.X,
            y: axis.Y,
            z: axis.Z
        ));
    }
    public static float BodyYaw(Quaternion orientation) {
        var behind = Vector3.Transform(
            value: Vector3.UnitZ,
            rotation: orientation
        );

        return MathF.Atan2(
            x: behind.Z,
            y: behind.X
        );
    }
    /// <summary>The look sample a joined seat's compiled chase rig folds into its authored orbit: the seat's own live
    /// yaw/pitch offset, plus the body's heading when the world declares a body-relative yaw reference.</summary>
    /// <param name="yawReference">The world's authored yaw reference.</param>
    /// <param name="bodyOrientation">The perceived body's orientation.</param>
    /// <param name="liveYaw">The seat's live yaw offset, radians.</param>
    /// <param name="livePitch">The seat's live pitch offset, radians.</param>
    /// <returns>The composed look sample.</returns>
    public static SdfCameraLook Look(WorldSeatYawReference yawReference, Quaternion bodyOrientation, float liveYaw, float livePitch) => new(
        Pitch: livePitch,
        Yaw: (liveYaw + ((yawReference == WorldSeatYawReference.World)
            ? 0f
            : BodyYaw(orientation: bodyOrientation)))
    );
}
