using System.Numerics;
using Puck.Maths;
using Puck.SdfVm.Views;

namespace Puck.World.Client;

/// <summary>Pure camera math shared by the seat-owned local and traveling view paths.</summary>
internal static class WorldSeatCameraResolver {
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
        : VectorFunctions.YawOf(attitude: bodyOrientation)))
    );
}
