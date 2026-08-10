using System.Numerics;
using Puck.SdfVm.Views;

namespace Puck.World.Client;

/// <summary>
/// The seat-native chase/orbit camera resolution — the one place an authored <see cref="WorldCameraRig"/> composes a
/// seat's live pointer/stick orbit offset and the rig's own <see cref="WorldCameraRig.SmoothRate"/> ease, shared by every
/// caller that resolves a body through a world's own authored seat rig: <see cref="WorldFrameSource.ResolveCamera"/>
/// (a local seat rendering its own boot instance) and <see cref="AwaySeatSceneEmitter.ResolveChaseCamera"/> (a
/// traveling seat rendering a destination instance it crossed into). Both feed this the same shape — an authored
/// rig, a resolved control feel (see <see cref="ResolveSeatLook"/> for how that feel itself is assembled), a body
/// pose, and a live orbit offset — so a destination frames identically whether the seat sits at its boot or arrived
/// through a portal.
/// </summary>
internal static class WorldSeatCameraResolver {
    /// <summary>One caller's persisted live-orbit compile cache: the compiled rig instance and the authored
    /// <see cref="WorldCameraMotion.Orbit"/>/yaw/pitch it was built from, so an unmoved orbit costs no recompile.
    /// One instance per (seat, rendered instance) pair a caller tracks independently.</summary>
    internal sealed class LiveOrbitCache {
        public ISdfCameraRig? Rig;
        public WorldCameraMotion.Orbit? Authored;
        public float Yaw;
        public float Pitch;
    }

    /// <summary>One caller's persisted <see cref="WorldCameraRig.SmoothRate"/> ease state: the exponentially-lagged
    /// eye/target and whether it has been seeded yet. One instance per (seat, rendered instance) pair.</summary>
    internal sealed class SmoothingState {
        public bool Seeded;
        public Vector3 Eye;
        public Vector3 Target;
    }

    /// <summary>Assembles one seat's complete control-feel policy from its two owners, per the lead ruling this
    /// type's own remarks cite: the world currently framing the seat (the boot world for a boot-anchored seat, the
    /// destination for a traveling one) owns rig structure — <see cref="WorldSeatLook.WorldAxes"/> and the pitch
    /// clamp; the seat's own live control feel (<see cref="WorldSeatFeel.Look"/> — its profile's, or the framing
    /// world's own when unclaimed) owns input preferences — pointer sensitivity, stick rate, inversion, and what
    /// arms the drag. The one
    /// place this split is made: a caller merges here rather than reading each field from whichever source happens
    /// to be at hand, so <c>WorldFrameSource.ResolveCamera</c> (a boot seat) and
    /// <see cref="AwaySeatSceneEmitter.ResolveChaseCamera"/> (a traveling seat) resolve the same shape from the same
    /// rule.</summary>
    /// <param name="structure">The currently-framing world's own authored <c>playerDefaults.seatLook</c> — supplies
    /// <see cref="WorldSeatLook.WorldAxes"/>, <see cref="WorldSeatLook.MinPitch"/>, and
    /// <see cref="WorldSeatLook.MaxPitch"/>.</param>
    /// <param name="preference">The seat's live control feel — supplies <see cref="WorldSeatLook.YawSensitivity"/>,
    /// <see cref="WorldSeatLook.PitchSensitivity"/>, <see cref="WorldSeatLook.StickLookRate"/>, <see cref="WorldSeatLook.InvertYaw"/>,
    /// <see cref="WorldSeatLook.InvertPitch"/>, and <see cref="WorldSeatLook.Arming"/>.</param>
    public static WorldSeatLook ResolveSeatLook(WorldSeatLook structure, WorldSeatLook preference) => new(
        YawSensitivity: preference.YawSensitivity,
        PitchSensitivity: preference.PitchSensitivity,
        InvertYaw: preference.InvertYaw,
        InvertPitch: preference.InvertPitch,
        MinPitch: structure.MinPitch,
        MaxPitch: structure.MaxPitch,
        Arming: preference.Arming,
        WorldAxes: structure.WorldAxes,
        StickLookRate: preference.StickLookRate
    );

    /// <summary>Recovers a body's heading as the yaw scalar <c>OrbitRig.Offset</c>'s convention expects (0 = +Z,
    /// positive toward +X), by reading where the orientation sends +Z. A pure-yaw body orientation (the ordinary
    /// upright-avatar case) recovers its exact heading; a body additionally pitched or rolled recovers an
    /// approximation — the same convention <see cref="WorldFrameSource"/> and <c>WorldEditorSession.SetLookToward</c>
    /// both already read this way.</summary>
    public static float BodyYaw(Quaternion orientation) {
        var behind = Vector3.Transform(value: Vector3.UnitZ, rotation: orientation);

        return MathF.Atan2(y: behind.X, x: behind.Z);
    }

