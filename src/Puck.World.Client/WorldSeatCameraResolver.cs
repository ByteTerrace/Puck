using System.Numerics;

namespace Puck.World.Client;

/// <summary>Pure camera math shared by the seat-owned local and traveling view paths.</summary>
internal static class WorldSeatCameraResolver {
    internal sealed class LiveOrbitCache {
        public WorldCameraProgramOp.Orbit? Authored;
        public float Pitch;
        public IWorldCameraProgramRig? Rig;
        public float Yaw;
    }
    internal sealed class SmoothingState {
        public Vector3 Boom;
        public bool Seeded;
    }

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
    public static IWorldCameraProgramRig ResolveChase(WorldCameraProgram authoredRig, IWorldCameraProgramRig compiledChase,
        WorldSeatYawReference yawReference, Quaternion bodyOrientation, WorldDefinition definition, float liveYaw, float livePitch,
        LiveOrbitCache cache) {
        if (authoredRig.OrbitOp is not { } orbit) {
            return compiledChase;
        }

        var yaw = ((orbit.Yaw + liveYaw) + ((yawReference == WorldSeatYawReference.World)
            ? 0f
            : BodyYaw(orientation: bodyOrientation)));
        var pitch = (orbit.Pitch + livePitch);

        if (
            (cache.Rig is { } cached) &&
            ReferenceEquals(
            objA: cache.Authored,
            objB: orbit
        ) &&
            (cache.Yaw == yaw) &&
            (cache.Pitch == pitch)
        ) {
            return cached;
        }

        cache.Authored = orbit;
        cache.Yaw = yaw;
        cache.Pitch = pitch;

        var bakedOrbit = (orbit with { Yaw = yaw, Pitch = pitch });
        var operations = new List<WorldCameraProgramOp>(capacity: authoredRig.Operations.Count);

        foreach (var op in authoredRig.Operations) {
            operations.Add(item: (ReferenceEquals(
                objA: op,
                objB: orbit
            )
                ? bakedOrbit
                : op));
        }

        cache.Rig = WorldCameraRigCompiler.Compile(
            definition: definition,
            program: (authoredRig with { Operations = operations })
        );

        return cache.Rig;
    }
    public static void Smooth(SmoothingState state, float smoothRate, bool isPlainChase, float deltaSeconds,
        ref Vector3 eye, ref Vector3 target) {
        if (
            !isPlainChase ||
            (smoothRate <= 0f)
        ) {
            state.Seeded = false;
            return;
        }

        if (!state.Seeded) {
            state.Boom = (eye - target);
            state.Seeded = true;
        } else {
            var alpha = (1f - MathF.Exp(x: (-smoothRate * MathF.Max(
                x: deltaSeconds,
                y: 0f
            ))));

            state.Boom = Vector3.Lerp(
                amount: alpha,
                value1: state.Boom,
                value2: (eye - target)
            );
        }

        // Smooth only the authored orbit boom. The subject's translation is already the continuous render pose and
        // must remain exact: smoothing absolute eye/target coordinates made a fast-rising player leave the camera
        // physically below them (and made every authority handoff look like camera lag despite a continuous anchor).
        // Keeping target live while easing its relative boom preserves authored orbit smoothing without inventing
        // a second, delayed player trajectory in presentation.
        eye = (target + state.Boom);
    }
}
