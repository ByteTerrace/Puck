using System.Numerics;
using Puck.Commands;
using Puck.Maths;

namespace Puck.World.Client;

public sealed partial class WorldFramePresenter {
    // A view keeps its producer name across dresses, including while a layout temporarily stops showing it.
    private readonly List<string> m_viewProducerNames = [];

    private string ViewProducerName(int view) {
        while (m_viewProducerNames.Count <= view) {
            m_viewProducerNames.Add(item: WorldRootGraph.ProducerOf(view: m_viewProducerNames.Count));
        }
        return m_viewProducerNames[view];
    }

    /// <summary>Finds the surface a ray meets among this world's solid static placements, marched in fixed point out to
    /// the world's far distance measured from the camera the ray was cast through, so a pick through a window onto this
    /// world from another lands on the surface the window shows.</summary>
    /// <param name="ray">The ray, in this world's space.</param>
    /// <param name="from">The position of the camera the ray was cast through, which the far distance is measured from.</param>
    /// <param name="point">The point the ray meets, in this world's space, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the world's query field exists and the ray proves a surface within the far
    /// distance.</returns>
    public bool TrySurface(SourceRay ray, Vector3 from, out FixedVector3 point) {
        point = default;

        var reach = (FixedQ4816.FromDouble(value: WorldRenderFarDistance.Resolve(defaults: m_client.Definition.Render)) - (ray.Origin - FixedVector3.FromVector3(value: from)).Length);

        if (
            (m_client.StaticField is not { } field) ||
            (reach <= FixedQ4816.Zero) ||
            !field.Raycast(
                dir: ray.Direction,
                hit: out var hit,
                maxDist: reach,
                origin: FixedPosition.FromLocal(local: ray.Origin)
            ) ||
            (hit.Confidence != WorldQueryConfidence.Exact)
        ) {
            return false;
        }

        point = (hit.Point - FixedPosition.Zero);

        return true;
    }
}
