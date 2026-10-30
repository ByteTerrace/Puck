using System.Numerics;

namespace Puck.World.Client;

// Weighted endpoint sums can move identical floats by an ulp as alpha changes. A stopped body's attachments and
// shadow casters must remain exactly still, including through neighbour and session projections.
internal static class WorldPoseInterpolation {
    internal static Vector3 Position(Vector3 previous, Vector3 current, float alpha) => ((previous == current)
        ? current : Vector3.Lerp(value1: previous, value2: current, amount: alpha));

    internal static Quaternion Orientation(Quaternion previous, Quaternion current, float alpha) => ((previous == current)
        ? Quaternion.Normalize(value: current) : Quaternion.Lerp(quaternion1: previous, quaternion2: current, amount: alpha));
}
