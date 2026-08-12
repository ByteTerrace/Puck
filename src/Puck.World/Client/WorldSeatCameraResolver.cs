using System.Numerics;
using Puck.SdfVm.Views;

namespace Puck.World.Client;

/// <summary>Pure camera math shared by the seat-owned local and traveling view paths.</summary>
internal static class WorldSeatCameraResolver {
    internal sealed class LiveOrbitCache {
        public ISdfCameraRig? Rig;
        public WorldCameraMotion.Orbit? Authored;
        public float Yaw;
        public float Pitch;
    }

    internal sealed class SmoothingState {
        public Vector3 Boom;
        public bool Seeded;
    }

    public static float BodyYaw(Quaternion orientation) {
        var behind = Vector3.Transform(value: Vector3.UnitZ, rotation: orientation);
        return MathF.Atan2(y: behind.X, x: behind.Z);
    }

    public static ISdfCameraRig ResolveChase(WorldCameraRig authoredRig, ISdfCameraRig compiledChase,
        WorldSeatYawReference yawReference, Quaternion bodyOrientation, float liveYaw, float livePitch,
        LiveOrbitCache cache) {
        if (authoredRig.Motion is not WorldCameraMotion.Orbit orbit) {
            return compiledChase;
        }

        var yaw = orbit.Yaw + liveYaw + ((yawReference == WorldSeatYawReference.World)
            ? 0f
            : BodyYaw(orientation: bodyOrientation));
        var pitch = orbit.Pitch + livePitch;

        if ((cache.Rig is { } cached) && ReferenceEquals(objA: cache.Authored, objB: orbit)
            && (cache.Yaw == yaw) && (cache.Pitch == pitch)) {
            return cached;
        }

        cache.Authored = orbit;
        cache.Yaw = yaw;
        cache.Pitch = pitch;
        cache.Rig = WorldCameraRigCompiler.Compile(rig: authoredRig with {
            Motion = orbit with { Yaw = yaw, Pitch = pitch },
        });
        return cache.Rig;
    }

    public static void Smooth(SmoothingState state, float smoothRate, bool isPlainChase, float deltaSeconds,
        ref Vector3 eye, ref Vector3 target) {
        if (!isPlainChase || (smoothRate <= 0f)) {
            state.Seeded = false;
            return;
        }

        if (!state.Seeded) {
            state.Boom = (eye - target);
            state.Seeded = true;
        } else {
            var alpha = 1f - MathF.Exp(x: -smoothRate * MathF.Max(x: deltaSeconds, y: 0f));
            state.Boom = Vector3.Lerp(value1: state.Boom, value2: (eye - target), amount: alpha);
        }

        // Smooth only the authored orbit boom. The subject's translation is already the continuous render pose and
        // must remain exact: smoothing absolute eye/target coordinates made a fast-rising player leave the camera
        // physically below them (and made every authority handoff look like camera lag despite a continuous anchor).
        // Keeping target live while easing its relative boom preserves authored orbit smoothing without inventing
        // a second, delayed player trajectory in presentation.
        eye = (target + state.Boom);
    }
}
