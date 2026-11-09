using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.SdfVm;

namespace Puck.World.Client;

// Occlusion as rate evidence (the bodies lever, RateOf): each rated registration asks every view that may show it or its
// shadow, through that view's sight (ISdfViewSight, the mesh pass's own pyramid test), two groups of boxes: its reach box
// (RateReach about its root) widened by a sweep, and the receivers its shadow can reach in each of the view's cascades
// that keeps it (SdfMeshBox.ShadowReach), every receiver being a camera sample of that view. A group is built once, from
// an anchor, and asked again unchanged while the anchor still holds the registration: the same instance and camera, the
// current reach sphere inside the anchor's, every cascade keeping it now one the anchor's shadow group covers, each such
// cascade's light within SightTurnRadians of the anchor's, its extent unchanged and its receivers' reach no wider. The sweep covers the reach's growth
// and the root's motion over the sight's latency, so an anchor outlives the latency it waits on. Every path here keeps
// demand on unknown evidence: no sight, no instance for a view, an unmeasured speed, a read of the pose, unknown cascades,
// or a group with no completed answer for its version and camera.
public sealed partial class WorldCreationInstances {
    // The angle, in radians, a cascade's light may turn while a shadow group built for it still holds; the group's boxes are
    // widened by the chord it sweeps (SdfMeshBox.ShadowReach).
    private const float SightTurnRadians = 0.01f;
    // The most boxes one cascade's receivers are cut into.
    private const int SightShadowBoxes = 8;
    // The most cascades of one view an anchor covers: a view's key light's two and its second light's one.
    private const int SightCascades = 3;
    // The frames a sweep assumes before any sight has measured its latency.
    private const int SightUnmeasuredFrames = 4;
    // The latencies a sweep covers the root's motion for, so an anchor still holds a moving root after its answer lands.
    private const float SightRootLatencies = 3f;
    // The sight groups' keys and versions, unique in the process.
    private static long SightKeys;
    private static long SightVersions;

    // The instance rendering each rated view (RateBy), and the evidence of what each hides.
    private readonly List<string?> m_rateInstances = [];
    private ISdfViewSight? m_rateSight;
    // The seconds the current pack advances presentation by, which a sweep counts the sight's latency in.
    private float m_rateFrameSeconds;
    private readonly SdfMeshBox[] m_sightCut = new SdfMeshBox[SightShadowBoxes];

    /// <summary>One shadow cascade of a view the pool's world is rendered through, as the frame just dressed fitted it.</summary>
    /// <param name="View">The index of the view whose receivers the cascade shadows.</param>
    /// <param name="Cull">The renderer's own caster cull of the cascade (<see cref="SdfMeshCull"/>).</param>
    /// <param name="Cascade">The cascade's light camera.</param>
    /// <param name="Reach">The receivers' reach in the cascade, in world units (<see cref="SdfMeshCull.ReachOf"/>).</param>
    public readonly record struct RateShadow(int View, SdfMeshCull Cull, SdfMeshShadowCamera Cascade, float Reach);

    // What every view's sight says of a registration.
    private enum Sight : byte {
        // A view may show it, or nothing answered.
        Seen,
        // Every view hides it, but a view may show a receiver of its shadow.
        ShadowOnly,
        // Every view hides it and every receiver its shadow can reach.
        Hidden,
    }

    // A registration's groups in one view: the anchor they were built from and the boxes each holds.
    private sealed class SightAnchor {
        public long Version;
        public string? Instance;
        public CameraSnapshot Camera;
        public Vector3 Center;
        public float Radius;
        // The view's cascades (by their order among the view's RateShadows) the shadow group covers, and each one's light
        // and extent when it was built.
        public int Cascades;
        public readonly Vector3[] Forward = new Vector3[SightCascades];
        public readonly float[] Extent = new float[SightCascades];
        public readonly float[] ShadowReach = new float[SightCascades];
        public readonly SdfMeshBox[] Reach = new SdfMeshBox[1];
        public SdfMeshBox[] Shadow = [];
        public int ShadowCount;
    }