    /// <summary>Resolves the rig to actually render through: the compiled plain chase for any authored motion other
    /// than <see cref="WorldCameraMotion.Orbit"/>, or — for an authored Orbit rig — a freshly-composed rig feeding
    /// the seat's live pointer/stick offset into the document's own orbit vocabulary (authored yaw/pitch + the live offset), rather
    /// than post-rotating a resolved eye/target. <paramref name="seatLookWorldAxes"/> selects what the composed yaw
    /// rides on top of: <see langword="false"/> adds the body's own yaw (the orbit rides the body's heading — turn,
    /// and the camera swings with you); <see langword="true"/> drops it, an absolute orbit independent of facing.
    /// Recompiles <paramref name="cache"/>'s rig only when the authored orbit instance or the composed yaw/pitch
    /// actually changed since the last call.</summary>
    /// <param name="authoredRig">The rendered instance's own authored seat rig (its <c>views.seatRig</c>).</param>
    /// <param name="compiledChase">The caller's already-compiled plain chase for <paramref name="authoredRig"/> —
    /// returned unchanged when the authored motion is not <see cref="WorldCameraMotion.Orbit"/>.</param>
    /// <param name="seatLookWorldAxes">The resolved control feel's <see cref="WorldSeatLook.WorldAxes"/>.</param>
    /// <param name="bodyOrientation">The traveler/seat body's resolved orientation.</param>
    /// <param name="liveYaw">The seat's live orbit yaw offset (<c>WorldCameraOrbit.Yaw</c>).</param>
    /// <param name="livePitch">The seat's live orbit pitch offset (<c>WorldCameraOrbit.Pitch</c>).</param>
    /// <param name="cache">This (seat, rendered instance) pair's persisted compile cache.</param>
    public static ISdfCameraRig ResolveChase(WorldCameraRig authoredRig, ISdfCameraRig compiledChase, bool seatLookWorldAxes, Quaternion bodyOrientation, float liveYaw, float livePitch, LiveOrbitCache cache) {
        if (authoredRig.Motion is not WorldCameraMotion.Orbit orbit) {
            return compiledChase;
        }

        var yaw = ((orbit.Yaw + liveYaw) + (seatLookWorldAxes ? 0f : BodyYaw(orientation: bodyOrientation)));
        var pitch = (orbit.Pitch + livePitch);

        if ((cache.Rig is { } cached) && ReferenceEquals(objA: cache.Authored, objB: orbit) && (cache.Yaw == yaw) && (cache.Pitch == pitch)) {
            return cached;
        }

        var rig = authoredRig with {
            Motion = orbit with { Yaw = yaw, Pitch = pitch },
        };

        cache.Authored = orbit;
        cache.Yaw = yaw;
        cache.Pitch = pitch;
        cache.Rig = WorldCameraRigCompiler.Compile(rig: rig);

        return cache.Rig;
    }

    /// <summary>Applies the rig-level <see cref="WorldCameraRig.SmoothRate"/> ease to a resolved eye/target:
    /// frame-rate-independent exponential low-pass, <c>alpha = 1 - e^(-rate * dt)</c>, seeded un-smoothed on the
    /// first call so a camera never flies in from zero. A zero rate, or <paramref name="isPlainChase"/> false (an
    /// override rig — the boot editor's drag/workbench/fly/orbit — is in force), passes <paramref name="eye"/>/
    /// <paramref name="target"/> through unchanged and resets the seed so smoothing restarts cleanly the next time
    /// the plain chase resumes.</summary>
    public static void Smooth(SmoothingState state, float smoothRate, bool isPlainChase, float deltaSeconds, ref Vector3 eye, ref Vector3 target) {
        if (!isPlainChase || (smoothRate <= 0f)) {
            state.Seeded = false;

            return;
        }

        if (!state.Seeded) {
            state.Eye = eye;
            state.Target = target;
            state.Seeded = true;
        } else {
            var alpha = (1f - MathF.Exp(x: (-smoothRate * MathF.Max(x: deltaSeconds, y: 0f))));

            state.Eye = Vector3.Lerp(value1: state.Eye, value2: eye, amount: alpha);
            state.Target = Vector3.Lerp(value1: state.Target, value2: target, amount: alpha);
        }

        eye = state.Eye;
        target = state.Target;
    }
}