    // What every view that may show a registration or its shadow says of it, with the most frames a view's sight took to
    // answer. Asks each such view's groups, rebuilding the ones whose anchor no longer holds the registration.
    private Sight SightOf(Registration live, Vector3 center, float radius, float placementScale, out int latency) {
        latency = -1;

        var speed = ((live.BodyIndex is not null) ? SdfUpdateRateTiers.BodyPartSpeed : (live.PartSpeed * placementScale));

        // A root that follows its body through dynamics draws where the follower stands, which may trail the body's
        // position or overshoot it by as far: the queried sphere holds both.
        if (live.HasRootDynamics) {
            radius += Vector3.Distance(value1: center, value2: live.FollowedPosition);
        }
        var frameSeconds = ((m_rateFrameSeconds > 0f) ? m_rateFrameSeconds : (1f / SdfUpdateRateTiers.EveryFrameHertz));
        var rootSpeed = ((live.SightPack == (m_packCount - 1L)) ? (Vector3.Distance(value1: center, value2: live.SightCenter) / frameSeconds) : float.PositiveInfinity);

        live.SightCenter = center;
        live.SightPack = m_packCount;

        if ((m_rateSight is not { } sight) || !m_rateShadowsKnown || (live.PoseReadPack >= (m_packCount - 1L)) || !float.IsFinite(f: speed) || (m_rateViews.Count == 0)) {
            return Sight.Seen;
        }
        if (live.SightAnchors.Length < m_rateViews.Count) {
            Array.Resize(array: ref live.SightAnchors, newSize: m_rateViews.Count);
        }

        var half = new Vector3(value: radius);
        var sphere = new SdfMeshBox(Center: center, HalfX: (Vector3.UnitX * radius), HalfY: (Vector3.UnitY * radius), HalfZ: (Vector3.UnitZ * radius));
        var bodySeen = false;
        var shadowSeen = false;

        for (var view = 0; (view < m_rateViews.Count); view++) {
            var camera = m_rateViews[view].Camera;
            var shows = camera.MaySee(center: center, halfX: (Vector3.UnitX * half.X), halfY: (Vector3.UnitY * half.Y), halfZ: (Vector3.UnitZ * half.Z), margin: WorldPortalVisibility.Margin);
            var cascades = KeepingCascades(view: view, sphere: in sphere);

            if (cascades < 0) {
                return Sight.Seen;
            }
            if (!shows && (cascades == 0)) {
                continue;
            }
            if ((view >= m_rateInstances.Count) || (m_rateInstances[view] is not { } instance)) {
                return Sight.Seen;
            }

            var anchor = (live.SightAnchors[view] ??= new SightAnchor());

            if (!Holds(anchor: anchor, camera: in camera, cascades: cascades, center: center, instance: instance, radius: radius, view: view)) {
                var frames = sight.LatencyFrames(instance: instance);
                var waited = ((Math.Max(val1: frames, val2: SightUnmeasuredFrames) + 1) * frameSeconds);
                var sweep = ((speed * waited) + (float.IsFinite(f: rootSpeed) ? (rootSpeed * (SightRootLatencies * waited)) : 0f));

                Anchor(anchor: anchor, camera: in camera, cascades: cascades, center: center, instance: instance, radius: (radius + sweep), view: view);
            }

            var key = (live.SightKey * 2L);
            var bodyHidden = (!shows || sight.Hidden(boxes: anchor.Reach, camera: in camera, instance: instance, key: key, version: anchor.Version));
            var shadowHidden = ((cascades == 0) || (anchor.ShadowCount == 0) || sight.Hidden(boxes: anchor.Shadow.AsSpan(start: 0, length: anchor.ShadowCount), camera: in camera, instance: instance, key: (key + 1L), version: anchor.Version));

            bodySeen |= !bodyHidden;
            shadowSeen |= !shadowHidden;
            latency = Math.Max(val1: latency, val2: sight.LatencyFrames(instance: instance));
        }

        return (bodySeen ? Sight.Seen : (shadowSeen ? Sight.ShadowOnly : Sight.Hidden));
    }
    // The cascades of a view whose caster cull keeps a sphere's box, a bit by each cascade's order among the view's; -1
    // when one past the most an anchor covers keeps it.
    private int KeepingCascades(int view, in SdfMeshBox sphere) {
        var cascades = 0;
        var order = 0;

        foreach (var shadow in m_rateShadows) {
            if (shadow.View != view) {
                continue;
            }
            if (shadow.Cull.Keeps(box: sphere)) {
                if (order >= SightCascades) {
                    return -1;
                }

                cascades |= (1 << order);
            }

            order++;
        }

        return cascades;
    }
    // Whether an anchor's groups still answer for a registration's reach sphere in a view.
    private bool Holds(SightAnchor anchor, in CameraSnapshot camera, int cascades, Vector3 center, string instance, float radius, int view) {
        if ((anchor.Version == 0L) || (anchor.Camera != camera) || !string.Equals(a: anchor.Instance, b: instance, comparisonType: StringComparison.Ordinal) || ((cascades & ~anchor.Cascades) != 0)) {
            return false;
        }

        // The sphere, not the reach cube, is what the shadow group was swept from (SdfMeshBox.ShadowReach holds the
        // receivers of any sphere inside it), and inside the sphere is inside the cube.
        if ((Vector3.Distance(value1: center, value2: anchor.Center) + radius) > anchor.Radius) {
            return false;
        }

        var turned = MathF.Cos(x: SightTurnRadians);
        var order = 0;

        foreach (var shadow in m_rateShadows) {
            if (shadow.View != view) {
                continue;
            }
            if (((anchor.Cascades & (1 << order)) != 0) && ((Vector3.Dot(vector1: shadow.Cascade.Forward, vector2: anchor.Forward[order]) < turned) || (shadow.Cascade.HalfHeight != anchor.Extent[order]) || (shadow.Reach > anchor.ShadowReach[order]))) {
                return false;
            }

            order++;
        }

        return true;
    }
    // Rebuilds an anchor's groups about a swept sphere: its reach box, and the receivers its shadow can reach in each
    // keeping cascade that the view's camera may show, under a new version.
    private void Anchor(SightAnchor anchor, in CameraSnapshot camera, int cascades, Vector3 center, string instance, float radius, int view) {
        anchor.Version = Interlocked.Increment(location: ref SightVersions);
        anchor.Instance = instance;
        anchor.Camera = camera;
        anchor.Center = center;
        anchor.Radius = radius;
        anchor.Cascades = cascades;
        anchor.Reach[0] = new SdfMeshBox(Center: center, HalfX: (Vector3.UnitX * radius), HalfY: (Vector3.UnitY * radius), HalfZ: (Vector3.UnitZ * radius));
        anchor.ShadowCount = 0;

        var order = 0;

        foreach (var shadow in m_rateShadows) {
            if (shadow.View != view) {
                continue;
            }
            if ((cascades & (1 << order)) != 0) {
                anchor.Forward[order] = shadow.Cascade.Forward;
                anchor.Extent[order] = shadow.Cascade.HalfHeight;
                anchor.ShadowReach[order] = shadow.Reach;

                var cut = SdfMeshBox.ShadowReach(boxes: m_sightCut, cascade: shadow.Cascade, center: center, radius: radius, reach: shadow.Reach, turn: SightTurnRadians);

                for (var index = 0; (index < cut); index++) {
                    var box = m_sightCut[index];

                    // A receiver is a sample the view's camera shows, so a box no part of which it may see holds none.
                    if (!camera.MaySee(center: box.Center, halfX: box.HalfX, halfY: box.HalfY, halfZ: box.HalfZ, margin: WorldPortalVisibility.Margin)) {
                        continue;
                    }
                    if (anchor.Shadow.Length == anchor.ShadowCount) {
                        Array.Resize(array: ref anchor.Shadow, newSize: Math.Max(val1: SightShadowBoxes, val2: (2 * anchor.Shadow.Length)));
                    }

                    anchor.Shadow[anchor.ShadowCount++] = box;
                }
            }

            order++;
        }
    }
}
